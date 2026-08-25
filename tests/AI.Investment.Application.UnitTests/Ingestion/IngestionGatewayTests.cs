using System.Text;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ingestion;

/// <summary>
/// The four gates in front of every fetch, and what each one writes down.
/// </summary>
/// <remarks>
/// Two assertions recur and carry most of the weight: <c>FetchCount == 0</c>, which says the
/// network was never touched, and a recorded run naming the rule, which says the refusal was
/// written down. A gate that stops a request but leaves no trace turns a compliance decision into
/// an unexplained absence of data.
/// </remarks>
public sealed class IngestionGatewayTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId TestSource = SourceId.Create("test-source");
    private const DataCategory Filings = DataCategory.RegulatoryFilings;

    // ---------- fixtures ----------

    private static DataSource ActiveSource(LicensingTerms? licensing = null)
    {
        var source = DataSource.Register(
            TestSource,
            "Test Source",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [Filings],
            UpdateCadence.EventDriven,
            licensing ?? LicensingTerms.OpenData(),
            VerificationPolicy.Authoritative,
            Now);

        source.Activate(Now);

        return source;
    }

    private static ProviderCapabilities Capabilities(
        bool supportsWindow = false,
        TimeSpan? maxWindow = null,
        ProviderQuota? quota = null,
        DataCategory category = Filings) =>
        ProviderCapabilities.Create(
            [category],
            [Region.UnitedStates],
            ["Company"],
            supportsWindow,
            maxWindow,
            quota);

    private static IngestionRequest Request(DateRangeSpec window = DateRangeSpec.None) =>
        IngestionRequest.Create(
            TestSource,
            Filings,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "AAPL"),
            CorrelationId.New(),
            Now,
            window switch
            {
                DateRangeSpec.OneDay => DateRange.Create(Now.AddDays(-1), Now),
                DateRangeSpec.OneYear => DateRange.Create(Now.AddDays(-365), Now),
                _ => null,
            });

    private enum DateRangeSpec
    {
        None = 0,
        OneDay = 1,
        OneYear = 2,
    }

    private static ProviderResponse Page(string body, string? continuation = null) =>
        ProviderResponse.Create(
            Encoding.UTF8.GetBytes(body),
            "application/json",
            Now,
            continuationToken: continuation);

    private sealed record Harness(
        IngestionGateway Gateway,
        InMemorySourceRegistry Registry,
        StubProviderCatalogue Catalogue,
        RecordingArchive Archive,
        RecordingRunStore Runs,
        StubRateLimiter RateLimiter,
        StubActionGateway Actions);

    private static Harness Build(
        DataSource? source = null,
        FakeDataProvider? provider = null,
        bool allowRate = true,
        ActionOutcomeStatus policy = ActionOutcomeStatus.Executed)
    {
        var registry = new InMemorySourceRegistry();

        if (source is not null)
        {
            registry.Add(source);
        }

        var catalogue = new StubProviderCatalogue();

        if (provider is not null)
        {
            catalogue.Add(provider);
        }

        var archive = new RecordingArchive();
        var runs = new RecordingRunStore();
        var rateLimiter = new StubRateLimiter(allowRate);
        var actions = new StubActionGateway(policy);

        var gateway = new IngestionGateway(
            registry,
            catalogue,
            rateLimiter,
            archive,
            runs,
            actions,
            new FixedClock(Now));

        return new Harness(gateway, registry, catalogue, archive, runs, rateLimiter, actions);
    }

    // ---------- gate 1: registration ----------

    [Fact]
    public async Task An_unregistered_source_is_refused_without_touching_the_network()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{}")]);
        var harness = Build(source: null, provider: provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(IngestionGateway.SourceRegisteredRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
        Assert.Single(harness.Runs.Recorded);
    }

    // ---------- gate 2: source admission ----------

    [Fact]
    public async Task An_inactive_source_is_refused_by_source_admission()
    {
        var source = DataSource.Register(
            TestSource,
            "Test Source",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [Filings],
            UpdateCadence.EventDriven,
            LicensingTerms.OpenData(),
            VerificationPolicy.Authoritative,
            Now);

        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{}")]);
        var harness = Build(source, provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(SourceAdmission.SourceActiveRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
    }

    /// <summary>
    /// The compliance case: terms that forbid storage stop the fetch, not the write. By the time
    /// bytes have arrived, an impermissible ingestion has already happened.
    /// </summary>
    [Fact]
    public async Task A_source_whose_licence_forbids_storage_is_refused_before_fetching()
    {
        var licensing = LicensingTerms.Create(
            storageAllowed: false,
            redistributionAllowed: false,
            automatedProcessingAllowed: true,
            attributionRequired: true);

        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{}")]);
        var harness = Build(ActiveSource(licensing), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(SourceAdmission.StoragePermittedRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
        Assert.Equal(0, harness.Archive.StoreCount);
    }

    // ---------- gate 3: provider availability and capability ----------

    [Fact]
    public async Task A_source_with_no_connector_is_refused()
    {
        var harness = Build(ActiveSource(), provider: null);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionGateway.ProviderAvailableRule, run.RefusalRuleId);
    }

    [Fact]
    public async Task A_connector_that_does_not_serve_the_category_is_refused()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(category: DataCategory.MarketPrices),
            [Page("{}")]);

        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(ProviderCapabilityCheck.CategorySupportedRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
    }

    /// <summary>
    /// Answering a historical question with the latest value is a wrong answer, not a missing one,
    /// so the request is refused rather than downgraded.
    /// </summary>
    [Fact]
    public async Task A_windowed_request_to_a_connector_without_window_support_is_refused()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(supportsWindow: false), [Page("{}")]);
        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request(DateRangeSpec.OneDay));

        Assert.Equal(ProviderCapabilityCheck.WindowSupportedRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
    }

    [Fact]
    public async Task A_window_larger_than_the_connector_accepts_is_refused()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(supportsWindow: true, maxWindow: TimeSpan.FromDays(31)),
            [Page("{}")]);

        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request(DateRangeSpec.OneYear));

        Assert.Equal(ProviderCapabilityCheck.WindowWithinLimitRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
    }

    // ---------- gate 4: rate limit ----------

    [Fact]
    public async Task A_spent_quota_refuses_the_run_rather_than_waiting()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(quota: ProviderQuota.PerMinute(10)),
            [Page("{}")]);

        var harness = Build(ActiveSource(), provider, allowRate: false);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionGateway.WithinRateLimitRule, run.RefusalRuleId);
        Assert.Equal(1, harness.RateLimiter.AcquireAttempts);
        Assert.Equal(0, provider.FetchCount);
    }

    [Fact]
    public async Task A_connector_declaring_no_quota_is_not_rate_limited()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(quota: null), [Page("{}")]);
        var harness = Build(ActiveSource(), provider);

        await harness.Gateway.IngestAsync(Request());

        Assert.Equal(0, harness.RateLimiter.AcquireAttempts);
    }

    // ---------- the seam ----------

    [Fact]
    public async Task A_permitted_run_fetches_archives_and_records()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{\"a\":1}")]);
        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(1, provider.FetchCount);
        Assert.Equal(1, harness.Archive.StoreCount);
        Assert.Single(run.Artifacts);
        Assert.Single(harness.Runs.Recorded);
    }

    /// <summary>
    /// Ingestion is a side effect, so it is proposed under its own capability and carries the
    /// request fingerprint as its idempotency key - a retry after a timeout must not fetch and
    /// archive the same window twice.
    /// </summary>
    [Fact]
    public async Task A_run_is_proposed_through_the_seam_under_the_ingestion_capability()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{}")]);
        var harness = Build(ActiveSource(), provider);
        var request = Request();

        await harness.Gateway.IngestAsync(request);

        var proposal = harness.Actions.LastProposal;

        Assert.NotNull(proposal);
        Assert.Equal(Capability.DataIngestion, proposal!.Capability);
        Assert.Equal(request.Fingerprint(), proposal.IdempotencyKey);
        Assert.Equal(request.CorrelationId, proposal.CorrelationId);
    }

    /// <summary>
    /// The kill switch and every other policy rule reach ingestion for free, because ingestion
    /// goes through the same seam as everything else rather than beside it.
    /// </summary>
    [Fact]
    public async Task A_policy_denial_stops_the_fetch_and_is_recorded()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{}")]);
        var harness = Build(ActiveSource(), provider, policy: ActionOutcomeStatus.Denied);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(IngestionGateway.PolicyPermittedRule, run.RefusalRuleId);
        Assert.Equal(0, provider.FetchCount);
        Assert.Equal(0, harness.Archive.StoreCount);
        Assert.Single(harness.Runs.Recorded);
    }

    [Fact]
    public async Task An_action_awaiting_approval_does_not_fetch()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("{}")]);
        var harness = Build(ActiveSource(), provider, policy: ActionOutcomeStatus.ApprovalRequired);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(0, provider.FetchCount);
    }

    // ---------- paging and failure ----------

    [Fact]
    public async Task Pages_are_followed_until_the_continuation_token_runs_out()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            [Page("{\"p\":1}", "next-1"), Page("{\"p\":2}", "next-2"), Page("{\"p\":3}")]);

        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(3, provider.FetchCount);
        Assert.Equal(3, run.Artifacts.Count);
    }

    /// <summary>
    /// Identical bytes across pages are one artifact - the archive is content-addressed, so
    /// counting them twice would overstate what was retrieved.
    /// </summary>
    [Fact]
    public async Task Identical_pages_produce_one_artifact()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            [Page("{\"same\":1}", "next"), Page("{\"same\":1}")]);

        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(2, provider.FetchCount);
        Assert.Single(run.Artifacts);
    }

    /// <summary>
    /// A scheduler ingesting fifty subjects must not lose forty-nine because one provider was
    /// down, so a transport failure comes back as a failed run rather than an exception.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_becomes_a_failed_run_rather_than_an_exception()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            throwOnFetch: new InvalidOperationException("connection reset by peer"));

        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Failed, run.Outcome);
        Assert.Single(harness.Runs.Recorded);
    }

    /// <summary>
    /// The ledger is append-only and cannot be redacted, so a provider's exception message - one
    /// of the likelier places for a URL with an embedded key to surface - is never copied into it.
    /// </summary>
    [Fact]
    public async Task A_failure_reason_does_not_repeat_the_provider_message()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            throwOnFetch: new InvalidOperationException("https://api.example.com/v1?apikey=SECRET"));

        var harness = Build(ActiveSource(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.NotNull(run.Reason);
        Assert.DoesNotContain("SECRET", run.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", run.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}
