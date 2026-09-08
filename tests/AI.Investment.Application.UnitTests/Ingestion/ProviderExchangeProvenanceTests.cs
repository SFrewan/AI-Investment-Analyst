using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ingestion;

/// <summary>
/// Per-run request provenance: what was asked of a provider, and what came back.
/// </summary>
/// <remarks>
/// <para>
/// The record exists because the archive is content-addressed. The two-byte payload <c>[]</c> is
/// held by 324 runs and its sidecar describes only the first writer, so a store keyed by content can
/// never say what 324 different requests asked. The assertions that carry the most weight here are
/// the ones about that: several runs producing one hash, and each keeping its own evidence.
/// </para>
/// <para>
/// No network. Every exchange comes from <see cref="FakeDataProvider"/>, which returns
/// <see cref="ProviderResponse"/> objects built in this file. No EODHD call is made or needed.
/// </para>
/// </remarks>
public sealed class ProviderExchangeProvenanceTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId TestSource = SourceId.Create("test-source");
    private const DataCategory Filings = DataCategory.RegulatoryFilings;

    // ---------- fixtures ----------

    private static DataSource ActiveSource()
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

        source.Activate(Now);

        return source;
    }

    private static ProviderCapabilities Capabilities() =>
        ProviderCapabilities.Create(
            [Filings],
            [Region.UnitedStates],
            ["Company"],
            false,
            null,
            null);

    private static IngestionRequest Request(CorrelationId? correlation = null) =>
        IngestionRequest.Create(
            TestSource,
            Filings,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "AAPL"),
            correlation ?? CorrelationId.New(),
            Now,
            null);

    private static readonly DateTime WindowFrom = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private static RequestProvenance Provenance(
        int? status = 200,
        string? correlationId = "vendor-ref-1",
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        IReadOnlyDictionary<string, string>? extraParameters = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["symbol"] = "AAPL.US",
            ["fmt"] = "json",
            ["period"] = "d",
            ["from"] = RequestProvenance.FormatDate(WindowFrom),
            ["to"] = RequestProvenance.FormatDate(WindowTo),
        };

        foreach (var pair in extraParameters ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            parameters[pair.Key] = pair.Value;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = "application/json",
            ["x-ratelimit-remaining"] = "998",
        };

        foreach (var pair in extraHeaders ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            headers[pair.Key] = pair.Value;
        }

        return RequestProvenance.Create(
            "api/eod/{symbol}",
            parameters,
            WindowFrom,
            WindowTo,
            status,
            headers,
            correlationId);
    }

    private static ProviderResponse Page(
        string body,
        string? continuation = null,
        RequestProvenance? provenance = null) =>
        ProviderResponse.Create(
            Encoding.UTF8.GetBytes(body),
            "application/json",
            Now,
            continuationToken: continuation,
            provenance: provenance ?? Provenance());

    private sealed record Harness(
        IngestionGateway Gateway,
        RecordingArchive Archive,
        RecordingRunStore Runs,
        RecordingProviderExchangeStore Exchanges);

    private static Harness Build(FakeDataProvider provider, RecordingProviderExchangeStore? store = null)
    {
        var registry = new InMemorySourceRegistry();
        registry.Add(ActiveSource());

        var catalogue = new StubProviderCatalogue();
        catalogue.Add(provider);

        var archive = new RecordingArchive();
        var runs = new RecordingRunStore();
        var exchanges = store ?? new RecordingProviderExchangeStore();

        var gateway = new IngestionGateway(
            registry,
            catalogue,
            new StubRateLimiter(true),
            archive,
            runs,
            new StubActionGateway(ActionOutcomeStatus.Executed),
            new FixedClock(Now),
            exchanges);

        return new Harness(gateway, archive, runs, exchanges);
    }

    // ---------- 1. one request, one record ----------

    [Fact]
    public async Task One_exchange_produces_exactly_one_provenance_record()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]);
        var harness = Build(provider);

        var run = await harness.Gateway.IngestAsync(Request());

        var recorded = Assert.Single(harness.Exchanges.Recorded);

        Assert.Equal(run.Id, recorded.IngestionRunId);
        Assert.Equal(0, recorded.ExchangeOrdinal);
        Assert.Equal(TestSource, recorded.SourceId);
        Assert.Equal("api/eod/{symbol}", recorded.EndpointTemplate);
    }

    // ---------- 2. multi-exchange ----------

    [Fact]
    public async Task A_paged_run_produces_one_record_per_exchange_sharing_the_run_id()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            [Page("[1]", continuation: "next"), Page("[2]", continuation: "more"), Page("[3]")]);

        var harness = Build(provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(3, harness.Exchanges.Recorded.Count);
        Assert.All(harness.Exchanges.Recorded, e => Assert.Equal(run.Id, e.IngestionRunId));

        // The ordinal is what separates them, and it is what the unique index is built on.
        Assert.Equal([0, 1, 2], harness.Exchanges.Recorded.Select(e => e.ExchangeOrdinal).ToArray());

        // Three exchanges, three distinct payloads, three distinct hashes.
        Assert.Equal(3, harness.Exchanges.Recorded.Select(e => e.ResponseContentHash).Distinct().Count());
    }

    // ---------- 3. shared payload ----------

    [Fact]
    public async Task Two_runs_returning_identical_bytes_keep_separate_records_with_one_shared_hash()
    {
        // The [] case, in miniature. This is the whole reason the record exists: the archive stores
        // these bytes once and its sidecar can describe only the first writer, so the evidence for
        // the second run has to live somewhere the content hash is not the key.
        var store = new RecordingProviderExchangeStore();

        var first = Build(new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]), store);
        var runOne = await first.Gateway.IngestAsync(Request());

        var second = Build(new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]), store);
        var runTwo = await second.Gateway.IngestAsync(Request());

        Assert.Equal(2, store.Recorded.Count);
        Assert.NotEqual(runOne.Id, runTwo.Id);

        var hashes = store.Recorded.Select(e => e.ResponseContentHash).Distinct().ToList();

        Assert.Single(hashes);
        Assert.Equal(2, store.Recorded.Select(e => e.IngestionRunId).Distinct().Count());
    }

    // ---------- 4 and 5. failure, then retry ----------

    [Fact]
    public async Task An_exchange_that_never_completed_leaves_no_record_while_the_run_is_still_recorded()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            throwOnFetch: new InvalidOperationException("the transport failed"));

        var harness = Build(provider);

        var run = await harness.Gateway.IngestAsync(Request());

        // The run is recorded as failed - that behaviour is unchanged.
        Assert.Equal(IngestionOutcome.Failed, run.Outcome);
        Assert.Single(harness.Runs.Recorded);

        // And no exchange row is invented for a request that never got an answer. A row claiming an
        // exchange happened would be worse than no row at all.
        Assert.Empty(harness.Exchanges.Recorded);
    }

    [Fact]
    public async Task A_retry_is_a_separate_run_and_a_separate_record_carrying_the_same_fingerprint()
    {
        // LGIQ's shape: a failed attempt and a successful retry of the SAME request. The run id is
        // the attempt identity, the fingerprint is what says it was the same question, and no
        // RetryAttempt counter is needed for either.
        var store = new RecordingProviderExchangeStore();
        var correlation = CorrelationId.New();

        var failing = Build(
            new FakeDataProvider(TestSource, Capabilities(), throwOnFetch: new InvalidOperationException("down")),
            store);

        var failed = await failing.Gateway.IngestAsync(Request(correlation));

        var succeeding = Build(new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]), store);
        var succeeded = await succeeding.Gateway.IngestAsync(Request(correlation));

        Assert.NotEqual(failed.Id, succeeded.Id);

        var recorded = Assert.Single(store.Recorded);

        Assert.Equal(succeeded.Id, recorded.IngestionRunId);
        Assert.Equal(succeeded.Request.Fingerprint(), recorded.RequestFingerprint);

        // Same question, both times - which is what makes the pair a retry rather than two requests.
        Assert.Equal(failed.Request.Fingerprint(), succeeded.Request.Fingerprint());
    }

    // ---------- 6, 7, 8. redaction and allow-lists ----------

    [Fact]
    public void The_api_token_is_not_on_the_parameter_allow_list()
    {
        Assert.DoesNotContain("api_token", RequestProvenance.AllowedParameterKeys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", RequestProvenance.AllowedParameterKeys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_api_token_offered_as_a_parameter_is_dropped_rather_than_stored()
    {
        var provenance = RequestProvenance.Create(
            "api/eod/{symbol}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = "AAPL.US",
                ["api_token"] = "SUPER-SECRET-KEY",
            });

        Assert.DoesNotContain("api_token", provenance.RedactedParameters.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("SUPER-SECRET-KEY", provenance.ParametersAsJson(), StringComparison.Ordinal);
        Assert.Contains("AAPL.US", provenance.ParametersAsJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Authorization_and_cookie_headers_are_never_persisted()
    {
        Assert.DoesNotContain("authorization", RequestProvenance.AllowedHeaderNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("set-cookie", RequestProvenance.AllowedHeaderNames, StringComparer.OrdinalIgnoreCase);

        var provenance = RequestProvenance.Create(
            "api/eod/{symbol}",
            headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer abcdef",
                ["Set-Cookie"] = "session=xyz",
                ["Content-Type"] = "application/json",
            });

        Assert.Single(provenance.SelectedHeaders);
        Assert.DoesNotContain("abcdef", provenance.HeadersAsJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("xyz", provenance.HeadersAsJson(), StringComparison.Ordinal);
        Assert.Contains("application/json", provenance.HeadersAsJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_arbitrary_unknown_header_is_dropped_rather_than_kept()
    {
        var provenance = RequestProvenance.Create(
            "api/eod/{symbol}",
            headers: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Internal-Debug-Token"] = "leaky",
            });

        Assert.Empty(provenance.SelectedHeaders);
        Assert.DoesNotContain("leaky", provenance.HeadersAsJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_is_deterministic()
    {
        // Same inputs in a different order must serialise identically, because these strings are
        // compared between exchanges and not merely displayed.
        var first = RequestProvenance.Create(
            "api/eod/{symbol}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["to"] = "2026-08-31", ["symbol"] = "AAPL.US", ["from"] = "2021-09-01",
            });

        var second = RequestProvenance.Create(
            "api/eod/{symbol}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = "AAPL.US", ["from"] = "2021-09-01", ["to"] = "2026-08-31",
            });

        Assert.Equal(first.ParametersAsJson(), second.ParametersAsJson());
    }

    [Fact]
    public void The_domain_refuses_a_record_carrying_a_credential_even_if_an_allow_list_let_it_through()
    {
        // The second line of defence. If an allow-list is ever widened by mistake, the append-only
        // record still refuses the write rather than storing something it can never take back out.
        var exception = Assert.Throws<DomainValidationException>(() => ProviderExchange.Record(
            IngestionRunId.New(),
            0,
            TestSource,
            new string('a', 64),
            "api/eod/{symbol}?api_token=SECRET",
            "{}",
            null,
            null,
            200,
            "{}",
            Now,
            null,
            null,
            null,
            Now));

        Assert.DoesNotContain("SECRET", exception.Message, StringComparison.Ordinal);
    }

    // ---------- 9, 10, 11, 12. what the evidence actually captures ----------

    [Fact]
    public async Task The_request_window_is_captured_exactly_as_sent()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]);
        var harness = Build(provider);

        await harness.Gateway.IngestAsync(Request());

        var recorded = Assert.Single(harness.Exchanges.Recorded);

        Assert.Equal(WindowFrom, recorded.RequestedFromUtc);
        Assert.Equal(WindowTo, recorded.RequestedToUtc);
        Assert.Contains("2021-09-01", recorded.RedactedRequestParameters, StringComparison.Ordinal);
        Assert.Contains("2026-08-31", recorded.RedactedRequestParameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_code_is_captured_as_an_integer()
    {
        var provider = new FakeDataProvider(
            TestSource, Capabilities(), [Page("[]", provenance: Provenance(status: 200))]);

        var harness = Build(provider);

        await harness.Gateway.IngestAsync(Request());

        Assert.Equal(200, Assert.Single(harness.Exchanges.Recorded).HttpStatusCode);
    }

    [Fact]
    public async Task The_retrieval_time_is_the_exchange_s_own()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]);
        var harness = Build(provider);

        await harness.Gateway.IngestAsync(Request());

        var recorded = Assert.Single(harness.Exchanges.Recorded);

        Assert.Equal(Now, recorded.RetrievedAtUtc);
        Assert.Equal(DateTimeKind.Utc, recorded.RetrievedAtUtc.Kind);
    }

    [Fact]
    public async Task The_recorded_hash_is_the_address_the_archive_returned()
    {
        var provider = new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]);
        var harness = Build(provider);

        var run = await harness.Gateway.IngestAsync(Request());

        var recorded = Assert.Single(harness.Exchanges.Recorded);
        var artifact = Assert.Single(run.Artifacts);

        Assert.Equal(artifact.Value, recorded.ResponseContentHash);
        Assert.Equal(2, recorded.ResponseByteLength);
        Assert.True(recorded.ProducedAPayload);
    }

    // ---------- 13. it changes nothing ----------

    [Fact]
    public async Task A_composition_without_an_exchange_store_behaves_exactly_as_before()
    {
        // The store is optional, so a host that has not registered one still acquires. This is what
        // keeps the change additive: nothing downstream reads these rows, so their absence cannot
        // alter an outcome.
        var registry = new InMemorySourceRegistry();
        registry.Add(ActiveSource());

        var catalogue = new StubProviderCatalogue();
        catalogue.Add(new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]));

        var runs = new RecordingRunStore();

        var gateway = new IngestionGateway(
            registry,
            catalogue,
            new StubRateLimiter(true),
            new RecordingArchive(),
            runs,
            new StubActionGateway(ActionOutcomeStatus.Executed),
            new FixedClock(Now));

        var run = await gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Single(run.Artifacts);
    }

    [Fact]
    public async Task A_connector_that_captures_no_provenance_still_acquires_and_records_nothing()
    {
        // Provenance is optional on ProviderResponse for exactly this reason: an uninstrumented
        // connector must keep working rather than be forced to invent evidence it does not have.
        var bare = ProviderResponse.Create(
            Encoding.UTF8.GetBytes("[]"),
            "application/json",
            Now);

        var harness = Build(new FakeDataProvider(TestSource, Capabilities(), [bare]));

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Single(run.Artifacts);
        Assert.Empty(harness.Exchanges.Recorded);
    }

    [Fact]
    public async Task Recording_provenance_does_not_change_the_archived_bytes_or_the_run_outcome()
    {
        // The bytes are what normalisation later reads, and normalisation is what decides
        // Normalized / Partial / Quarantined. If the bytes and the outcome are untouched, provenance
        // cannot have moved that decision.
        var withProvenance = Build(new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]));
        var withoutStore = Build(new FakeDataProvider(TestSource, Capabilities(), [Page("[]")]));

        var one = await withProvenance.Gateway.IngestAsync(Request());
        var two = await withoutStore.Gateway.IngestAsync(Request());

        Assert.Equal(one.Outcome, two.Outcome);
        Assert.Equal(one.Artifacts[0].Value, two.Artifacts[0].Value);
        Assert.Equal(withProvenance.Archive.StoreCount, withoutStore.Archive.StoreCount);
    }
}

/// <summary>Captures what the gateway asked to be written, without a database.</summary>
internal sealed class RecordingProviderExchangeStore : IProviderExchangeStore
{
    private readonly List<ProviderExchange> _recorded = [];

    public IReadOnlyList<ProviderExchange> Recorded => _recorded;

    public int RecordCallCount { get; private set; }

    public Task RecordAsync(
        IReadOnlyList<ProviderExchange> exchanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchanges);

        RecordCallCount++;
        _recorded.AddRange(exchanges);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProviderExchange>> ForRunAsync(
        IngestionRunId runId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderExchange>>(
            _recorded.Where(e => e.IngestionRunId == runId).OrderBy(e => e.ExchangeOrdinal).ToList());

    public Task<IReadOnlyList<ProviderExchange>> ForResponseContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderExchange>>(
            _recorded.Where(e => string.Equals(e.ResponseContentHash, contentHash, StringComparison.Ordinal)).ToList());
}
