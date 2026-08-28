using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Operations;

namespace AI.Investment.Application.Operations;

/// <summary>What one attempt to move a cycle forward achieved.</summary>
public sealed record CycleRunResult(
    Guid CycleId,
    CycleStatus Status,
    CycleStage Stage,
    string Summary,
    bool Escalated,
    bool Leased);

/// <summary>
/// Moves one operating cycle forward, enforcing every control on the way.
/// </summary>
/// <remarks>
/// <para>
/// The runner owns sequencing and nothing else. Every judgement belongs somewhere that can be tested
/// without it: whether an action is permitted is <c>PolicyEngine</c>, whether it breaches a ceiling
/// is <c>LimitEngine</c>, how much autonomy applies is <c>AutonomyResolver</c>, whether a human must
/// be told is <c>EscalationPolicy</c>, and whether the budget is spent is <c>CycleBudget</c>. All
/// five are pure. What is here is the order they are asked in, and the fact that a "no" from any of
/// them stops the cycle.
/// </para>
/// <para><strong>The invariants this class must never lose:</strong></para>
/// <list type="number">
/// <item>A stage's work happens once. The stage is persisted before the next one begins, and a
/// re-run of an already-advanced cycle skips what is already done.</item>
/// <item>The effect is invoked by the gateway and by nothing else. The plan supplies a delegate; the
/// runner never calls it.</item>
/// <item>Limits are evaluated before the gate, and a breach stops the cycle without a dispatch. A
/// breached ceiling is the decision.</item>
/// <item>An autonomy scope is open for the whole of the dispatch and closed immediately afterwards.
/// Outside it, a cycle-driven proposal is refused structurally by the policy engine.</item>
/// <item>Anything other than execution escalates and suspends. A cycle never proceeds past a
/// decision it did not get.</item>
/// </list>
/// <para>
/// <strong>Resumable.</strong> Every exit point leaves the cycle's stage and status written down, so
/// the next worker continues rather than restarting. A crash mid-stage re-runs that stage, which is
/// why the effect behind it carries an idempotency key: the seam suppresses the repeat.
/// </para>
/// </remarks>
public sealed class OperatingCycleRunner
{
    private readonly ICycleStore _cycles;
    private readonly IEnumerable<ICycleWorkPlan> _plans;
    private readonly IAutonomyGrantStore _grants;
    private readonly IAutonomyContext _autonomy;
    private readonly IPolicyContextProvider _policyContext;
    private readonly IActionGateway _gateway;
    private readonly ILimitProvider _limits;
    private readonly IExposureProvider _exposure;
    private readonly EscalationService _escalations;
    private readonly ShadowRecorder _shadow;
    private readonly IAuditSink _audit;
    private readonly IClock _clock;

