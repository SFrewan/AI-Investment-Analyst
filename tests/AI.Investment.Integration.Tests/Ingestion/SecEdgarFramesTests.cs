using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The cross-sectional EDGAR capability: what it can reach, and what it must never be mistaken for.
/// </summary>
/// <remarks>
/// <para>
/// A frame is the first request this platform makes whose subject is a <em>period</em> rather than
/// a thing. That is a new shape in a connector whose every other path is built from a CIK, and the
/// failures it invites are quiet ones: a period routed to a company endpoint returns somebody's
/// accounts, and a frame read by the company-facts normaliser would produce thousands of facts
/// stamped with a publication instant nobody published at.
/// </para>
/// <para>
/// So the tests below are mostly about things not happening.
/// </para>
/// </remarks>
public sealed class SecEdgarFramesTests
{
    private const string Frame = "us-gaap/Assets/USD/CY2021Q3I";

    private static readonly DateTime Now = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    // ---- endpoint construction ---------------------------------------------------------------

    [Fact]
    public void A_frame_identifier_becomes_the_documented_path()
    {
        var frame = SecEdgarEndpoints.ParseFrame(Frame);

        Assert.NotNull(frame);
        Assert.Equal("api/xbrl/frames/us-gaap/Assets/USD/CY2021Q3I.json", SecEdgarEndpoints.Frames(frame));
    }

    [Fact]
    public void A_frame_identifier_round_trips_through_its_own_text() =>
        Assert.Equal(Frame, SecEdgarEndpoints.ParseFrame(Frame)!.ToString());

    [Theory]
    [InlineData("us-gaap/Assets/USD/CY2021Q3I", "Assets", "USD", "CY2021Q3I")]
    [InlineData("us-gaap/Revenues/USD/CY2022Q1", "Revenues", "USD", "CY2022Q1")]
    [InlineData("us-gaap/NetIncomeLoss/USD/CY2024", "NetIncomeLoss", "USD", "CY2024")]
    public void The_segments_are_read_in_the_documented_order(
        string identifier,
        string concept,
        string unit,
        string period)
    {
        var frame = SecEdgarEndpoints.ParseFrame(identifier);

        Assert.NotNull(frame);
        Assert.Equal(SecEdgarEndpoints.Taxonomy, frame.Taxonomy);
        Assert.Equal(concept, frame.Concept);
        Assert.Equal(unit, frame.Unit);
        Assert.Equal(period, frame.Period);
    }

    // ---- the security boundary ----------------------------------------------------------------

