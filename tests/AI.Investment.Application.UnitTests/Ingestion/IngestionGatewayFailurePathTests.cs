using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Ingestion;

// StubActionGateway and FixedClock live here, beside the other shared doubles, and are used the
// same way by ProviderExchangeProvenanceTests in this namespace. Reusing them rather than declaring
// a second pair: two versions of "a gateway that always executes" would drift, and a drifted double
// makes a passing test mean less than it appears to.
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ingestion;

/// <summary>
/// What the gateway does when something fails <em>after</em> the run is already in the ledger.
/// </summary>
/// <remarks>
/// <para>
/// The catch path used to record the run unconditionally. On the success path the run has already
/// been recorded, so a failure in the provenance write two lines later sent the same run to the
/// store a second time - and an ingestion run has a primary key. The live pilot ended as
/// <c>23505 duplicate key value violates unique constraint "PK_ingestion_runs"</c>, which is not
/// what went wrong; it is what went wrong while handling what went wrong, standing in front of it.
/// </para>
/// <para>
/// These tests pin both halves: the duplicate never happens, and the original failure is the one
/// the caller sees. They also pin the behaviour that must NOT change - a failure before the run is
/// durable is still absorbed and returned, because a scheduler ingesting fifty subjects must not
/// lose forty-nine to one provider being down.
/// </para>
/// </remarks>
public sealed class IngestionGatewayFailurePathTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId TestSource = SourceId.Create("failure-path-source");
    private const DataCategory Filings = DataCategory.RegulatoryFilings;

    /// <summary>The exception a provenance store is made to throw, recognisable on sight.</summary>
    private sealed class ProvenanceWriteFailedException : Exception
    {
        public ProvenanceWriteFailedException()
            : base("The provenance write failed, and this is the message that must survive.")
        {
        }
    }

    // ---------- 1. already-recorded run + downstream failure ----------

    [Fact]
    public async Task A_failure_after_the_run_is_recorded_does_not_record_the_run_twice()
    {
        var harness = Build(new ThrowingProviderExchangeStore());

        await Assert.ThrowsAsync<ProvenanceWriteFailedException>(
            () => harness.Gateway.IngestAsync(Request()));

        // One insert, not two. This is the duplicate key that the live pilot hit.
        var recorded = Assert.Single(harness.Runs.Recorded);
        Assert.Equal(IngestionOutcome.Succeeded, recorded.Outcome);
    }

    [Fact]
    public async Task A_failure_after_the_run_is_recorded_preserves_the_original_exception()
    {
        var harness = Build(new ThrowingProviderExchangeStore());

        var exception = await Assert.ThrowsAsync<ProvenanceWriteFailedException>(
            () => harness.Gateway.IngestAsync(Request()));

        // Type, message and the fact that it is the FIRST failure rather than a second one raised
        // while handling it.
        Assert.Contains("must survive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_after_the_run_is_recorded_is_not_swallowed()
    {
        var harness = Build(new ThrowingProviderExchangeStore());

        // Not "returns a run that looks fine". The whole point of the provenance record is that the
        // platform must never quietly believe it holds evidence it does not hold, so the caller is
        // told rather than handed a successful-looking run.
        await Assert.ThrowsAsync<ProvenanceWriteFailedException>(
            () => harness.Gateway.IngestAsync(Request()));
    }

    [Fact]
    public async Task A_failed_provenance_write_is_not_retried_from_the_catch_path()
    {
        var store = new ThrowingProviderExchangeStore();
        var harness = Build(store);

        await Assert.ThrowsAsync<ProvenanceWriteFailedException>(
            () => harness.Gateway.IngestAsync(Request()));

        // Once. The old catch path called RecordExchangesAsync again, which for a store that had
        // just failed meant failing again from inside a catch block.
        Assert.Equal(1, store.Attempts);
    }

    // ---------- 2. the behaviour that must not change ----------

    [Fact]
    public async Task A_failure_before_the_run_is_recorded_is_still_absorbed_and_returned()
    {
        var provider = new FakeDataProvider(
            TestSource,
            Capabilities(),
            throwOnFetch: new InvalidOperationException("the provider is down"));

        var harness = Build(new RecordingProviderExchangeStore(), provider);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Failed, run.Outcome);

        // IngestionGateway.Describe records TYPE NAMES ONLY - never a message, never a URL -
        // because the reason goes into an append-only ledger that cannot be redacted afterwards,
        // and a provider's exception message is one of the likelier places for a URL with an
        // embedded key to surface. So the run names the failure by type, and the message must NOT
        // be there. This assertion originally looked for the message and failed, correctly: it was
        // asserting the opposite of a deliberate security property.
        //
        // `!` because IngestionRun.Reason is declared `string?` and this project builds with
        // nullable enabled and warnings as errors.
        Assert.Contains("InvalidOperationException", run.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("the provider is down", run.Reason!, StringComparison.Ordinal);

        // Recorded exactly once, by the catch path, because the success path never reached it.
        var recorded = Assert.Single(harness.Runs.Recorded);
        Assert.Equal(run.Id, recorded.Id);
    }

    [Fact]
    public async Task A_successful_run_is_unchanged()
    {
        var store = new RecordingProviderExchangeStore();
        var harness = Build(store);

        var run = await harness.Gateway.IngestAsync(Request());

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Single(harness.Runs.Recorded);
        Assert.Single(store.Recorded);
        Assert.Equal(1, store.RecordCallCount);
        Assert.Single(run.Artifacts);
    }

    // ---------- fixtures ----------

    private sealed record Harness(
        IngestionGateway Gateway,
        RecordingArchive Archive,
        RecordingRunStore Runs);

    private static Harness Build(
        IProviderExchangeStore exchanges,
        FakeDataProvider? provider = null)
    {
        var registry = new InMemorySourceRegistry();
        registry.Add(ActiveSource());

        var catalogue = new StubProviderCatalogue();
        catalogue.Add(provider ?? new FakeDataProvider(TestSource, Capabilities(), [Page()]));

        var archive = new RecordingArchive();
        var runs = new RecordingRunStore();

        var gateway = new IngestionGateway(
            registry,
            catalogue,
            new StubRateLimiter(true),
            archive,
            runs,
            new StubActionGateway(ActionOutcomeStatus.Executed),
            new FixedClock(Now),
            exchanges);

        return new Harness(gateway, archive, runs);
    }

    private static DataSource ActiveSource()
    {
        var source = DataSource.Register(
            TestSource,
            "Failure Path Source",
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

    private static IngestionRequest Request() =>
        IngestionRequest.Create(
            TestSource,
            Filings,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "AAPL"),
            CorrelationId.New(),
            Now,
            null);

    private static ProviderResponse Page() =>
        ProviderResponse.Create(
            Encoding.UTF8.GetBytes("[]"),
            "application/json",
            Now,
            continuationToken: null,
            provenance: RequestProvenance.Create(
                "api/eod/{symbol}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["symbol"] = "AAPL.US" },
                null,
                null,
                200,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["content-type"] = "application/json" },
                null));

    /// <summary>A provenance store that always fails, and counts how often it was asked.</summary>
    private sealed class ThrowingProviderExchangeStore : IProviderExchangeStore
    {
        public int Attempts { get; private set; }

        public Task RecordAsync(
            IReadOnlyList<ProviderExchange> exchanges,
            CancellationToken cancellationToken = default)
        {
            Attempts++;

            throw new ProvenanceWriteFailedException();
        }

        public Task<IReadOnlyList<ProviderExchange>> ForRunAsync(
            IngestionRunId runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderExchange>>([]);

        public Task<IReadOnlyList<ProviderExchange>> ForResponseContentHashAsync(
            string contentHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderExchange>>([]);
    }
}