    public OperatingCycleRunner(
        ICycleStore cycles,
        IEnumerable<ICycleWorkPlan> plans,
        IAutonomyGrantStore grants,
        IAutonomyContext autonomy,
        IPolicyContextProvider policyContext,
        IActionGateway gateway,
        ILimitProvider limits,
        IExposureProvider exposure,
        EscalationService escalations,
        ShadowRecorder shadow,
        IAuditSink audit,
        IClock clock)
    {
        _cycles = cycles ?? throw new ArgumentNullException(nameof(cycles));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _autonomy = autonomy ?? throw new ArgumentNullException(nameof(autonomy));
        _policyContext = policyContext ?? throw new ArgumentNullException(nameof(policyContext));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _exposure = exposure ?? throw new ArgumentNullException(nameof(exposure));
        _escalations = escalations ?? throw new ArgumentNullException(nameof(escalations));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>How long a worker holds a cycle before another may take it. Survives a crash.</summary>
    public static TimeSpan LeaseDuration { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The tier at or above which an action reaches a human whatever else is true.</summary>
    public static RiskTier EscalateAtOrAbove { get; } = RiskTier.High;

    public async Task<CycleRunResult> RunAsync(
        Guid cycleId,
        string worker,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        var cycle = await _cycles.FindAsync(cycleId, cancellationToken).ConfigureAwait(false);

        if (cycle is null)
        {
            return new CycleRunResult(cycleId, CycleStatus.Unknown, CycleStage.Unknown,
                "no such cycle", Escalated: false, Leased: false);
        }

        var now = _clock.UtcNow;

        if (!cycle.IsRunning)
        {
            return Result(cycle, "the cycle is not running", escalated: false, leased: false);
        }

        if (!cycle.TryLease(worker, now, LeaseDuration))
        {
            // Another worker holds it. Not an error and not something to retry immediately: the
            // lease exists precisely so two healthy workers do not do the same work twice.
            return Result(cycle, "another worker holds the lease", escalated: false, leased: false);
        }

        await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);

        var plan = _plans.FirstOrDefault(candidate =>
            string.Equals(candidate.TemplateName, cycle.TemplateName, StringComparison.Ordinal));

        if (plan is null)
        {
            await SuspendWithEscalationAsync(
                cycle,
                EscalationReason.ProviderFailure,
                $"no work plan is registered for template '{cycle.TemplateName}', so this cycle " +
                "cannot run. A cycle that quietly did nothing would look exactly like one that had " +
                "nothing to do.",
                proposalId: null,
                cancellationToken).ConfigureAwait(false);

            return Result(cycle, "no work plan registered", escalated: true, leased: true);
        }

        return await DriveAsync(cycle, plan, worker, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CycleRunResult> DriveAsync(
        OperatingCycle cycle,
        ICycleWorkPlan plan,
        string worker,
        CancellationToken cancellationToken)
    {
        ActionProposal? pending = null;
        CycleStageResult? last = null;
        var escalated = false;

        while (cycle.IsRunning)
        {
            var now = _clock.UtcNow;

            // Wall clock is checked every stage rather than only where money is spent, because the
            // ceiling a runaway cycle reaches first is usually time.
            if (cycle.CheckBudget(now).IsExhausted)
            {
                await SuspendedByBudgetAsync(cycle, cancellationToken).ConfigureAwait(false);

                return Result(cycle, cycle.StoppedReason ?? "budget exhausted", escalated: true, leased: true);
            }

            var stage = cycle.Stage;

            if (stage == CycleStage.PolicyGate)
            {
                var gate = await GateAsync(cycle, plan, pending, last, cancellationToken).ConfigureAwait(false);

                escalated |= gate;

                if (!cycle.IsRunning)
                {
                    return Result(cycle, cycle.StoppedReason ?? "stopped at the policy gate", escalated, leased: true);
                }
            }
            else
            {
                last = await plan.RunStageAsync(
                    new CycleStageContext(cycle.CycleId, cycle.Capability, cycle.TemplateName, stage, now),
                    cancellationToken).ConfigureAwait(false);

                if (stage == CycleStage.ProposeAction)
                {
                    pending = last.Proposal;
                }

                var verdict = cycle.Consume(last.ModelSpend, last.ProviderCalls, 0, _clock.UtcNow);

                if (verdict.IsExhausted)
                {
                    await SuspendedByBudgetAsync(cycle, cancellationToken).ConfigureAwait(false);

                    return Result(cycle, cycle.StoppedReason ?? "budget exhausted", escalated: true, leased: true);
                }
            }

            if (cycle.Stage == CycleStages.Last)
            {
                cycle.Complete(_clock.UtcNow);
                await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);
                await RecordCycleFinishedAsync(cycle, AuditEventType.CycleCompleted, cancellationToken)
                    .ConfigureAwait(false);

                return Result(cycle, "completed", escalated, leased: true);
            }

            var next = CycleStages.Next(cycle.Stage);

            if (next is null)
            {
                // Unreachable while Record is the last stage; present so that adding one without
                // updating CycleStages stops the cycle rather than looping on it.
                cycle.Fail("the cycle reached a stage with no successor.", _clock.UtcNow);
                await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);

                return Result(cycle, "no successor stage", escalated, leased: true);
            }

            cycle.Advance(next.Value, _clock.UtcNow);
            await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        cycle.ReleaseLease(worker, _clock.UtcNow);
        await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);

        return Result(cycle, cycle.StoppedReason ?? "stopped", escalated, leased: true);
    }

    /// <summary>
    /// Takes a proposal through limits, autonomy, policy and the shadow measurement.
    /// </summary>
    /// <returns>True when the cycle escalated.</returns>
    private async Task<bool> GateAsync(
        OperatingCycle cycle,
        ICycleWorkPlan plan,
        ActionProposal? proposal,
        CycleStageResult? last,
        CancellationToken cancellationToken)
    {
        if (proposal is null)
        {
            // A cycle that found nothing to propose is a normal, common outcome - most passes of a
            // monitoring loop should reach here - and it is not an escalation.
            return false;
        }

        var now = _clock.UtcNow;

        // 1. Limits first. A breached ceiling is the decision, and it is one the gateway would not
        //    make: the policy engine knows about capabilities and tiers, not about how much of the
        //    book is already committed.
        var limitSet = await _limits.GetAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await _exposure
            .GetAsync(proposal.Economics.EstimatedExposure.Currency, cancellationToken)
            .ConfigureAwait(false);

        var verdict = LimitEngine.Evaluate(proposal, snapshot, limitSet, now);

        // 2. Autonomy. Resolved from grants a human wrote, never from anything this cycle produced.
        var environment = (await _policyContext.GetAsync(cancellationToken).ConfigureAwait(false))
            .EnvironmentName;

        var grants = await _grants
            .GetActiveAsync(proposal.Capability, environment, now, cancellationToken)
            .ConfigureAwait(false);

        var resolution = AutonomyResolver.Resolve(
            AutonomyRequest.Create(
                proposal.Capability,
                proposal.ActionType.Value,
                proposal.RiskTier,
                proposal.Economics.EstimatedExposure,
                environment),
            grants,
            now);

        if (!verdict.IsAllowed)
        {
            await SuspendWithEscalationAsync(
                cycle,
                EscalationReason.LimitBreach,
                $"the action was refused before the gate: {verdict.Explain()}",
                proposal.ProposalId,
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        using (_autonomy.Enter(cycle.CycleId, resolution))
        {
            var outcome = await _gateway
                .DispatchAsync(
                    proposal,
                    ct => plan.ExecuteAsync(proposal, resolution, ct),
                    cancellationToken)
                .ConfigureAwait(false);

            // 3. Measure what one level up would have decided. Inside the scope, because the
            //    measurement is against the resolution that was actually in force.
            var context = await _policyContext.GetAsync(cancellationToken).ConfigureAwait(false);

            await _shadow.RecordAsync(proposal, context, outcome.Decision, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.WasExecuted)
            {
                cycle.Consume(proposal.Economics.EstimatedCost, 0, 1, _clock.UtcNow);
                await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);

                return false;
            }

            if (outcome.Status == ActionOutcomeStatus.DuplicateSuppressed)
            {
                // The effect had already been performed - a retry after a crash - so the cycle
                // continues rather than escalating. This is the resumption path working.
                return false;
            }

            var reason = EscalationPolicy.Required(new EscalationSignals
            {
                RiskTier = proposal.RiskTier,
                EscalateAtOrAbove = EscalateAtOrAbove,
                Reversibility = proposal.Economics.Reversibility,
                ExposureBand = resolution.Band,
                AutonomyMode = resolution.Mode,
                LimitBreached = false,
                BudgetExhausted = false,
                ProviderFailed = last?.ProviderFailed ?? false,
                EvidenceUntrustworthy = last?.EvidenceUntrustworthy ?? false,
                IsNovel = proposal.IsNovel || (last?.IsNovel ?? false),
                Confidence = proposal.Confidence ?? last?.Confidence,
            });

            await SuspendWithEscalationAsync(
                cycle,
                reason == EscalationReason.None ? EscalationReason.NoAutonomyGrant : reason,
                $"the gate answered {outcome.Decision.Outcome}: {outcome.Decision.Reason}",
                proposal.ProposalId,
                cancellationToken).ConfigureAwait(false);

            return true;
        }
    }

    private async Task SuspendedByBudgetAsync(OperatingCycle cycle, CancellationToken cancellationToken)
    {
        // CheckBudget has already suspended the cycle; this records why and tells somebody.
        await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);

        await _escalations.RaiseAsync(
            cycle.Capability,
            EscalationReason.BudgetExhausted,
            cycle.StoppedReason ?? "the cycle exhausted its budget.",
            cycle.CycleId,
            proposalId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await RecordCycleFinishedAsync(cycle, AuditEventType.CycleSuspended, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SuspendWithEscalationAsync(
        OperatingCycle cycle,
        EscalationReason reason,
        string explanation,
        Guid? proposalId,
        CancellationToken cancellationToken)
    {
        if (cycle.IsRunning)
        {
            cycle.Escalate(explanation, _clock.UtcNow);
            await _cycles.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        await _escalations.RaiseAsync(
            cycle.Capability,
            reason,
            explanation,
            cycle.CycleId,
            proposalId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await RecordCycleFinishedAsync(cycle, AuditEventType.CycleSuspended, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task RecordCycleFinishedAsync(
        OperatingCycle cycle,
        AuditEventType eventType,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditRecord.ForOperation(
                cycle.CorrelationId,
                eventType,
                "operations.cycle",
                $"Cycle {cycle.CycleId} {cycle.Status} at {cycle.Stage}" +
                (cycle.StoppedReason is null ? "." : $": {cycle.StoppedReason}"),
                _clock.UtcNow,
                cycle.CycleId,
                cycle.Capability,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cycle.status"] = cycle.Status.ToString(),
                    ["cycle.stage"] = cycle.Stage.ToString(),
                    ["cycle.template"] = cycle.TemplateName,
                    ["cycle.consumption"] = cycle.Consumption.ToString(),
                    ["cycle.escalations"] = OperationsMessages.Number(cycle.EscalationCount),
                }),
            cancellationToken);

    private static CycleRunResult Result(OperatingCycle cycle, string summary, bool escalated, bool leased) =>
        new(cycle.CycleId, cycle.Status, cycle.Stage, summary, escalated, leased);
}
