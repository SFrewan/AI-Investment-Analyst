using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Watching;

namespace AI.Investment.Application.Operations;

/// <summary>What happened when an observation was offered to the watches.</summary>
/// <param name="Evaluated">Watches the observation was compared against.</param>
/// <param name="Fired">Watches whose condition held and whose cooldown had passed.</param>
/// <param name="Started">Cycles actually created.</param>
/// <param name="SuppressedByCooldown">Firings held back by a watch's own cooldown.</param>
/// <param name="SuppressedByBackpressure">Firings held back by a concurrency or rate ceiling.</param>
/// <param name="SuppressedAsDuplicate">Firings for an observation that had already started a cycle.</param>
public sealed record TriggerOutcome(
    int Evaluated,
    int Fired,
    int Started,
    int SuppressedByCooldown,
    int SuppressedByBackpressure,
    int SuppressedAsDuplicate)
{
    /// <summary>Everything that could have started a cycle and did not.</summary>
    public int Suppressed =>
        SuppressedByCooldown + SuppressedByBackpressure + SuppressedAsDuplicate;
}

/// <summary>
/// Offers an observation to the watches and starts the cycles that survive every control.
/// </summary>
/// <remarks>
/// <para>
/// Three independent controls stand between an observation and a cycle, and they exist because they
/// fail differently. The watch's own cooldown bounds one watch; admission control bounds the whole
/// platform, so a market-wide event that fires a thousand different watches at once is still
/// bounded; and the cycle store's unique trigger key bounds redelivery, so a feed replaying its last
/// ten minutes produces the cycles it already produced rather than new ones.
/// </para>
/// <para>
/// <strong>Suppressions are recorded, not discarded.</strong> A suppression count that climbs while
/// the firing count stays flat is the control working. A suppression count of zero during a volatile
/// session is a control that is not, and without the record neither is visible.
/// </para>
/// <para>
/// Nothing here consults a model. The whole path from observation to cycle is comparisons and
/// counts, because the thing that decides to spend money must not itself be the thing that costs
/// money to run.
/// </para>
/// </remarks>
public sealed class TriggerEvaluator
{
    private readonly IWatchStore _watches;
    private readonly ICycleStore _cycles;
    private readonly IAdmissionLimitProvider _limits;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ICycleBudgetProvider _budgets;

    public TriggerEvaluator(
        IWatchStore watches,
        ICycleStore cycles,
        IAdmissionLimitProvider limits,
        ICycleBudgetProvider budgets,
        IAuditSink audit,
        ICorrelationContext correlation,
        IClock clock)
    {
        _watches = watches ?? throw new ArgumentNullException(nameof(watches));
        _cycles = cycles ?? throw new ArgumentNullException(nameof(cycles));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<TriggerOutcome> OfferAsync(
        TriggerSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var now = _clock.UtcNow;
        var limits = await _limits.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await _watches.GetEnabledAsync(signal.Type, cancellationToken).ConfigureAwait(false);

        var evaluated = 0;
        var fired = 0;
        var started = 0;
        var cooldown = 0;
        var backpressure = 0;
        var duplicate = 0;

        foreach (var watch in candidates)
        {
            evaluated++;

            var decision = watch.Evaluate(signal, now);

            if (!decision.Fires)
            {
                if (decision.Refusal == WatchRefusal.WithinCooldown)
                {
                    cooldown++;
                    await RecordSuppressionAsync(watch, decision.Reason, now, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            fired++;

            var admission = AdmissionControl.Admit(
                new AdmissionRequest(
                    watch.Capability,
                    watch.WatchId,
                    await _cycles.CountRunningAsync(cancellationToken).ConfigureAwait(false),
                    await _cycles.CountRunningAsync(watch.Capability, cancellationToken).ConfigureAwait(false),
                    QueuedTriggers: 0,
                    await _cycles.CountStartedByWatchAsync(
                        watch.WatchId,
                        now - limits.Window,
                        cancellationToken).ConfigureAwait(false)),
                limits);

            if (!admission.IsAdmitted)
            {
                backpressure++;
                await RecordSuppressionAsync(watch, admission.Explanation, now, cancellationToken).ConfigureAwait(false);

                continue;
            }

            var budget = await _budgets.GetAsync(watch.CycleTemplate, cancellationToken).ConfigureAwait(false);

            var cycle = OperatingCycle.Start(
                _correlation.Current,
                watch.Capability,
                watch.CycleTemplate,
                watch.FiringKeyFor(signal),
                budget,
                budget.MaxModelSpend.Currency,
                now,
                watch.WatchId);

            var added = await _cycles.TryAddAsync(cycle, cancellationToken).ConfigureAwait(false);

            if (!added)
            {
                // The same observation already started this cycle - a redelivery, or another worker
                // that got there first. Not an error, and not something to retry.
                duplicate++;
                await RecordSuppressionAsync(
                    watch,
                    $"a cycle already exists for trigger key '{cycle.TriggerKey}'.",
                    now,
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            started++;
            watch.RecordFiring(now);

            await _audit.RecordAsync(
                AuditRecord.ForOperation(
                    _correlation.Current,
                    AuditEventType.WatchFired,
                    "operations.watch",
                    decision.Reason,
                    now,
                    cycle.CycleId,
                    watch.Capability,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["watch.id"] = watch.WatchId.ToString("d", System.Globalization.CultureInfo.InvariantCulture),
                        ["watch.name"] = watch.Name,
                        ["cycle.template"] = watch.CycleTemplate,
                        ["cycle.triggerKey"] = cycle.TriggerKey,
                    }),
                cancellationToken).ConfigureAwait(false);
        }

        await _watches.SaveAsync(cancellationToken).ConfigureAwait(false);

        return new TriggerOutcome(evaluated, fired, started, cooldown, backpressure, duplicate);
    }

    private Task RecordSuppressionAsync(
        Watch watch,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                AuditEventType.WatchSuppressed,
                "operations.watch",
                $"Watch '{watch.Name}' did not start a cycle: {reason}",
                nowUtc,
                cycleId: null,
                watch.Capability,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["watch.id"] = watch.WatchId.ToString("d", System.Globalization.CultureInfo.InvariantCulture),
                    ["watch.name"] = watch.Name,
                }),
            cancellationToken);
}
