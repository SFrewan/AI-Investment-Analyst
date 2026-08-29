using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Watching;

namespace AI.Investment.Application.Operations;

/// <summary>What one pass of the schedule ticker did.</summary>
/// <param name="Examined">Enabled schedule watches considered.</param>
/// <param name="Due">Watches whose interval had elapsed.</param>
/// <param name="Offered">Distinct signals offered to the evaluator.</param>
/// <param name="Started">Cycles the evaluator actually created.</param>
/// <param name="Suppressed">Firings the evaluator's own controls held back.</param>
public sealed record ScheduleTickOutcome(int Examined, int Due, int Offered, int Started, int Suppressed);

/// <summary>
/// Produces the observations a scheduled watch waits for, which nothing else in the platform did.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> Every other trigger type describes something that arrives:
/// a filing appears, a price moves, a metric crosses. <see cref="TriggerType.Schedule"/> describes
/// something that does not - the passage of time - and so there was nothing to deliver it.
/// <c>TriggerEvaluator.OfferAsync</c> was reachable only from tests, which meant a scheduled watch
/// could be created, stored and enabled and would still never fire. This is the missing caller, and
/// it is deliberately the only new thing: cycle creation, admission, budgets, deduplication and
/// audit all stay exactly where they were.
/// </para>
/// <para>
/// <strong>The due instant is computed, never "now".</strong> This is the whole design. A signal
/// stamped with the wall clock would carry a different <c>ObservedAtUtc</c> on every tick, so
/// <c>Watch.FiringKeyFor</c> would produce a different key every time and the cycle store's unique
/// index - the thing that turns a redelivery into one cycle rather than a hundred - would never
/// match anything. Instead the signal is stamped with the interval boundary the watch is due at,
/// derived only from state already persisted on the watch. Two workers ticking a second apart, or
/// the same worker ticking sixty times before a cycle is picked up, compute the same instant, build
/// the same key, and produce one cycle.
/// </para>
/// <para>
/// <strong>The latest boundary, not the first.</strong> The boundary is the most recent one at or
/// before now rather than the first one after the watch was last due. Taking the first would mean a
/// platform that had been stopped for a week came back and replayed every boundary it had missed,
/// which is the backlog the domain's <c>MaxSignalAge</c> exists to refuse.
/// </para>
/// <para>
/// <strong>It decides nothing.</strong> This class computes a timestamp and hands the signal over.
/// Whether the watch may fire is <c>Watch.Evaluate</c>'s answer - enabled, type, target, signal age,
/// cooldown, condition - and whether a cycle may start is the evaluator's. A ticker that made any of
/// those judgements itself would be a second copy of a rule that already exists, and the two would
/// eventually disagree.
/// </para>
/// </remarks>
public sealed class ScheduleTicker
{
    private readonly IWatchStore _watches;
    private readonly TriggerEvaluator _evaluator;
    private readonly IClock _clock;

    public ScheduleTicker(IWatchStore watches, TriggerEvaluator evaluator, IClock clock)
    {
        _watches = watches ?? throw new ArgumentNullException(nameof(watches));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Offers one signal per due schedule watch, and returns what came of it.</summary>
    public async Task<ScheduleTickOutcome> TickAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var candidates = await _watches
            .GetEnabledAsync(TriggerType.Schedule, cancellationToken)
            .ConfigureAwait(false);

        // Two watches on the same target that fall due at the same instant are one observation, not
        // two. Keyed on the pair that decides identity, so the evaluator is not offered the same
        // reading twice.
        var signals = new Dictionary<string, TriggerSignal>(StringComparer.Ordinal);
        var due = 0;

        foreach (var watch in candidates)
        {
            if (DueInstant(watch, now) is not { } dueAtUtc)
            {
                continue;
            }

            due++;

            var key = string.Create(CultureInfo.InvariantCulture, $"{watch.Target}@{dueAtUtc:O}");

            if (!signals.ContainsKey(key))
            {
                signals[key] = TriggerSignal.Create(TriggerType.Schedule, watch.Target, dueAtUtc);
            }
        }

        var started = 0;
        var suppressed = 0;

        foreach (var signal in signals.Values)
        {
            var outcome = await _evaluator.OfferAsync(signal, cancellationToken).ConfigureAwait(false);

            started += outcome.Started;
            suppressed += outcome.Suppressed;
        }

        return new ScheduleTickOutcome(candidates.Count, due, signals.Count, started, suppressed);
    }

    /// <summary>
    /// The interval boundary a schedule watch is currently due at, or null when it is not due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <c>TriggerCondition.IsMet</c> for <c>IntervalElapsed</c> exactly: the reference is the
    /// watch's last firing, or its creation when it has never fired, and it is due once a whole
    /// interval has passed since. The boundary returned is that reference plus as many whole
    /// intervals as have elapsed, which is the most recent instant at which the watch became due.
    /// </para>
    /// <para>
    /// Because the reference only moves when the watch fires, a boundary that goes unused does not
    /// strand the watch: the next one arrives one interval later, and the one after that, so a
    /// missed window costs a cycle rather than the schedule.
    /// </para>
    /// </remarks>
    internal static DateTime? DueInstant(Watch watch, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(watch);

        if (watch.TriggerType != TriggerType.Schedule)
        {
            return null;
        }

        if (watch.Condition.Interval is not { } interval || interval <= TimeSpan.Zero)
        {
            // A schedule with no interval fires never rather than always. The domain refuses to
            // build one; this refuses to invent a boundary for one that somehow exists.
            return null;
        }

        var since = watch.LastFiredAtUtc ?? watch.CreatedAtUtc;
        var elapsed = nowUtc - since;

        if (elapsed < interval)
        {
            return null;
        }

        var wholeIntervals = elapsed.Ticks / interval.Ticks;

        return since.AddTicks(wholeIntervals * interval.Ticks);
    }
}
