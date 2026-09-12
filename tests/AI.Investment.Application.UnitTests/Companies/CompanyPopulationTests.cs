using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Companies;
using AI.Investment.Application.Securities;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Securities;
using Xunit;

namespace AI.Investment.Application.UnitTests.Companies;

/// <summary>
/// Which members of the sealed declaration are legal entities, and what a population run writes.
/// </summary>
/// <remarks>
/// The eligibility rule is three predicates and no more, so most of these facts are about what is
/// deliberately <em>not</em> a predicate. A company that stopped existing because nobody agreed on
/// its ticker would be anti-pattern 15 arriving from the company side, and these are what stop it.
/// </remarks>
public sealed class CompanyPopulationTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    // ---- eligibility ----------------------------------------------------------------------

    /// <summary>The minimum sufficient evidence: a canonical CIK and an issuer name.</summary>
    [Fact]
    public void A_member_with_a_canonical_cik_and_a_name_is_eligible()
    {
        var decision = Assert.Single(CompanyPopulationPolicy.Decide([Member()]));

        Assert.True(decision.IsEligible);
        Assert.Equal(CompanyPopulationRejection.None, decision.Rejection);
        Assert.Equal("ADAMS RESOURCES & ENERGY, INC.", decision.IssuerName);
    }

    /// <summary>D. Ten digits exactly, and a leading zero is significant rather than noise.</summary>
    [Theory]
    [InlineData("0000002178", true)]
    [InlineData("0001503584", true)]
    [InlineData("1503584", false)]      // unpadded - would become canonical only by inventing zeros
    [InlineData("00015035840", false)]  // eleven
    [InlineData("000150358A", false)]
    [InlineData("", false)]
    public void The_cik_must_be_exactly_ten_digits(string cik, bool eligible)
    {
        var decision = Assert.Single(CompanyPopulationPolicy.Decide([Member() with { Cik = cik }]));

        Assert.Equal(eligible, decision.IsEligible);

        if (!eligible)
        {
            Assert.Equal(CompanyPopulationRejection.InvalidCik, decision.Rejection);
        }
    }

    /// <summary>No name, no company. Nothing is substituted for one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_member_without_an_issuer_name_is_rejected(string? name)
    {
        var decision = Assert.Single(CompanyPopulationPolicy.Decide([Member() with { IssuerName = name }]));

        Assert.False(decision.IsEligible);
        Assert.Equal(CompanyPopulationRejection.MissingIssuerName, decision.Rejection);
        Assert.Null(decision.IssuerName);
    }

    /// <summary>E. One CIK twice in one declaration is a contradiction, not a merge.</summary>
    [Fact]
    public void A_cik_repeated_in_the_source_is_rejected_the_second_time()
    {
        var decisions = CompanyPopulationPolicy.Decide([Member(), Member() with { IssuerName = "Other Name Inc." }]);

        Assert.True(decisions[0].IsEligible);
        Assert.False(decisions[1].IsEligible);
        Assert.Equal(CompanyPopulationRejection.DuplicateCikInSource, decisions[1].Rejection);
    }

    /// <summary>
    /// H, I, J, K. None of the instrument-shaped evidence is a predicate.
    /// </summary>
    /// <remarks>
    /// Costamare is the member this exists for: its identity class is a vendor claim, its symbol
    /// was never accepted, it holds no vendor series, and it is a legal entity all the same.
    /// </remarks>
    [Fact]
    public void No_instrument_evidence_affects_company_eligibility()
    {
        var stripped = Member() with
        {
            Cik = "0001503584",
            IssuerName = "COSTAMARE INC.",
            IdentityClass = "vendor-symbol-claim-only",
            ResolutionStatus = "eodhd-only-provisional",
            AmbiguityStatus = "none",
            Symbol = new SymbolAssertion(
                "CMRE-PE", "eodhd-delisted-list-name-match", false, false, null, false),
            VendorSeries = null,
        };

        Assert.True(Assert.Single(CompanyPopulationPolicy.Decide([stripped])).IsEligible);

        // And with every instrument field removed outright, which no declaration produces but which
        // proves the rule reads none of them.
        var bare = stripped with { Symbol = null, VendorSeries = null, IdentityClass = "anything" };

        Assert.True(Assert.Single(CompanyPopulationPolicy.Decide([bare])).IsEligible);
    }

    /// <summary>An ambiguous symbol does not make the issuer ambiguous.</summary>
    [Fact]
    public void A_member_whose_symbol_is_ambiguous_is_still_a_company()
    {
        var ambiguous = Member() with
        {
            IdentityClass = "unresolved-ambiguous",
            ResolutionStatus = "ambiguous",
            AmbiguityStatus = "refused-multiple-candidates",
            Symbol = null,
        };

        Assert.True(Assert.Single(CompanyPopulationPolicy.Decide([ambiguous])).IsEligible);
    }

    // ---- the writer -----------------------------------------------------------------------

    /// <summary>F, G. The name comes from the declaration; unsupported fields stay null.</summary>
    [Fact]
    public async Task A_created_company_carries_the_declared_name_a_cik_and_nothing_else()
    {
        var companies = new InMemoryCompanyRepository();

        await Run(Declaration(Member()), companies);

        var company = Assert.Single(companies.Staged);

        Assert.Equal("ADAMS RESOURCES & ENERGY, INC.", company.Name);
        Assert.Equal("0000002178", company.Cik!.Value);

        Assert.Null(company.Sector);
        Assert.Null(company.Industry);
        Assert.Null(company.Country);
        Assert.Null(company.Description);

        // D4, still holding: there is nowhere on a company to put a symbol even if one were wanted.
        Assert.DoesNotContain(
            typeof(Company).GetProperties().Select(p => p.Name),
            n => n is "Ticker" or "Exchange");
    }

    /// <summary>D. The leading zeros survive the round trip through the value object.</summary>
    [Fact]
    public async Task Leading_zeros_are_preserved_exactly()
    {
        var companies = new InMemoryCompanyRepository();

        await Run(Declaration(Member() with { Cik = "0001503584", IssuerName = "COSTAMARE INC." }), companies);

        Assert.Equal("0001503584", Assert.Single(companies.Staged).Cik!.Value);
    }

    /// <summary>L. A second run over the same evidence writes nothing further.</summary>
    [Fact]
    public async Task A_second_run_creates_nothing_and_reports_what_already_existed()
    {
        var companies = new InMemoryCompanyRepository();
        var declaration = Declaration(Member(), Member() with { Cik = "0001503584", IssuerName = "COSTAMARE INC." });

        var first = await Run(declaration, companies);

        companies.Companies.AddRange(companies.Staged);
        companies.Staged.Clear();

        var second = await Run(declaration, companies);

        Assert.Equal(2, first.Created);
        Assert.Equal(0, first.AlreadyExisting);

        Assert.Equal(0, second.Created);
        Assert.Equal(2, second.AlreadyExisting);
        Assert.Empty(companies.Staged);
    }

    /// <summary>M, N. The census is derived from the run and names the evidence behind it.</summary>
    [Fact]
    public async Task The_census_is_derived_and_binds_to_the_declaration()
    {
        var result = await Run(
            Declaration(
                Member(),
                Member() with { Cik = "not-a-cik" },
                Member() with { Cik = "0001503584", IssuerName = null }),
            new InMemoryCompanyRepository());

        Assert.Equal("security-identity-sample400-2021-2026", result.DeclarationId);
        Assert.Equal("3c26008918782eb703ffadb1b3fbf1566a10be51fb7b9056bb535b9235216f7d", result.IdentityDigest);

        Assert.Equal(3, result.MembersRead);
        Assert.Equal(1, result.Eligible);
        Assert.Equal(2, result.Rejected);
        Assert.Equal(1, result.InvalidCik);
        Assert.Equal(1, result.MissingName);
        Assert.Equal(0, result.DuplicateInput);

        // Totals and entries cannot disagree, because the totals are counted from the entries.
        Assert.Equal(result.MembersRead, result.Created + result.AlreadyExisting + result.Rejected);
        Assert.Equal(result.Rejected, result.RejectionsByReason.Values.Sum());
    }

    /// <summary>
    /// O, P, Q, R, S. The writer can reach no provider, and cannot create a security at all.
    /// </summary>
    /// <remarks>
    /// Asserted structurally rather than by watching for a call: the constructor names every
    /// collaborator this class can possibly use, and none of them writes a security, an identifier,
    /// a listing, a venue or a corporate action.
    /// </remarks>
    [Fact]
    public void The_writer_can_reach_nothing_that_creates_a_security_or_calls_a_provider()
    {
        var dependencies = typeof(PopulateCompaniesFromDeclaration)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.Equal(
            ["ISecurityIdentityDeclaration", "ICompanyRepository", "IUnitOfWork"],
            dependencies);

        Assert.DoesNotContain(dependencies, d => d.Contains("SecurityRepository", StringComparison.Ordinal));

        Assert.DoesNotContain(
            dependencies,
            d => d.Contains("Provider", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Connector", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Gateway", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                || d.Contains("CorporateAction", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Listing", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Venue", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Observation", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<CompanyPopulationResult> Run(
        ISecurityIdentityDeclaration declaration,
        InMemoryCompanyRepository companies) =>
        new PopulateCompaniesFromDeclaration(declaration, companies, new CountingUnitOfWork())
            .ExecuteAsync(Now);

    private static StubDeclaration Declaration(params SecurityIdentityEvidence[] members) =>
        new(members);

    private static SecurityIdentityEvidence Member() =>
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
}
