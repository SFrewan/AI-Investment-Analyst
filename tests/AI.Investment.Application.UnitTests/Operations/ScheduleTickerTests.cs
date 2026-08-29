using AI.Investment.Application.Operations;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>
/// The caller a scheduled watch never had.
/// </summary>
/// <remarks>
/// <para>
/// Every other trigger type describes something arriving. <see cref="TriggerType.Schedule"/>
/// describes the passage of time, so nothing delivered it and a scheduled watch could be created,
/// stored, enabled and never fire. These tests are about the two properties that make the fix
/// correct rather than merely present: the signal is stamped with a computed boundary rather than
/// the wall clock, so repeated ticks deduplicate; and every existing control still decides.
/// </para>
/// </remarks>
public sealed class ScheduleTickerTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly FakeClock _clock = new(Now);
    private readonly InMemoryWatchStore _watches = new();
    private readonly InMemoryCycleStore _cycles = new();
    private readonly RecordingAuditSink _audit = new();

    // ---- the gap this closes -----------------------------------------------------------------

    [Fact]
    public async Task A_scheduled_watch_whose_interval_has_elapsed_starts_one_cycle()
    {
        var watch = Seeded();

        _clock.Advance(Interval);

        var tick = await Ticker().TickAsync();

        Assert.Equal(1, tick.Examined);
        Assert.Equal(1, tick.Due);
        Assert.Equal(1, tick.Offered);
        Assert.Equal(1, tick.Started);

        var cycle = Assert.Single(_cycles.Cycles);

        Assert.Equal(watch.WatchId, cycle.WatchId!.Value);
        Assert.Equal("equity-price-review", cycle.TemplateName);
        Assert.Equal(1, watch.FireCount);
        Assert.Equal(1, _audit.CountOf(AuditEventType.WatchFired));
    }

    [Fact]
    public async Task A_scheduled_watch_whose_interval_has_not_elapsed_starts_nothing()
    {
        Seeded();

        _clock.Advance(Interval - TimeSpan.FromMinutes(1));

        var tick = await Ticker().TickAsync();

        Assert.Equal(1, tick.Examined);
        Assert.Equal(0, tick.Due);
        Assert.Equal(0, tick.Offered);
        Assert.Equal(0, tick.Started);
        Assert.Empty(_cycles.Cycles);
    }

    // ---- the property that makes it idempotent -----------------------------------------------

    /// <summary>
    /// The heart of it. A signal stamped with the wall clock would carry a different
    /// <c>ObservedAtUtc</c> on every tick, so <c>FiringKeyFor</c> would produce a different key each
    /// time and the store's unique index would never deduplicate anything.
    /// </summary>
    [Fact]
    public void The_due_instant_is_the_boundary_and_does_not_move_with_the_wall_clock()
    {
        var watch = ScheduleWatch();

        var atBoundary = ScheduleTicker.DueInstant(watch, Now + Interval);
        var aSecondLater = ScheduleTicker.DueInstant(watch, Now + Interval + TimeSpan.FromSeconds(1));
        var muchLater = ScheduleTicker.DueInstant(watch, Now + Interval + TimeSpan.FromMinutes(37));

        Assert.Equal(Now + Interval, atBoundary!.Value);
        Assert.Equal(atBoundary, aSecondLater);
        Assert.Equal(atBoundary, muchLater);
    }

    /// <summary>
    /// The same boundary produces the same firing key, which is what the cycle store's unique index
    /// compares. Two workers ticking a second apart write one cycle, not two.
    /// </summary>
    [Fact]
    public void Two_ticks_inside_one_window_produce_the_same_firing_key()
    {
        var watch = ScheduleWatch();

        var first = ScheduleTicker.DueInstant(watch, Now + Interval);
        var second = ScheduleTicker.DueInstant(watch, Now + Interval + TimeSpan.FromMinutes(20));

        var firstKey = watch.FiringKeyFor(
            TriggerSignal.Create(TriggerType.Schedule, watch.Target, first!.Value));
        var secondKey = watch.FiringKeyFor(
            TriggerSignal.Create(TriggerType.Schedule, watch.Target, second!.Value));

        Assert.Equal(firstKey, secondKey);
    }

    [Fact]
    public async Task Ticking_repeatedly_inside_one_window_starts_exactly_one_cycle()
    {
        Seeded();

        _clock.Advance(Interval);

        var first = await Ticker().TickAsync();

        _clock.Advance(TimeSpan.FromSeconds(30));

        var second = await Ticker().TickAsync();

        Assert.Equal(1, first.Started);
        Assert.Equal(0, second.Started);
        Assert.Single(_cycles.Cycles);
    }

    /// <summary>
    /// After an outage the platform takes the most recent boundary, not the first one it missed.
    /// Replaying every missed boundary is exactly the backlog <c>MaxSignalAge</c> exists to refuse.
    /// </summary>
    [Fact]
    public void After_a_long_gap_the_boundary_is_the_most_recent_one_not_the_first_missed()
    {
        var watch = ScheduleWatch();

        var due = ScheduleTicker.DueInstant(watch, Now + TimeSpan.FromHours(25));

        Assert.Equal(Now + TimeSpan.FromHours(24), due!.Value);
    }

    /// <summary>
    /// And the domain still refuses a boundary older than the watch will act on. The ticker does not
    /// second-guess that: it offers the signal and <c>Watch.Evaluate</c> declines it.
    /// </summary>
    [Fact]
    public async Task A_boundary_older_than_the_watchs_max_signal_age_starts_nothing()
    {
        Seeded();

        // Default MaxSignalAge is one hour; two hours past a six-hourly boundary is stale.
        _clock.Advance(Interval + TimeSpan.FromHours(2));

        var tick = await Ticker().TickAsync();

        Assert.Equal(1, tick.Due);
        Assert.Equal(1, tick.Offered);
        Assert.Equal(0, tick.Started);
        Assert.Empty(_cycles.Cycles);
    }

    // ---- every existing control still decides -------------------------------------------------

    /// <summary>
    /// The reversal added by the watch-disablement block still works: a disabled watch is not
    /// ticked, because the ticker asks the store for enabled watches and nothing else.
    /// </summary>
    [Fact]
    public async Task A_disabled_watch_is_never_ticked()
    {
        var watch = Seeded();

        watch.Disable("stepping back", Now);

        _clock.Advance(Interval);

        var tick = await Ticker().TickAsync();

        Assert.Equal(0, tick.Examined);
        Assert.Equal(0, tick.Due);
        Assert.Equal(0, tick.Started);
        Assert.Empty(_cycles.Cycles);
    }

    [Fact]
    public async Task A_watch_waiting_for_something_other_than_a_schedule_is_not_ticked()
    {
        _watches.Seed(Watch.Create(
            "price move",
            WatchTarget.Create("Security", "AAPL.US"),
            TriggerType.PriceMove,
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 0.02m),
            TimeSpan.FromHours(1),
            Capability.OpportunityManagement,
            "equity-price-review",
            Now));

        _clock.Advance(TimeSpan.FromDays(2));

        var tick = await Ticker().TickAsync();

        Assert.Equal(0, tick.Examined);
        Assert.Equal(0, tick.Started);
        Assert.Empty(_cycles.Cycles);
    }

    /// <summary>
    /// The watch's own cooldown is still what bounds it. The ticker never touches
    /// <c>LastFiredAtUtc</c>; the evaluator records the firing and the next boundary is measured
    /// from there.
    /// </summary>
    [Fact]
    public async Task The_next_boundary_is_measured_from_the_firing_not_from_creation()
    {
        var watch = Seeded();

        _clock.Advance(Interval);
        await Ticker().TickAsync();

        var firedAt = watch.LastFiredAtUtc;

        Assert.NotNull(firedAt);

        // One more interval measured from creation would be due now; measured from the firing it is
        // not, and the firing is what counts.
        _clock.Advance(Interval - TimeSpan.FromMinutes(1));

        var tick = await Ticker().TickAsync();

        Assert.Equal(0, tick.Due);
        Assert.Single(_cycles.Cycles);
    }

    [Fact]
    public async Task Two_watches_on_the_same_target_and_boundary_are_offered_one_signal()
    {
        Seeded("first");
        Seeded("second");

        _clock.Advance(Interval);

        var tick = await Ticker().TickAsync();

        Assert.Equal(2, tick.Examined);
        Assert.Equal(2, tick.Due);

        // One observation, not two: the same target falling due at the same instant is one reading.
        Assert.Equal(1, tick.Offered);

        // Both watches still get to answer for themselves, and both start their own cycle because
        // their firing keys differ by watch id.
        Assert.Equal(2, tick.Started);
        Assert.Equal(2, _cycles.Cycles.Count);
    }

    // ---- what it cannot do ---------------------------------------------------------------------

    /// <summary>
    /// No provider, no HTTP client, no configuration. A schedule tick cannot call EODHD, because the
    /// ticker is not given anything that could.
    /// </summary>
    [Fact]
    public void The_ticker_cannot_reach_a_provider_or_the_network()
    {
        var dependencies = typeof(ScheduleTicker)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.DoesNotContain(dependencies, name =>
            name.Contains("Provider", StringComparison.Ordinal) ||
            name.Contains("Http", StringComparison.Ordinal) ||
            name.Contains("Client", StringComparison.Ordinal) ||
            name.Contains("Configuration", StringComparison.Ordinal));
    }

    [Fact]
    public void A_schedule_with_no_interval_is_never_due()
    {
        var watch = Watch.Create(
            "any observation",
            WatchTarget.Create("Security", "AAPL.US"),
            TriggerType.Schedule,
            TriggerCondition.OnAnyObservation(),
            TimeSpan.FromHours(1),
            Capability.OpportunityManagement,
            "equity-price-review",
            Now);

        Assert.Null(ScheduleTicker.DueInstant(watch, Now + TimeSpan.FromDays(30)));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Watch ScheduleWatch(string name = "AAPL daily review") =>
        Watch.Create(
            name,
            WatchTarget.Create("Security", "AAPL.US"),
            TriggerType.Schedule,
            TriggerCondition.Every(Interval),
            TimeSpan.FromMinutes(30),
            Capability.OpportunityManagement,
            "equity-price-review",
            Now);

    private Watch Seeded(string name = "AAPL daily review")
    {
        var watch = ScheduleWatch(name);

        _watches.Seed(watch);

        return watch;
    }

    private ScheduleTicker Ticker() =>
        new(
            _watches,
            new TriggerEvaluator(
                _watches,
                _cycles,
                new FixedAdmissionLimits(AdmissionLimits.Create(4, 2, 10, 6, TimeSpan.FromHours(1))),
                new FixedCycleBudget(CycleBudget.Create(
                    TimeSpan.FromMinutes(10),
                    Money.Create(1m, Currency.Usd),
                    20,
                    1)),
                _audit,
                new FakeCorrelationContext(),
                _clock),
            _clock);
}
