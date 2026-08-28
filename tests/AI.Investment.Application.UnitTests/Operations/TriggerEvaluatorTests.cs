using AI.Investment.Application.Operations;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>
/// Three independent controls stand between an observation and a cycle, and each of them fails
/// differently. This file exercises all three, and the counts they leave behind.
/// </summary>
public sealed class TriggerEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeClock _clock = new(Now);
    private readonly InMemoryWatchStore _watches = new();
    private readonly InMemoryCycleStore _cycles = new();
    private readonly RecordingAuditSink _audit = new();

    private TriggerEvaluator Evaluator(AdmissionLimits? limits = null) =>
        new(
            _watches,
            _cycles,
            new FixedAdmissionLimits(limits ?? AdmissionLimits.Create(4, 2, 10, 6, TimeSpan.FromHours(1))),
            new FixedCycleBudget(CycleBudget.Create(
                TimeSpan.FromMinutes(10),
                Money.Create(1m, Currency.Usd),
                20,
                1)),
            _audit,
            new FakeCorrelationContext(),
            _clock);

    private Watch Seeded(TimeSpan? cooldown = null)
    {
        var watch = Watch.Create(
            "watchlist",
            WatchTarget.Create("Security", "AAPL"),
            TriggerType.PriceMove,
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 0.02m),
            cooldown ?? TimeSpan.FromMinutes(30),
            Capability.Analysis,
            "monitor-watchlist",
            Now);

        _watches.Seed(watch);

        return watch;
    }

    private static TriggerSignal Signal(DateTime at, decimal value = 0.05m) =>
        TriggerSignal.Create(TriggerType.PriceMove, WatchTarget.Create("Security", "AAPL"), at, value);

    [Fact]
    public async Task An_observation_that_meets_a_watch_starts_one_cycle()
    {
        Seeded();

        var outcome = await Evaluator().OfferAsync(Signal(Now));

        Assert.Equal(1, outcome.Evaluated);
        Assert.Equal(1, outcome.Fired);
        Assert.Equal(1, outcome.Started);
        Assert.Equal(0, outcome.Suppressed);
        Assert.Single(_cycles.Cycles);
        Assert.Equal(1, _audit.CountOf(AuditEventType.WatchFired));
    }

    /// <summary>
    /// The same observation delivered twice starts one cycle, because the firing key is derived from
    /// the observation's identity and the store refuses the second.
    /// </summary>
    [Fact]
    public async Task A_redelivered_observation_does_not_start_a_second_cycle()
    {
        Seeded(cooldown: TimeSpan.FromSeconds(30));

        await Evaluator().OfferAsync(Signal(Now));

        _clock.Advance(TimeSpan.FromMinutes(5));

        var outcome = await Evaluator().OfferAsync(Signal(Now));

        Assert.Equal(0, outcome.Started);
        Assert.Equal(1, outcome.SuppressedAsDuplicate);
        Assert.Single(_cycles.Cycles);
    }

    /// <summary>
    /// The control that matters most during a volatile session, and the one whose suppressions are
    /// recorded so that a count of zero during one is visible as a defect.
    /// </summary>
    [Fact]
    public async Task A_second_observation_inside_the_cooldown_is_suppressed_and_recorded()
    {
        Seeded(cooldown: TimeSpan.FromMinutes(30));

        await Evaluator().OfferAsync(Signal(Now));

        _clock.Advance(TimeSpan.FromMinutes(5));

        var outcome = await Evaluator().OfferAsync(Signal(_clock.UtcNow));

        Assert.Equal(0, outcome.Fired);
        Assert.Equal(1, outcome.SuppressedByCooldown);
        Assert.Equal(1, _audit.CountOf(AuditEventType.WatchSuppressed));
    }

    [Fact]
    public async Task Backpressure_stops_a_firing_watch_from_starting_a_cycle()
    {
        Seeded(cooldown: TimeSpan.FromSeconds(30));

        // One cycle already running for this capability against a ceiling of one.
        var limits = AdmissionLimits.Create(4, 1, 10, 6, TimeSpan.FromHours(1));

        await Evaluator(limits).OfferAsync(Signal(Now));

        _clock.Advance(TimeSpan.FromMinutes(1));

        var outcome = await Evaluator(limits).OfferAsync(Signal(_clock.UtcNow));

        Assert.Equal(1, outcome.Fired);
        Assert.Equal(0, outcome.Started);
        Assert.Equal(1, outcome.SuppressedByBackpressure);
        Assert.Equal(1, _audit.CountOf(AuditEventType.WatchSuppressed));
    }

    /// <summary>
    /// Ceilings that could not be read admit nothing, so a storm during a configuration failure
    /// starts no cycles at all rather than all of them.
    /// </summary>
    [Fact]
    public async Task Unreadable_ceilings_admit_nothing()
    {
        Seeded();

        var outcome = await Evaluator(AdmissionLimits.FailClosed).OfferAsync(Signal(Now));

        Assert.Equal(1, outcome.Fired);
        Assert.Equal(0, outcome.Started);
        Assert.Equal(1, outcome.SuppressedByBackpressure);
    }

    [Fact]
    public async Task An_observation_no_watch_wants_starts_nothing()
    {
        Seeded();

        var outcome = await Evaluator().OfferAsync(Signal(Now, value: 0.001m));

        Assert.Equal(1, outcome.Evaluated);
        Assert.Equal(0, outcome.Fired);
        Assert.Empty(_cycles.Cycles);
    }

    /// <summary>
    /// A cycle carries the correlation, capability and template of the watch that started it, so the
    /// whole run is reconstructable from the row.
    /// </summary>
    [Fact]
    public async Task A_started_cycle_records_where_it_came_from()
    {
        var watch = Seeded();

        await Evaluator().OfferAsync(Signal(Now));

        var cycle = Assert.Single(_cycles.Cycles);

        Assert.Equal(watch.WatchId, cycle.WatchId);
        Assert.Equal(watch.CycleTemplate, cycle.TemplateName);
        Assert.Equal(watch.Capability, cycle.Capability);
        Assert.Equal(watch.FiringKeyFor(Signal(Now)), cycle.TriggerKey);
        Assert.Equal(CycleStatus.Running, cycle.Status);
        Assert.Equal(CycleStages.First, cycle.Stage);
    }
}
