using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Securities;

/// <summary>
/// Creates securities and their identifier assertions from the sealed identity declaration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The declaration is the only input.</strong> Nothing here calls a provider, reads a price,
/// consults an observation or consumes an acquisition authorisation. A run is a function of one
/// sealed file and the rows that already exist, which is what makes it reproducible.
/// </para>
/// <para>
/// <strong>Idempotent by lookup, not by hope.</strong> Before creating anything the run asks whether
/// an identifier of that kind and value already exists; if it does, the security it belongs to
/// already exists and nothing is written. The database refuses a duplicate independently, because
/// a uniqueness rule enforced only in application code is enforced only until the next caller.
/// </para>
/// <para>
/// <strong>What it will not do.</strong> It creates no ticker assertion, because the declaration
/// evidences no interval for one and <c>SecurityIdentifier</c> requires a <c>ValidFrom</c>; it
/// resolves no ambiguity; it attaches no corporate action; and it never falls back to a company
/// name, a CIK or an observation symbol when the evidence is silent.
/// </para>
/// </remarks>
public sealed class PopulateSecuritiesFromDeclaration
{
    private readonly ISecurityIdentityDeclaration _declaration;
    private readonly ISecurityRepository _securities;
    private readonly ICompanyRepository _companies;
    private readonly IUnitOfWork _unitOfWork;

    public PopulateSecuritiesFromDeclaration(
        ISecurityIdentityDeclaration declaration,
        ISecurityRepository securities,
        ICompanyRepository companies,
        IUnitOfWork unitOfWork)
    {
        _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _securities = securities ?? throw new ArgumentNullException(nameof(securities));
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    /// <summary>
    /// Runs the population and returns its census.
    /// </summary>
    /// <param name="nowUtc">
    /// Supplied by the caller, like every other creation instant in this codebase. It records when
    /// the row was written here, never when the instrument was issued.
    /// </param>
    public async Task<SecurityPopulationResult> ExecuteAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<SecurityPopulationEntry>(_declaration.Members.Count);

        // Guards against one declaration naming the same vendor key twice. The sealed declaration
        // does not, but the run must not depend on that: within a single unit of work the database
        // constraint has not been consulted yet, so nothing else would catch it.
        var claimedInThisRun = new HashSet<string>(StringComparer.Ordinal);

        // One company may issue several securities - the architecture says so explicitly, and it is
        // why Security carries a CompanyId rather than Company carrying a symbol. A company staged
        // for the first of them is not queryable until the unit of work commits, so without this
        // the second would stage a duplicate and the filtered unique index on cik would refuse the
        // whole run.
        var companiesStagedInThisRun = new Dictionary<string, Company>(StringComparer.Ordinal);

        foreach (var member in _declaration.Members)
        {
            var decision = SecurityPopulationPolicy.Decide(member);

            if (!decision.IsEligible)
            {
                entries.Add(new SecurityPopulationEntry(
                    member.Cik,
                    SecurityPopulationOutcome.Refused,
                    decision.Refusal,
                    VendorSymbol: null));

                continue;
            }

            var identifier = decision.VendorSymbol!;

            var existing = await _securities
                .FindByIdentifierAsync(SecurityIdentifierKind.VendorSymbol, identifier.Value, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null || !claimedInThisRun.Add(identifier.Value))
            {
                entries.Add(new SecurityPopulationEntry(
                    member.Cik,
                    SecurityPopulationOutcome.AlreadyPresent,
                    SecurityPopulationRefusal.None,
                    identifier.Value));

                continue;
            }

            var company = await ResolveCompanyAsync(member, companiesStagedInThisRun, nowUtc, cancellationToken)
                .ConfigureAwait(false);

            var security = Security.Create(SecurityId.New(), company.Id, nowUtc);

            security.AssertIdentifier(SecurityIdentifier.Assert(
                SecurityIdentifierKind.VendorSymbol,
                identifier.Value,
                identifier.FirstBarDate,
                identifier.LastBarDate,
                SourceId.Create(identifier.SourceAuthority)));

            _securities.Add(security);

            entries.Add(new SecurityPopulationEntry(
                member.Cik,
                SecurityPopulationOutcome.Created,
                SecurityPopulationRefusal.None,
                identifier.Value,
                SecurityIdentifierKind.VendorSymbol));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SecurityPopulationResult(
            _declaration.DeclarationId,
            _declaration.IdentityDigest,
            entries);
    }

    /// <summary>
    /// The issuer this security belongs to, created from the declaration when it is not yet held.
    /// </summary>
    /// <remarks>
    /// Only the companies these securities need, and only the two fields the declaration evidences
    /// - the legal name and the CIK. Sector, industry, country and description stay null because no
    /// source for them is held, and populating the remaining members of the sealed universe is a
    /// separate stage with its own gate.
    /// </remarks>
    private async Task<Company> ResolveCompanyAsync(
        SecurityIdentityEvidence member,
        Dictionary<string, Company> stagedInThisRun,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (stagedInThisRun.TryGetValue(member.Cik, out var staged))
        {
            return staged;
        }

        var cik = Cik.Create(member.Cik);

        var existing = await _companies.FindByCikAsync(cik, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var company = Company.Create(
            CompanyId.New(),
            member.IssuerName ?? throw new InvalidOperationException(
                $"The declaration accepted a security identity for CIK {member.Cik} without naming " +
                "the issuer. A company cannot be created without a name, and inventing one here " +
                "would put a fabricated legal identity behind a real instrument."),
            nowUtc,
            cik: cik);

        _companies.Add(company);
        stagedInThisRun.Add(member.Cik, company);

        return company;
    }
}
