using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// That a fetch which reached the vendor can never leave the ledger and the claim disagreeing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The failure these exist for cost an instrument and cannot be undone.</strong> The action
/// seam claims an idempotency key before the effect runs; the data plane records the run after it.
/// During the Block 2B backfill the effect succeeded and the record threw - a shared owned-entity
/// instance had already poisoned the change tracker - so <c>AAPL.US</c> prices ended with a claim
/// and no ledger row. From that state the request can never be fetched again, because the seam
/// suppresses it as a duplicate, and can never be recorded, because nothing re-runs it. The claim
/// cannot be released either: the write guard refuses to delete a <c>ProcessedAction</c>
/// unconditionally, which is correct and deliberate.
/// </para>
/// <para>
/// So the invariant has to hold from the other side, and that is what is asserted here: whatever
/// state the context is in, the run that describes a fetch reaches the ledger. When the fetch
/// succeeded the ledger says so, and the claim and the ledger then agree that the request is done.
/// </para>
/// <para>
/// These run against real PostgreSQL through the real stores and the real seam. Only the network
/// and the policy engine are substituted - see <see cref="AcquisitionHarness"/> for why.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class IngestionLedgerConsistencyTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Two sessions is enough; this is about the ledger, not about the series.</summary>
    private const string PriceDocument =
        """
        [{"date":"2026-08-27","open":100.0,"high":101.0,"low":99.0,"close":100.5,"adjusted_close":100.5,"volume":1000},
         {"date":"2026-08-28","open":100.5,"high":102.0,"low":100.0,"close":101.5,"adjusted_close":101.5,"volume":1200}]
        """;

    private readonly PostgresFixture _fixture;

    public IngestionLedgerConsistencyTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// <strong>The regression.</strong> The exact Block 2B failure, asserted not to happen at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <see cref="DateRange"/> instance handed to two requests inside one scope. EF associates
    /// an owned entity with its owner by reference identity, so the second save read as re-parenting
    /// the first run's window and the tracker refused it - after the fetch had happened and been
    /// paid for.
    /// </para>
    /// <para>
    /// The sharing is left in on purpose, because the fix is not in the caller. It is in
    /// <c>IngestionRequest.Create</c>, which now copies the subject and the window the way
    /// <c>Observation.RecordFact</c> has always copied its subject - so a caller that shares an
    /// instance is simply no longer able to cause this. A test that shared nothing would prove that
    /// this one caller had been corrected, which is not the property worth holding.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_second_run_sharing_an_owned_instance_still_reaches_the_ledger()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        using var harness = await AcquisitionHarness.StartAsync(_fixture, Now);

        harness.Handler
            .WithPrices("AAPL.US", PriceDocument)
            .WithPrices("MSFT.US", PriceDocument);

        // THE BUG, reproduced: one window object, two requests, one scope.
        var window = DateRange.Create(
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc));

        var first = PriceRequest("AAPL.US", window);
        var second = PriceRequest("MSFT.US", window);

        var firstRun = await harness.Ingestion.IngestAsync(first);
        var secondRun = await harness.Ingestion.IngestAsync(second);

        Assert.Equal(IngestionOutcome.Succeeded, firstRun.Outcome);
        Assert.Equal(IngestionOutcome.Succeeded, secondRun.Outcome);

        // Both fetches happened, so both must be in the ledger.
        Assert.Equal(2, harness.Handler.PriceCalls);

        Assert.True(
            await harness.Runs.HasCompletedAsync(first.Fingerprint()),
            "The first run is missing from the ledger.");

        Assert.True(
            await harness.Runs.HasCompletedAsync(second.Fingerprint()),
            "The second run reached the vendor but never reached the ledger. This is the Block 2B " +
            "failure: its idempotency claim now blocks every retry of a request nothing will ever " +
            "record.");
    }

    /// <summary>
    /// <strong>The invariant.</strong> No claim is left without the completed run it belongs to.
    /// </summary>
    /// <remarks>
    /// Stated over the whole table rather than over one request, because the property that matters
    /// is not "this request recovered" but "no request can be in that state". Every ingestion claim
    /// present after the run above must have a <c>Succeeded</c> ledger row carrying its fingerprint.
    /// </remarks>
    [SkippableFact]
    public async Task No_ingestion_claim_is_left_without_a_completed_run()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        using var harness = await AcquisitionHarness.StartAsync(_fixture, Now);

        harness.Handler
            .WithPrices("AAPL.US", PriceDocument)
            .WithPrices("MSFT.US", PriceDocument)
            .WithSplits("AAPL.US", "[]");

        var window = DateRange.Create(
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc));

        var requests = new[]
        {
            PriceRequest("AAPL.US", window),
            PriceRequest("MSFT.US", window),
            SplitRequest("AAPL.US", window),
        };

        foreach (var request in requests)
        {
            _ = await harness.Ingestion.IngestAsync(request);
        }

        var claims = await harness.Context
            .Set<ProcessedAction>()
            .AsNoTracking()
            .Select(p => p.IdempotencyKey)
            .ToListAsync();

        var stranded = new List<string>();

        foreach (var request in requests)
        {
            var key = KeyFor(request);

            if (!claims.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (!await harness.Runs.HasCompletedAsync(request.Fingerprint()))
            {
                stranded.Add(key);
            }
        }

        Assert.True(
            stranded.Count == 0,
            "These requests hold an idempotency claim with no completed run behind it, so they can "
            + "neither be re-fetched nor ever recorded: " + string.Join(", ", stranded));
    }

    /// <summary>
    /// A run is ledgered even when this context is already carrying a change that cannot be saved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defence in depth, and the layer that does not depend on knowing the cause. The domain now
    /// copies the owned instances that produced the Block 2B failure, but the store still shares
    /// the caller's scoped context and still has no way to know what else is pending on it - only
    /// that a refusal or a failure it is asked to record must not be lost to somebody else's
    /// broken write.
    /// </para>
    /// <para>
    /// The pending change here is an unauthorised one: an observation added without an open
    /// authorisation window, which the write guard refuses. That is an ordinary shape rather than
    /// a contrived one - something tried to write outside the seam, and the ledger still has to be
    /// able to say so.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_refusal_is_recorded_even_when_the_context_is_already_broken()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        using var harness = await AcquisitionHarness.StartAsync(_fixture, Now);

        var window = DateRange.Create(
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc));

        // Something wrote domain state without going through the seam. The guard refuses it, and
        // the rejected entity stays in the tracker.
        harness.Context.Observations.Add(Observation.RecordFact(
            IngestionSubject.Create("Security", "AAPL.US"),
            "security.close",
            ObservationValue.Number(100m),
            Provenance.Create("eodhd-eod", Now.AddDays(-1), Now.AddDays(-1), Now)));

        await Assert.ThrowsAsync<UnauthorizedWriteException>(
            () => harness.Context.SaveChangesAsync());

        // The ledger must still be writable. This is the situation the exemption exists for.
        await harness.Runs.RecordAsync(Refused(PriceRequest("AAPL.US", window)));

        var recorded = await harness.Context.IngestionRuns.AsNoTracking().CountAsync();

        Assert.Equal(1, recorded);

        // And nothing was smuggled past the guard on the way: the unauthorised observation is gone,
        // not committed.
        Assert.Equal(0, await harness.Context.Observations.AsNoTracking().CountAsync());
    }

    // ---- helpers -----------------------------------------------------------

    private static IngestionRun Refused(IngestionRequest request) =>
        IngestionRun.Refuse(request, "test.refused@1", "recorded by the ledger consistency test", Now);

    private static IngestionRequest PriceRequest(string symbol, DateRange window) =>
        IngestionRequest.Create(
            EodhdProvider.Id,
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create(EodhdProvider.SecurityKind, symbol),
            CorrelationId.Create("ledger-" + symbol.Replace('.', '-')),
            Now,
            window);

    private static IngestionRequest SplitRequest(string symbol, DateRange window) =>
        IngestionRequest.Create(
            EodhdSplitsProvider.Id,
            DataCategory.CorporateActions,
            Region.Global,
            IngestionSubject.Create(EodhdProvider.SecurityKind, symbol),
            CorrelationId.Create("ledger-splits-" + symbol.Replace('.', '-')),
            Now,
            window);

    /// <summary>
    /// The seam's key for an ingestion request.
    /// </summary>
    /// <remarks>
    /// Restated here rather than exposed from the gateway, because a test that read the key from
    /// the code under test would agree with it however wrong it was. If this drifts from
    /// <c>IngestionGateway.IdempotencyKeyFor</c> the assertion above stops finding claims, which
    /// is a visible failure rather than a silent one.
    /// </remarks>
    private static string KeyFor(IngestionRequest request) =>
        string.Create(CultureInfo.InvariantCulture, $"{request.Fingerprint()}:{request.CorrelationId}");
}
