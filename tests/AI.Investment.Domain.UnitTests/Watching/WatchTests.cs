using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Watching;

/// <summary>
/// The mechanism that removes "the human opens the dashboard and asks", and the controls that stop
/// it becoming "the platform wakes up four hundred times a night".
/// </summary>
public sealed class WatchTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Watch Watch(
        TriggerType type = TriggerType.PriceMove,
        TriggerCondition? condition = null,
        TimeSpan? cooldown = null,
        string? identifier = "AAPL",
        TimeSpan? maxSignalAge = null) =>
        Domain.Watching.Watch.Create(
            "watchlist price move",
            WatchTarget.Create("Security", identifier),
            type,
            condition ?? TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 0.05m),
            cooldown ?? TimeSpan.FromMinutes(30),
            Capability.Analysis,
            "monitor-watchlist",
            Now,
            maxSignalAge);

    private static TriggerSignal Signal(
        TriggerType type = TriggerType.PriceMove,
        string? identifier = "AAPL",
        decimal? value = 0.07m,
        DateTime? observedAtUtc = null) =>
        TriggerSignal.Create(
            type,
            WatchTarget.Create("Security", identifier),
            observedAtUtc ?? Now,
            value);

    [Fact]
    public void A_watch_fires_when_its_condition_holds()
    {
        var decision = Watch().Evaluate(Signal(), Now);

        Assert.True(decision.Fires);
        Assert.Equal(WatchRefusal.None, decision.Refusal);
        Assert.Contains("monitor-watchlist", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watch_does_not_fire_when_the_condition_does_not_hold()
    {
        var decision = Watch().Evaluate(Signal(value: 0.01m), Now);

        Assert.False(decision.Fires);
        Assert.Equal(WatchRefusal.ConditionNotMet, decision.Refusal);
    }

    [Fact]
    public void A_watch_ignores_an_observation_of_another_kind_or_about_something_else()
    {
        Assert.Equal(
            WatchRefusal.TypeMismatch,
            Watch().Evaluate(Signal(type: TriggerType.NewsEvent), Now).Refusal);

        Assert.Equal(
            WatchRefusal.TargetMismatch,
            Watch().Evaluate(Signal(identifier: "MSFT"), Now).Refusal);
    }

    /// <summary>A watch with no identifier covers every instance of its kind.</summary>
    [Fact]
    public void A_watch_with_no_identifier_covers_the_whole_kind()
    {
        var decision = Watch(identifier: null).Evaluate(Signal(identifier: "MSFT"), Now);

        Assert.True(decision.Fires);
    }

    [Fact]
    public void A_disabled_watch_never_fires()
    {
        var watch = Watch();

        watch.Disable("too noisy during earnings", Now);

        var decision = watch.Evaluate(Signal(), Now);

        Assert.False(decision.Fires);
        Assert.Equal(WatchRefusal.Disabled, decision.Refusal);
        Assert.Contains("too noisy", decision.Reason, StringComparison.Ordinal);

        watch.Enable(Now);

        Assert.True(watch.Evaluate(Signal(), Now).Fires);
    }

    /// <summary>
    /// The control that matters most. One volatile session against a watch with no cooldown is a
    /// thousand cycles and a bill nobody authorised.
    /// </summary>
    [Fact]
    public void A_watch_inside_its_cooldown_does_not_fire_again()
    {
        var watch = Watch(cooldown: TimeSpan.FromMinutes(30));

        Assert.True(watch.Evaluate(Signal(), Now).Fires);

        watch.RecordFiring(Now);

        var suppressed = watch.Evaluate(Signal(observedAtUtc: Now.AddMinutes(10)), Now.AddMinutes(10));

        Assert.False(suppressed.Fires);
        Assert.Equal(WatchRefusal.WithinCooldown, suppressed.Refusal);

        var afterwards = watch.Evaluate(Signal(observedAtUtc: Now.AddMinutes(31)), Now.AddMinutes(31));

        Assert.True(afterwards.Fires);
    }

    [Fact]
    public void A_cooldown_shorter_than_the_floor_is_refused()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            Watch(cooldown: TimeSpan.FromSeconds(1)));

        Assert.Contains("thousand cycles", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backlog replayed after an outage would otherwise start a cycle for every price move of the
    /// last two days, all at once, all acting on prices that have since moved again.
    /// </summary>
    [Fact]
    public void An_observation_older_than_the_watch_acts_on_does_not_fire()
    {
        var watch = Watch(maxSignalAge: TimeSpan.FromMinutes(10));

        var decision = watch.Evaluate(Signal(observedAtUtc: Now.AddMinutes(-30)), Now);

        Assert.False(decision.Fires);
        Assert.Equal(WatchRefusal.SignalTooOld, decision.Refusal);
    }

    [Fact]
    public void An_observation_dated_in_the_future_does_not_fire()
    {
        var decision = Watch().Evaluate(Signal(observedAtUtc: Now.AddMinutes(5)), Now);

        Assert.False(decision.Fires);
        Assert.Equal(WatchRefusal.SignalInFuture, decision.Refusal);
    }

    /// <summary>
    /// The deduplication key. The same observation delivered twice produces the same key, and the
    /// cycle store's unique index turns a redelivered ten minutes into no new work.
    /// </summary>
    [Fact]
    public void The_firing_key_is_stable_for_the_same_observation_and_differs_for_another()
    {
        var watch = Watch();
        var signal = Signal();

        Assert.Equal(watch.FiringKeyFor(signal), watch.FiringKeyFor(Signal()));

        Assert.NotEqual(
            watch.FiringKeyFor(signal),
            watch.FiringKeyFor(Signal(observedAtUtc: Now.AddSeconds(1))));

        Assert.NotEqual(watch.FiringKeyFor(signal), Watch().FiringKeyFor(signal));
    }

    [Fact]
    public void Recording_a_firing_starts_the_cooldown_and_counts()
    {
        var watch = Watch();

        watch.RecordFiring(Now);

        Assert.Equal(Now, watch.LastFiredAtUtc);
        Assert.Equal(1, watch.FireCount);
    }

    // ---- Conditions ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TriggerComparison.AtOrAbove, 100, 100, true)]
    [InlineData(TriggerComparison.AtOrAbove, 100, 99, false)]
    [InlineData(TriggerComparison.AtOrBelow, 100, 100, true)]
    [InlineData(TriggerComparison.AtOrBelow, 100, 101, false)]
    [InlineData(TriggerComparison.MovedAtLeast, 5, -6, true)]
    [InlineData(TriggerComparison.MovedAtLeast, 5, -4, false)]
    public void A_threshold_condition_compares_exactly(
        TriggerComparison comparison,
        decimal threshold,
        decimal observed,
        bool expected)
    {
        var condition = TriggerCondition.Compare(comparison, threshold);

        Assert.Equal(expected, condition.IsMet(observed, null, Now, Now));
    }

    /// <summary>
    /// Fail closed: an observation with no value, or a condition this build cannot interpret, does
    /// not fire. The dangerous misreading is the one that fires on everything.
    /// </summary>
    [Fact]
    public void A_condition_that_cannot_be_evaluated_does_not_fire()
    {
        var condition = TriggerCondition.Compare(TriggerComparison.AtOrAbove, 100m);

        Assert.False(condition.IsMet(null, null, Now, Now));
    }

    [Fact]
    public void A_schedule_fires_when_the_interval_has_elapsed_since_the_last_firing()
    {
        var condition = TriggerCondition.Every(TimeSpan.FromHours(1));

        Assert.False(condition.IsMet(null, Now, Now, Now.AddMinutes(30)));
        Assert.True(condition.IsMet(null, Now, Now, Now.AddMinutes(60)));

        // Never fired: measured from when the watch was created instead.
        Assert.True(condition.IsMet(null, null, Now, Now.AddMinutes(61)));
        Assert.False(condition.IsMet(null, null, Now, Now.AddMinutes(1)));
    }

    [Fact]
    public void A_zero_interval_schedule_is_refused()
    {
        var error = Assert.Throws<DomainValidationException>(() => TriggerCondition.Every(TimeSpan.Zero));

        Assert.Contains("trigger storm", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_that_does_not_compare_a_value_is_refused_as_a_threshold()
    {
        Assert.Throws<DomainValidationException>(() =>
            TriggerCondition.Compare(TriggerComparison.IntervalElapsed, 1m));

        Assert.Throws<DomainValidationException>(() =>
            TriggerCondition.Compare(TriggerComparison.Unknown, 1m));

        Assert.Throws<DomainValidationException>(() =>
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, -1m));
    }

    [Fact]
    public void An_any_observation_condition_fires_on_arrival()
    {
        var watch = Watch(type: TriggerType.NewFiling, condition: TriggerCondition.OnAnyObservation());

        Assert.True(watch.Evaluate(Signal(type: TriggerType.NewFiling, value: null), Now).Fires);
    }

    [Fact]
    public void A_watch_and_a_signal_refuse_the_unknown_trigger_type()
    {
        Assert.Throws<DomainValidationException>(() => Watch(type: TriggerType.Unknown));

        Assert.Throws<DomainValidationException>(() => TriggerSignal.Create(
            TriggerType.Unknown, WatchTarget.Create("Security", "AAPL"), Now));
    }

    [Fact]
    public void Evaluation_requires_a_utc_instant() =>
        Assert.Throws<DomainValidationException>(() =>
            Watch().Evaluate(Signal(), DateTime.SpecifyKind(Now, DateTimeKind.Local)));
}
