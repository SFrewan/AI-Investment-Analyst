using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Xunit;

namespace AI.Investment.Integration.Tests.Portfolio;

/// <summary>
/// The snapshot every money limit is judged against, built from a real database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This class had no test at all.</strong> Position size, total exposure, concentration,
/// drawdown, daily loss and the per-cycle cost ceiling are all evaluated against the
/// <c>ExposureSnapshot</c> this provider assembles, and every one of the twenty-one
/// <c>LimitEngineTests</c> hands the engine a snapshot built by hand. The engine's arithmetic was
/// therefore well covered and the numbers fed into it were covered by nothing.
/// </para>
/// <para>
/// It has already shipped broken once. The per-instrument exposure map was <c>null</c> until
/// Block 3, so <c>ExposureTo</c> answered zero for every instrument and the concentration ceiling
/// could not bind - a control that read as working. Nothing in the suite would have noticed then,
/// and nothing would notice a repeat now. That is what these tests are for.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class LedgerExposurePersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 31, 15, 0, 0, DateTimeKind.Utc);

    private static readonly Currency Usd = Currency.Usd;

    private readonly PostgresFixture _fixture;

    public LedgerExposurePersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task An_empty_ledger_produces_a_flat_snapshot_rather_than_a_failure()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var snapshot = await ReadAsync();

        Assert.Equal(Usd, snapshot.Currency);
        Assert.True(snapshot.TotalExposure.IsZero);
        Assert.True(snapshot.RealisedLossToday.IsZero);
        Assert.True(snapshot.ExposureTo("AAPL.US").IsZero);
    }

    /// <summary>
    /// Total exposure is the positions balance, and it comes from the entries rather than a field.
    /// </summary>
    [SkippableFact]
    public async Task Committed_capital_reaches_the_snapshot_as_total_exposure()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedLedgerAsync(
            Entry(LedgerAccount.Positions, LedgerAccount.Cash, 1_000m, "bought AAPL"),
            Entry(LedgerAccount.Positions, LedgerAccount.Cash, 500m, "bought MSFT"));

        var snapshot = await ReadAsync();

        Assert.Equal(1_500m, snapshot.TotalExposure.Amount);
    }

    /// <summary>
    /// A loss today counts against the daily ceiling; the same loss yesterday does not.
    /// </summary>
    /// <remarks>
    /// The boundary the daily-loss limit turns on. Getting it wrong in one direction lets a bad
    /// day continue past its ceiling; in the other it stops trading on a ceiling that was
    /// consumed last week.
    /// </remarks>
    [SkippableFact]
    public async Task Only_todays_realised_losses_count_towards_the_daily_ceiling()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedLedgerAsync(
            Entry(LedgerAccount.RealisedLosses, LedgerAccount.Cash, 40m, "lost yesterday", -20),
            Entry(LedgerAccount.RealisedLosses, LedgerAccount.Cash, 25m, "lost this morning", -2),
            Entry(LedgerAccount.RealisedLosses, LedgerAccount.Cash, 15m, "lost since", -1));

        var snapshot = await ReadAsync();

        Assert.Equal(40m, snapshot.RealisedLossToday.Amount);
        Assert.NotNull(snapshot.LastRealisedLossAtUtc);
    }

    /// <summary>
    /// The concentration input, which answered zero for every instrument until Block 3.
    /// </summary>
    /// <remarks>
    /// Asserted per instrument rather than in aggregate, because the defect it replaced was not a
    /// wrong total - the total was right - but a map that was never built. A test on the total
    /// would have passed throughout.
    /// </remarks>
    [SkippableFact]
    public async Task Exposure_is_attributed_to_the_instrument_that_holds_it()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedPositionsAsync(
            Fill("AAPL.US", "venue-aapl-1", quantity: 10m, price: 100m),
            Fill("AAPL.US", "venue-aapl-2", quantity: 5m, price: 120m),
            Fill("MSFT.US", "venue-msft-1", quantity: 4m, price: 250m));

        var snapshot = await ReadAsync();

        Assert.Equal(1_600m, snapshot.ExposureTo("AAPL.US").Amount);
        Assert.Equal(1_000m, snapshot.ExposureTo("MSFT.US").Amount);

        // An instrument nothing was ever bought in is zero, not absent and not an error.
        Assert.True(snapshot.ExposureTo("TSLA.US").IsZero);
    }

    /// <summary>
    /// The cycle-cost input is zero here, and that is now correct rather than a gap.
    /// </summary>
    /// <remarks>
    /// <c>LimitEngine.CheckCycleCost</c> compares a proposal against <c>snapshot.CycleCost</c>,
    /// and what an operating cycle has spent is a property of that cycle rather than of the book.
    /// This provider is repository-scoped and has never been told which cycle it is serving, so
    /// zero is the truthful answer from here - and it used to be the only answer anywhere, which
    /// meant the ceiling could not accumulate. <c>OperatingCycleRunner</c> now supplies the real
    /// figure through <c>ExposureSnapshot.WithCycleCost</c> before the limit engine sees the
    /// snapshot; that behaviour is proved in
    /// <c>OperatingCycleRunnerTests.The_cycle_cost_ceiling_counts_what_the_cycle_has_already_spent</c>.
    /// This test holds the boundary: the provider must keep answering zero rather than inventing
    /// a cycle it cannot see.
    /// </remarks>
    [SkippableFact]
    public async Task The_provider_reports_no_cycle_cost_because_it_serves_no_particular_cycle()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedLedgerAsync(
            Entry(LedgerAccount.Fees, LedgerAccount.Cash, 12m, "commission", -1),
            Entry(LedgerAccount.Fees, LedgerAccount.Cash, 9m, "commission", 0));

        var snapshot = await ReadAsync();

        Assert.True(snapshot.CycleCost.IsZero);

        // And the runner's contribution is applied on top of exactly this snapshot.
        Assert.Equal(21m, snapshot.WithCycleCost(Money.Create(21m, Usd)).CycleCost.Amount);
    }

    // ---- seeding -----------------------------------------------------------

    private async Task<ExposureSnapshot> ReadAsync()
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await new LedgerExposureProvider(context, new FixedClock(Now)).GetAsync(Usd);
    }

    private async Task SeedLedgerAsync(params LedgerEntry[] entries)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfLedgerStore(context).AppendAsync(entries);
        }
    }

    private async Task SeedPositionsAsync(params PositionEvent[] events)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var store = new EfPositionEventStore(context);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            foreach (var positionEvent in events)
            {
                await store.AppendAsync(positionEvent);
            }
        }
    }

    private static LedgerEntry Entry(
        LedgerAccount debit,
        LedgerAccount credit,
        decimal amount,
        string description,
        int hours = 0) =>
        LedgerEntry.Post(
            debit,
            credit,
            Money.Create(amount, Usd),
            Now.AddHours(hours),
            description);

    private static PositionEvent Fill(
        string instrument,
        string reference,
        decimal quantity,
        decimal price) =>
        PositionEvent.Record(
            instrument,
            PositionChange.Acquired,
            quantity,
            Money.Create(price, Usd),
            Money.Create(1m, Usd),
            reference,
            new OpportunityId(Guid.NewGuid()),
            Now.AddHours(-1));

    /// <summary>A clock that does not move, so "today" is a fact rather than a race.</summary>
    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime nowUtc) => UtcNow = nowUtc;

        public DateTime UtcNow { get; }
    }
}
