using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ingestion;

public sealed class ProviderQuotaTests
{
    [Fact]
    public void A_quota_reports_the_spacing_that_satisfies_it_evenly() =>
        Assert.Equal(TimeSpan.FromSeconds(6), ProviderQuota.PerMinute(10).MinimumSpacing);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_quota_must_permit_at_least_one_request(int maxRequests) =>
        Assert.Throws<DomainValidationException>(() =>
            ProviderQuota.Create(maxRequests, TimeSpan.FromMinutes(1)));

    [Fact]
    public void A_quota_window_must_be_positive() =>
        Assert.Throws<DomainValidationException>(() => ProviderQuota.Create(10, TimeSpan.Zero));

    [Fact]
    public void The_named_factories_produce_the_expected_windows()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ProviderQuota.PerSecond(5).Window);
        Assert.Equal(TimeSpan.FromMinutes(1), ProviderQuota.PerMinute(5).Window);
        Assert.Equal(TimeSpan.FromDays(1), ProviderQuota.PerDay(5).Window);
    }
}

public sealed class ProviderCapabilitiesTests
{
    private static ProviderCapabilities Create(
        IEnumerable<DataCategory>? categories = null,
        IEnumerable<Region>? regions = null,
        IEnumerable<string>? subjectKinds = null,
        bool supportsWindow = false,
        TimeSpan? maxWindow = null) =>
        ProviderCapabilities.Create(
            categories ?? [DataCategory.RegulatoryFilings],
            regions ?? [Region.UnitedStates],
            subjectKinds ?? ["Company"],
            supportsWindow,
            maxWindow);

    [Fact]
    public void A_connector_must_declare_a_category() =>
        Assert.Throws<DomainValidationException>(() => Create(categories: []));

    [Fact]
    public void Unknown_is_not_a_declaration() =>
        Assert.Throws<DomainValidationException>(() => Create(categories: [DataCategory.Unknown]));

    [Fact]
    public void A_connector_must_declare_a_region() =>
        Assert.Throws<DomainValidationException>(() => Create(regions: []));

    [Fact]
    public void A_connector_must_declare_a_subject_kind()
    {
        Assert.Throws<DomainValidationException>(() => Create(subjectKinds: []));
        Assert.Throws<DomainValidationException>(() => Create(subjectKinds: ["  "]));
    }

    /// <summary>
    /// Declaring a maximum period while not accepting periods at all is a contradiction, and
    /// guessing which half was meant would hide the mistake.
    /// </summary>
    [Fact]
    public void A_maximum_window_without_window_support_is_a_contradiction() =>
        Assert.Throws<DomainValidationException>(() =>
            Create(supportsWindow: false, maxWindow: TimeSpan.FromDays(1)));

    [Fact]
    public void A_maximum_window_must_be_positive() =>
        Assert.Throws<DomainValidationException>(() =>
            Create(supportsWindow: true, maxWindow: TimeSpan.Zero));

    [Fact]
    public void Subject_kinds_are_matched_case_insensitively()
    {
        var capabilities = Create(subjectKinds: ["Company"]);

        Assert.True(capabilities.Understands("company"));
        Assert.True(capabilities.Understands("COMPANY"));
        Assert.True(capabilities.Understands(" Company "));
        Assert.False(capabilities.Understands("Product"));
        Assert.False(capabilities.Understands(null));
    }

    [Fact]
    public void A_global_declaration_covers_every_region()
    {
        var capabilities = Create(regions: [Region.Global]);

        Assert.True(capabilities.Covers(Region.UnitedStates));
        Assert.True(capabilities.Covers(Region.Create("JP")));
    }

    /// <summary>
    /// The platform's scope is not equities, so the subject vocabulary must not be either.
    /// </summary>
    [Theory]
    [InlineData("Product")]
    [InlineData("Supplier")]
    [InlineData("CurrencyPair")]
    [InlineData("ShippingRoute")]
    [InlineData("ClinicalTrial")]
    public void Non_equity_subject_kinds_are_declarable(string kind) =>
        Assert.True(Create(subjectKinds: [kind]).Understands(kind));
}

public sealed class ProviderCapabilityCheckTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private static IngestionRequest Request(
        DataCategory category = DataCategory.RegulatoryFilings,
        string subjectKind = "Company",
        Region? region = null,
        DateRange? window = null) =>
        IngestionRequest.Create(
            SourceId.Create("sec-edgar"),
            category,
            region ?? Region.UnitedStates,
            IngestionSubject.Create(subjectKind, "AAPL"),
            CorrelationId.New(),
            Now,
            window);

    private static ProviderCapabilities Capabilities(
        bool supportsWindow = false,
        TimeSpan? maxWindow = null) =>
        ProviderCapabilities.Create(
            [DataCategory.RegulatoryFilings],
            [Region.UnitedStates],
            ["Company"],
            supportsWindow,
            maxWindow);

    [Fact]
    public void A_request_the_connector_serves_is_capable()
    {
        var result = ProviderCapabilityCheck.Evaluate(Capabilities(), Request());

        Assert.True(result.IsCapable);
        Assert.Null(result.RuleId);
    }

    [Fact]
    public void An_unsupported_category_is_refused() =>
        Assert.Equal(
            ProviderCapabilityCheck.CategorySupportedRule,
            ProviderCapabilityCheck.Evaluate(
                Capabilities(),
                Request(category: DataCategory.MarketPrices)).RuleId);

    [Fact]
    public void An_uncovered_region_is_refused() =>
        Assert.Equal(
            ProviderCapabilityCheck.RegionSupportedRule,
            ProviderCapabilityCheck.Evaluate(
                Capabilities(),
                Request(region: Region.Create("GB"))).RuleId);

    [Fact]
    public void An_unknown_subject_kind_is_refused() =>
        Assert.Equal(
            ProviderCapabilityCheck.SubjectKindSupportedRule,
            ProviderCapabilityCheck.Evaluate(
                Capabilities(),
                Request(subjectKind: "Product")).RuleId);

    /// <summary>
    /// Serving the latest value in answer to a historical question is a wrong answer rather than
    /// a missing one, which is why this refuses instead of quietly downgrading.
    /// </summary>
    [Fact]
    public void A_window_asked_of_a_latest_only_connector_is_refused() =>
        Assert.Equal(
            ProviderCapabilityCheck.WindowSupportedRule,
            ProviderCapabilityCheck.Evaluate(
                Capabilities(supportsWindow: false),
                Request(window: DateRange.Create(Now.AddDays(-2), Now))).RuleId);

    [Fact]
    public void A_window_within_the_declared_maximum_is_capable() =>
        Assert.True(
            ProviderCapabilityCheck.Evaluate(
                Capabilities(supportsWindow: true, maxWindow: TimeSpan.FromDays(31)),
                Request(window: DateRange.Create(Now.AddDays(-30), Now))).IsCapable);

    /// <summary>
    /// An over-long window is refused rather than sent, because a provider may answer it with a
    /// silently truncated result - a gap that looks like a complete answer.
    /// </summary>
    [Fact]
    public void A_window_beyond_the_declared_maximum_is_refused() =>
        Assert.Equal(
            ProviderCapabilityCheck.WindowWithinLimitRule,
            ProviderCapabilityCheck.Evaluate(
                Capabilities(supportsWindow: true, maxWindow: TimeSpan.FromDays(31)),
                Request(window: DateRange.Create(Now.AddDays(-90), Now))).RuleId);

    [Fact]
    public void An_unbounded_window_capability_accepts_any_period() =>
        Assert.True(
            ProviderCapabilityCheck.Evaluate(
                Capabilities(supportsWindow: true),
                Request(window: DateRange.Create(Now.AddDays(-3650), Now))).IsCapable);
}
