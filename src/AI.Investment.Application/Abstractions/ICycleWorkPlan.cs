using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Abstractions;

/// <summary>What a cycle knows about itself while a stage runs.</summary>
public sealed record CycleStageContext(
    Guid CycleId,
    Capability Capability,
    string TemplateName,
    CycleStage Stage,
    DateTime NowUtc);

/// <summary>
/// What one stage did, in the terms the loop needs: what it cost, and what it noticed.
/// </summary>
/// <remarks>
/// The costs are how a budget is enforced, and they are reported by the stage rather than measured
/// by the loop because only the stage knows what it spent. The flags are the inputs to the
/// escalation policy that a stage - not the loop - can observe: whether the evidence was
/// trustworthy, whether a provider failed, whether this looks like something new.
/// </remarks>
public sealed record CycleStageResult
{
    /// <summary>A stage that spent nothing and noticed nothing.</summary>
    public static CycleStageResult Nothing(Currency currency) =>
        new() { ModelSpend = Money.Zero(currency) };

    /// <summary>What this stage spent at a model provider.</summary>
    public required Money ModelSpend { get; init; }

    /// <summary>How many provider calls it made.</summary>
    public int ProviderCalls { get; init; }

    /// <summary>
    /// The action this stage produced, when it produced one. Only <see cref="CycleStage.ProposeAction"/>
    /// is expected to return one.
    /// </summary>
    public ActionProposal? Proposal { get; init; }

    /// <summary>True when the evidence was stale, quarantined or uncorroborated.</summary>
    public bool EvidenceUntrustworthy { get; init; }

    /// <summary>True when a provider failed or a step exhausted its retries.</summary>
    public bool ProviderFailed { get; init; }

    /// <summary>True when this falls outside the pattern the capability has operated within.</summary>
    public bool IsNovel { get; init; }

    /// <summary>The stage's stated confidence, when it had one.</summary>
    public Confidence? Confidence { get; init; }
}

/// <summary>
/// The work one cycle template does, stage by stage.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the operating loop and the analysis it drives. Phase 6 builds the loop - the
/// state machine, the budgets, the cooldowns, the backpressure, the grants, the escalations and the
/// outbox - and deliberately ships no analytical plan, because what a cycle should analyse is the
/// subject of the earlier phases and of the ones after. A template with no registered plan does not
/// silently do nothing: the cycle escalates and suspends, which is the fail-closed reading of "this
/// installation is configured to run something that does not exist".
/// </para>
/// <para>
/// A plan never decides whether it is allowed to act. It returns a proposal; the loop takes it
/// through limits, autonomy, policy and approval, and only the gateway ever invokes an effect.
/// </para>
/// </remarks>
public interface ICycleWorkPlan
{
    /// <summary>The template name this plan answers for.</summary>
    string TemplateName { get; }

    /// <summary>Runs one stage.</summary>
    Task<CycleStageResult> RunStageAsync(
        CycleStageContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the effect the gateway has authorised, and returns a short description of what it did.
    /// </summary>
    /// <remarks>
    /// Invoked only by the action gateway, only inside an authorisation window, and only after the
    /// policy engine has returned Execute. A plan that performed its effect in
    /// <see cref="RunStageAsync"/> instead would have bypassed the gate, which is why the proposal
    /// and the effect are separated here as they are everywhere else in this system.
    /// </remarks>
    Task<string> ExecuteAsync(
        ActionProposal proposal,
        AutonomyResolution autonomy,
        CancellationToken cancellationToken = default);
}