    /// <summary>
    /// Nothing that could reach a different endpoint is accepted as a period.
    /// </summary>
    /// <remarks>
    /// Every segment lands in a URL path. Refusing rather than escaping is the same choice the
    /// EODHD connector makes about tickers, and for the same reason: escaping turns a malformed
    /// identifier into a valid request for something else, and refusing turns it into a recorded
    /// failure.
    /// </remarks>
    [Theory]
    [InlineData("us-gaap/../../submissions/CIK0000320193/USD/CY2021Q3I")]
    [InlineData("us-gaap/Assets/USD/..")]
    [InlineData("us-gaap/Assets/USD/CY2021Q3I.json")]
    [InlineData("us-gaap/Assets/USD/CY2021Q3I?x=1")]
    [InlineData("us-gaap/Assets/USD/CY 2021")]
    [InlineData("us-gaap/Assets/USD/CY2021%2F")]
    [InlineData("us-gaap/Assets/USD/CY2021Q3I/extra")]
    [InlineData("us-gaap/Assets/USD")]
    [InlineData("us-gaap//USD/CY2021Q3I")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_identifier_that_is_not_plainly_a_frame_is_refused(string? identifier) =>
        Assert.Null(SecEdgarEndpoints.ParseFrame(identifier));

    /// <summary>Another taxonomy is a request for facts nothing here was written against.</summary>
    [Theory]
    [InlineData("ifrsfull/Assets/USD/CY2021Q3I")]
    [InlineData("dei/EntityCommonStockSharesOutstanding/shares/CY2021Q3I")]
    public void Only_the_declared_taxonomy_is_accepted(string identifier) =>
        Assert.Null(SecEdgarEndpoints.ParseFrame(identifier));

    /// <summary>
    /// The hyphen is allowed, and that is load-bearing rather than lax.
    /// </summary>
    /// <remarks>
    /// The first version of the rule allowed letters and digits only and rejected every real
    /// frame, because the taxonomy is spelled <c>us-gaap</c>. These pin the shape of the boundary
    /// so it cannot be tightened back into uselessness or loosened into a path escape.
    /// </remarks>
    [Theory]
    [InlineData("us-gaap/Assets/USD/CY2021Q3I")]
    [InlineData("us-gaap/EarningsPerShareDiluted/USD-per-shares/CY2022Q1")]
    public void A_hyphen_is_part_of_a_legitimate_segment(string identifier) =>
        Assert.NotNull(SecEdgarEndpoints.ParseFrame(identifier));

    [Theory]
    [InlineData("us-gaap/Assets/USD/-")]
    [InlineData("us-gaap/Assets/-/CY2021Q3I")]
    public void A_segment_of_punctuation_alone_names_nothing(string identifier) =>
        Assert.Null(SecEdgarEndpoints.ParseFrame(identifier));

    [Fact]
    public void An_absurdly_long_segment_is_refused() =>
        Assert.Null(SecEdgarEndpoints.ParseFrame(
            "us-gaap/" + new string('A', SecEdgarEndpoints.MaxFrameSegmentLength + 1) + "/USD/CY2021Q3I"));

    // ---- request classification ---------------------------------------------------------------

    /// <summary>A CIK is not a frame, so a company subject cannot become a market-wide request.</summary>
    [Theory]
    [InlineData("0000320193")]
    [InlineData("CIK0000320193")]
    [InlineData("320193")]
    public void A_company_identifier_is_not_a_frame(string cik) =>
        Assert.Null(SecEdgarEndpoints.ParseFrame(cik));

    /// <summary>And a frame is not a CIK, so it cannot become a company request.</summary>
    [Fact]
    public void A_frame_identifier_is_not_a_cik() =>
        Assert.Null(SecEdgarEndpoints.NormaliseCik(Frame));

    /// <summary>
    /// The market-wide category resolves to no company path, whatever CIK is offered.
    /// </summary>
    /// <remarks>
    /// The check that stops a period subject silently returning one firm's accounts and being
    /// archived as a cross-section of the market.
    /// </remarks>
    [Fact]
    public void The_market_wide_category_has_no_company_endpoint() =>
        Assert.Null(SecEdgarEndpoints.ForCategory(
            DataCategory.MarketWideDisclosure,
            "0000320193"));

    [Theory]
    [InlineData(DataCategory.FinancialStatements)]
    [InlineData(DataCategory.RegulatoryFilings)]
    [InlineData(DataCategory.CompanyProfile)]
    [InlineData(DataCategory.EarningsDisclosure)]
    public void The_company_categories_still_resolve_exactly_as_before(DataCategory category) =>
        Assert.NotNull(SecEdgarEndpoints.ForCategory(category, "0000320193"));

    // ---- capability authorisation --------------------------------------------------------------

    [Fact]
    public void The_connector_declares_the_market_wide_category() =>
        Assert.Contains(DataCategory.MarketWideDisclosure, Provider().Capabilities.Categories);

    [Fact]
    public void The_connector_declares_the_period_subject_kind() =>
        Assert.Contains(
            SecEdgarEndpoints.PeriodSubjectKind,
            Provider().Capabilities.SubjectKinds,
            StringComparer.Ordinal);

    /// <summary>The company subject kind is still declared; nothing was replaced.</summary>
    [Fact]
    public void The_company_subject_kind_is_untouched() =>
        Assert.Contains("Company", Provider().Capabilities.SubjectKinds, StringComparer.Ordinal);

    [Fact]
    public void The_registry_entry_declares_the_market_wide_category() =>
        Assert.Contains(DataCategory.MarketWideDisclosure, Definition().Categories);

    /// <summary>
    /// The source is admissible for the new category once activated, by the unchanged rules.
    /// </summary>
    [Fact]
    public void The_source_is_admissible_for_market_wide_disclosure_once_activated()
    {
        var source = Definition();
        source.Activate(Now);

        Assert.True(SourceAdmission
            .Evaluate(source, DataCategory.MarketWideDisclosure, Region.UnitedStates)
            .IsAdmitted);
    }

    /// <summary>Registered inactive, like everything else. A new category activates nothing.</summary>
    [Fact]
    public void The_definition_is_still_registered_inactive() =>
        Assert.False(Definition().IsActive);

    // ---- normalisation ------------------------------------------------------------------------

    /// <summary>
    /// A frame is never read by the company-facts normaliser.
    /// </summary>
    /// <remarks>
    /// The failure this exists for: thousands of rows becoming observations stamped with a
    /// publication instant nobody published at, because a frame has no <c>filed</c> date - it is
    /// EDGAR's own assembly of whatever each company most recently reported for the period.
    /// </remarks>
    [Fact]
    public void The_company_facts_normaliser_refuses_a_market_wide_payload() =>
        Assert.False(new SecEdgarCompanyFactsNormalizer()
            .CanNormalize(SecEdgarProvider.Id, DataCategory.MarketWideDisclosure));

    /// <summary>And the frames normaliser refuses a company payload, in the other direction.</summary>
    [Theory]
    [InlineData(DataCategory.FinancialStatements)]
    [InlineData(DataCategory.RegulatoryFilings)]
    [InlineData(DataCategory.CompanyProfile)]
    [InlineData(DataCategory.MarketPrices)]
    public void The_frames_normaliser_refuses_everything_else(DataCategory category) =>
        Assert.False(new SecEdgarFramesNormalizer().CanNormalize(SecEdgarProvider.Id, category));

    [Fact]
    public void The_frames_normaliser_claims_only_edgar() =>
        Assert.False(new SecEdgarFramesNormalizer()
            .CanNormalize(EodhdProvider.Id, DataCategory.MarketWideDisclosure));

    [Fact]
    public void The_frames_normaliser_takes_the_market_wide_category() =>
        Assert.True(new SecEdgarFramesNormalizer()
            .CanNormalize(SecEdgarProvider.Id, DataCategory.MarketWideDisclosure));

    /// <summary>
    /// It produces no observations, and does not quarantine either.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Zero observations is the point; a quarantine would file a document the
    /// platform deliberately declines to interpret alongside the ones it could not read, and would
    /// make the acquisition's "quarantined payloads unchanged" check fire on every frame fetched.
    /// </remarks>
    [Fact]
    public async Task A_frame_is_archived_and_yields_no_observations()
    {
        var result = await new SecEdgarFramesNormalizer().NormalizeAsync(Input(
            """{"taxonomy":"us-gaap","tag":"Assets","data":[{"cik":320193,"val":1}]}"""));

        Assert.Empty(result.Observations);
        Assert.False(result.IsQuarantined);
    }

    /// <summary>Even nonsense archives without complaint: it is never read, so it cannot be unreadable.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public async Task A_frame_is_not_parsed_so_it_cannot_be_refused_for_its_shape(string payload)
    {
        var result = await new SecEdgarFramesNormalizer().NormalizeAsync(Input(payload));

        Assert.Empty(result.Observations);
        Assert.False(result.IsQuarantined);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static NormalizationInput Input(string payload) => new(
        SecEdgarProvider.Id,
        DataCategory.MarketWideDisclosure,
        IngestionSubject.Create(SecEdgarEndpoints.PeriodSubjectKind, Frame),
        ContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(payload)),
        System.Text.Encoding.UTF8.GetBytes(payload),
        Now);

    private static SecEdgarProvider Provider() => new(
        new HttpClient { BaseAddress = new Uri("https://data.sec.gov.test/") },
        Options.Create(EdgarOptions()),
        new StoppedClock());

    private static DataSource Definition() =>
        new SecEdgarSource().Definition(Now);

    private static SecEdgarOptions EdgarOptions() => new()
    {
        Enabled = true,
        ApplicationName = "AI Investment Analyst Tests",
        ContactEmail = "tests@example.invalid",
        MaxRequestsPerSecond = 5,
    };

    private sealed class StoppedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
