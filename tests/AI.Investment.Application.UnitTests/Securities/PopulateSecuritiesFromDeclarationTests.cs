using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Securities;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Securities;

/// <summary>
/// What a population run writes, what it refuses, and what a second run does.
/// </summary>
/// <remarks>
/// The repositories are in-memory doubles, deliberately: these facts are about the run's decisions
/// and its idempotency, which are properties of the writer rather than of PostgreSQL. The database
/// half - that the schema holds what the writer produces and refuses a duplicate independently -
/// is proven separately against a real database.
/// </remarks>
public sealed class PopulateSecuritiesFromDeclarationTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A, B. The eligible member becomes a security carrying one vendor-symbol assertion.</summary>
    [Fact]
    public async Task An_eligible_member_becomes_a_security_with_its_vendor_identifier()
    {
        var securities = new InMemorySecurityRepository();
        var companies = new InMemoryCompanyRepository();

        var result = await Run(Declaration(Eligible()), securities, companies);

        Assert.Equal(1, result.SecuritiesCreated);
        Assert.Equal(1, result.VendorSymbolIdentifiersCreated);

        var security = Assert.Single(securities.Staged);
        var identifier = Assert.Single(security.Identifiers);

        Assert.Equal(SecurityIdentifierKind.VendorSymbol, identifier.Kind);
        Assert.Equal("AE.US", identifier.Value);
        Assert.Equal(new DateOnly(2021, 9, 1), identifier.ValidFrom);
        Assert.Equal(new DateOnly(2025, 2, 4), identifier.ValidTo);
        Assert.Equal("eodhd", identifier.SourceId.Value);
    }

    /// <summary>
    /// C. No ticker assertion is created, however plainly the issuer stated the symbol.
    /// </summary>
    /// <remarks>
    /// The declaration carries <c>AE</c> as an issuer-stated ticker and the run reads it, so this
    /// is not an absence of information - it is a refusal to turn undated information into a dated
    /// assertion. A <c>Ticker</c> row here would claim the security carried that symbol from the
    /// vendor series' first bar, which is the exact falsehood D5 found in the evidence.
    /// </remarks>
    [Fact]
    public async Task No_ticker_identifier_is_ever_created()
    {
        var securities = new InMemorySecurityRepository();

        var result = await Run(Declaration(Eligible()), securities, new InMemoryCompanyRepository());

        Assert.Equal(0, result.TickerIdentifiersCreated);
        Assert.DoesNotContain(
            securities.Staged.SelectMany(s => s.Identifiers),
            i => i.Kind == SecurityIdentifierKind.Ticker);

        Assert.Equal(
            new Dictionary<SecurityIdentifierKind, int> { [SecurityIdentifierKind.VendorSymbol] = 1 },
            result.IdentifiersCreatedByKind);
    }

    /// <summary>D, E, F, G. Every refused class is refused, and each keeps its own reason.</summary>
    [Fact]
    public async Task Refused_classes_create_nothing_and_keep_their_reasons()
    {
        var securities = new InMemorySecurityRepository();
        var companies = new InMemoryCompanyRepository();

        var result = await Run(
            Declaration(
                Eligible() with { Cik = "0000000002", IdentityClass = "issuer-identity-only" },
                // A vendor claim carries a vendor-sourced symbol. The policy reads the symbol's own
                // source rather than trusting the class label, so the two must agree here.
                Eligible() with
                {
                    Cik = "0000000003",
                    IdentityClass = "vendor-symbol-claim-only",
                    Symbol = new SymbolAssertion(
                        "CMRE-PE",
                        "eodhd-delisted-list-name-match",
                        IsIssuerStated: false,
                        IsAccepted: false,
                        SecurityTitleObserved: null,
                        ValidityIntervalEvidenced: false),
                },
                Eligible() with { Cik = "0000000004", Symbol = null, AmbiguityStatus = "no-candidate" },
                Eligible() with { Cik = "0000000005", AmbiguityStatus = "refused-multiple-candidates" }),
            securities,
            companies);

        Assert.Equal(0, result.SecuritiesCreated);
        Assert.Equal(4, result.Refused);
        Assert.Empty(securities.Staged);
        Assert.Empty(companies.Staged);

        Assert.Equal(
            new Dictionary<SecurityPopulationRefusal, int>
            {
                [SecurityPopulationRefusal.IssuerIdentityOnly] = 1,
                [SecurityPopulationRefusal.VendorSymbolClaimOnly] = 1,
                [SecurityPopulationRefusal.NoSecurityEvidence] = 1,
                [SecurityPopulationRefusal.UnresolvedAmbiguity] = 1,
            },
            result.RefusalsByReason);
    }

    /// <summary>H. The company is the legal entity; the security is the instrument.</summary>
    [Fact]
    public async Task The_company_carries_legal_identity_and_the_security_carries_none_of_it()
    {
        var companies = new InMemoryCompanyRepository();
        var securities = new InMemorySecurityRepository();

        await Run(Declaration(Eligible()), securities, companies);

        var company = Assert.Single(companies.Staged);
        var security = Assert.Single(securities.Staged);

        Assert.Equal("0000002178", company.Cik!.Value);
        Assert.Equal("ADAMS RESOURCES & ENERGY, INC.", company.Name);
        Assert.Equal(company.Id, security.CompanyId);

        // The narrowing D4 made, still holding: no symbol reached the company, and no CIK reached
        // the security.
        Assert.DoesNotContain(
            typeof(Company).GetProperties().Select(p => p.Name),
            n => n is "Ticker" or "Exchange");

        Assert.DoesNotContain(
            typeof(Security).GetProperties().Select(p => p.Name),
            n => n.Contains("Cik", StringComparison.OrdinalIgnoreCase));

        // And the fields no source evidences stay empty rather than being filled from the issuer.
        Assert.Null(company.Country);
        Assert.Null(company.Description);
        Assert.Null(company.Sector);
    }

    /// <summary>I. A second run over the same evidence writes nothing further.</summary>
    [Fact]
    public async Task A_second_run_creates_nothing_and_reports_what_was_already_present()
    {
        var securities = new InMemorySecurityRepository();
        var companies = new InMemoryCompanyRepository();
        var declaration = Declaration(Eligible());

        var first = await Run(declaration, securities, companies);

        // Commit what the first run staged, which is what SaveChangesAsync does for real. The
        // shared company double keeps staged and stored rows apart already; the security double
        // does the same, because a lookup that saw uncommitted rows would make idempotency look
        // like it worked when only the in-run guard had fired.
        securities.Commit();
        companies.Companies.AddRange(companies.Staged);
        companies.Staged.Clear();

        var second = await Run(declaration, securities, companies);

        Assert.Equal(1, first.SecuritiesCreated);
        Assert.Equal(0, second.SecuritiesCreated);
        Assert.Equal(1, second.AlreadyPresent);
        Assert.Empty(securities.Staged);
        Assert.Single(securities.Committed);
    }

    /// <summary>
    /// J. One declaration naming the same vendor key twice creates one security, not two.
    /// </summary>
    /// <remarks>
    /// The sealed declaration does not do this - no symbol in it maps to two CIKs - but within a
    /// single unit of work nothing has reached the database yet, so the unique index has not been
    /// consulted and would not catch it until the commit failed the whole run.
    /// </remarks>
    [Fact]
    public async Task A_repeated_vendor_symbol_within_one_run_is_not_created_twice()
    {
        var securities = new InMemorySecurityRepository();

        var result = await Run(
            Declaration(Eligible(), Eligible() with { Cik = "0000000009" }),
            securities,
            new InMemoryCompanyRepository());

        Assert.Equal(1, result.SecuritiesCreated);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Single(securities.Staged);
    }

    /// <summary>
    /// One company issuing two securities gets one company row and two securities.
    /// </summary>
    /// <remarks>
    /// The architecture says a company may issue several instruments, and the filtered unique index
    /// on <c>cik</c> would refuse the whole run if the second security staged a second company.
    /// </remarks>
    [Fact]
    public async Task One_issuer_with_two_instruments_yields_one_company_and_two_securities()
    {
        var securities = new InMemorySecurityRepository();
        var companies = new InMemoryCompanyRepository();

        var result = await Run(
            Declaration(
                Eligible(),
                Eligible() with
                {
                    VendorSeries = new VendorSeriesAssertion(
                        "AE-PA.US", "eodhd", new DateOnly(2021, 9, 1), new DateOnly(2025, 2, 4)),
                }),
            securities,
            companies);

        Assert.Equal(2, result.SecuritiesCreated);
        Assert.Equal(2, securities.Staged.Count);
        Assert.Single(companies.Staged);
        Assert.Single(securities.Staged.Select(s => s.CompanyId).Distinct());
    }

    /// <summary>K, L. The census is derived from the run and names the evidence behind it.</summary>
    [Fact]
    public async Task The_census_is_derived_and_binds_to_the_declaration_that_produced_it()
    {
        var result = await Run(
            Declaration(Eligible(), Eligible() with { Cik = "0000000002", VendorSeries = null }),
            new InMemorySecurityRepository(),
            new InMemoryCompanyRepository());

        Assert.Equal("security-identity-sample400-2021-2026", result.DeclarationId);
        Assert.Equal("3c26008918782eb703ffadb1b3fbf1566a10be51fb7b9056bb535b9235216f7d", result.IdentityDigest);

        // Totals and entries cannot disagree, because the totals are counted from the entries.
        Assert.Equal(result.MembersRead, result.Entries.Count);
        Assert.Equal(
            result.MembersRead,
            result.SecuritiesCreated + result.AlreadyPresent + result.Refused);

        Assert.Equal(
            ["0000002178", "0000000002"],
            result.Entries.Select(e => e.Cik));
    }

    /// <summary>M, N. The run reaches no provider and attaches no corporate action.</summary>
    /// <remarks>
    /// Asserted structurally rather than by watching for a call: the writer's constructor names
    /// every collaborator it can possibly use, and none of them is a provider, a connector, an
    /// acquisition authorisation or a corporate action.
    /// </remarks>
    [Fact]
    public void The_writer_can_reach_no_provider_and_no_corporate_action()
    {
        var dependencies = typeof(PopulateSecuritiesFromDeclaration)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.Equal(
            ["ISecurityIdentityDeclaration", "ISecurityRepository", "ICompanyRepository", "IUnitOfWork"],
            dependencies);

        Assert.DoesNotContain(
            dependencies,
            d => d.Contains("Provider", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Connector", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Gateway", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                || d.Contains("CorporateAction", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Observation", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<SecurityPopulationResult> Run(
        ISecurityIdentityDeclaration declaration,
        InMemorySecurityRepository securities,
        InMemoryCompanyRepository companies) =>
        new PopulateSecuritiesFromDeclaration(
            declaration, securities, companies, new CountingUnitOfWork()).ExecuteAsync(Now);

    private static StubDeclaration Declaration(params SecurityIdentityEvidence[] members) =>
        new(members);

    private static SecurityIdentityEvidence Eligible() =>
        new(
            "0000002178",
            "ADAMS RESOURCES & ENERGY, INC.",
            "issuer-stated-security-identity",
            "sec-filing-recovered",
            "none",
            new SymbolAssertion(
                "AE", "issuer-filing-cover-page", true, true, "equity", ValidityIntervalEvidenced: false),
            new VendorSeriesAssertion(
                "AE.US", "eodhd", new DateOnly(2021, 9, 1), new DateOnly(2025, 2, 4)));

    private sealed class StubDeclaration : ISecurityIdentityDeclaration
    {
        public StubDeclaration(IReadOnlyList<SecurityIdentityEvidence> members) => Members = members;

        public string DeclarationId => "security-identity-sample400-2021-2026";

        public string EvidenceBaseFingerprint => "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

        public string IdentityDigest =>
            "3c26008918782eb703ffadb1b3fbf1566a10be51fb7b9056bb535b9235216f7d";

        public IReadOnlyList<SecurityIdentityEvidence> Members { get; }
    }

    /// <summary>
    /// Staging and committing kept apart, because idempotency depends on the difference.
    /// </summary>
    private sealed class InMemorySecurityRepository : ISecurityRepository
    {
        public List<Security> Staged { get; } = [];

        public List<Security> Committed { get; } = [];

        public void Commit()
        {
            Committed.AddRange(Staged);
            Staged.Clear();
        }

        public Task<Security?> GetByIdAsync(SecurityId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.FirstOrDefault(s => s.Id == id));

        public Task<Security?> FindByIdentifierAsync(
            SecurityIdentifierKind kind,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.FirstOrDefault(
                s => s.Identifiers.Any(i => i.Kind == kind && i.Value == value)));

        // The resolution members, implemented against the committed rows rather than thrown from.
        // A double that threw would make the population tests depend on nothing calling them, which
        // is a weaker guarantee than behaving correctly if something does.
        public Task<IReadOnlyList<SecurityId>> FindByIdentifierAsAtAsync(
            SecurityIdentifierKind kind,
            string value,
            SourceId source,
            DateOnly asOf,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecurityId>>(
                Committed
                    .Where(s => s.Identifiers.Any(i =>
                        i.Kind == kind && i.Value == value && i.SourceId == source && i.CoversDate(asOf)))
                    .Select(s => s.Id)
                    .OrderBy(id => id.Value)
                    .ToList());

        public Task<(int OfKind, int OfKindAndValue)> CountIdentifierAssertionsAsync(
            SecurityIdentifierKind kind,
            string value,
            CancellationToken cancellationToken = default)
        {
            var assertions = Committed.SelectMany(s => s.Identifiers).ToList();

            return Task.FromResult((
                assertions.Count(i => i.Kind == kind),
                assertions.Count(i => i.Kind == kind && i.Value == value)));
        }

        public Task<bool> AnyIdentifierFromSourceAsync(
            SecurityIdentifierKind kind,
            string value,
            SourceId source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Committed.SelectMany(s => s.Identifiers).Any(
                i => i.Kind == kind && i.Value == value && i.SourceId == source));

        public void Add(Security security) => Staged.Add(security);
    }
}
