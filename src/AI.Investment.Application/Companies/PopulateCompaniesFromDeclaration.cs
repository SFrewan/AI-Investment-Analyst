using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Companies;

namespace AI.Investment.Application.Companies;

/// <summary>
/// Creates companies from the sealed identity declaration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The declaration is the only input.</strong> Nothing here calls a provider, reads a price
/// or an observation, touches a security, or consumes an acquisition authorisation. A run is a
/// function of one sealed file and the rows that already exist.
/// </para>
/// <para>
/// <strong>It cannot create a security, and that is structural rather than promised.</strong> The
/// constructor names every collaborator this class can reach, and none of them can write one. A
/// company is a legal entity; whether it issued anything tradable is a separate question with its
/// own evidence and its own stage.
/// </para>
/// <para>
/// <strong>Idempotent by CIK.</strong> The CIK is the legal entity's external identifier and is
/// unique when present, so an existing row holding it is the same company. Run against G2's
/// post-state, the 77 companies it created come back as already existing rather than as duplicates.
/// </para>
/// </remarks>
public sealed class PopulateCompaniesFromDeclaration
{
    private readonly ISecurityIdentityDeclaration _declaration;
    private readonly ICompanyRepository _companies;
    private readonly IUnitOfWork _unitOfWork;

    public PopulateCompaniesFromDeclaration(
        ISecurityIdentityDeclaration declaration,
        ICompanyRepository companies,
        IUnitOfWork unitOfWork)
    {
        _declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    /// <summary>
    /// Runs the population and returns its census.
    /// </summary>
    /// <param name="nowUtc">
    /// Supplied by the caller, like every other creation instant here. It records when the row was
    /// written, never when the company was incorporated.
    /// </param>
    public async Task<CompanyPopulationResult> ExecuteAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var decisions = CompanyPopulationPolicy.Decide(_declaration.Members);
        var entries = new List<CompanyPopulationEntry>(decisions.Count);

        foreach (var decision in decisions)
        {
            if (!decision.IsEligible)
            {
                entries.Add(new CompanyPopulationEntry(
                    decision.Cik,
                    CompanyPopulationOutcome.Rejected,
                    decision.Rejection,
                    IssuerName: null));

                continue;
            }

            var cik = Cik.Create(decision.Cik);

            var existing = await _companies.FindByCikAsync(cik, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                entries.Add(new CompanyPopulationEntry(
                    decision.Cik,
                    CompanyPopulationOutcome.AlreadyExisting,
                    CompanyPopulationRejection.None,
                    existing.Name));

                continue;
            }

            // Sector, industry, country and description are left null. The declaration carries no
            // field for any of them, and manufacturing one - an industry from a SIC code, a country
            // from a state of incorporation - would put a value into evidence that no source states.
            _companies.Add(Company.Create(
                CompanyId.New(),
                decision.IssuerName!,
                nowUtc,
                cik: cik));

            entries.Add(new CompanyPopulationEntry(
                decision.Cik,
                CompanyPopulationOutcome.Created,
                CompanyPopulationRejection.None,
                decision.IssuerName));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Counted after the commit, from the repository, so the census reports what the database
        // holds rather than what the run assumed it would hold.
        var finalCompanies = await _companies.CountAsync(null, cancellationToken).ConfigureAwait(false);

        return new CompanyPopulationResult(
            _declaration.DeclarationId,
            _declaration.IdentityDigest,
            finalCompanies,
            entries);
    }
}
