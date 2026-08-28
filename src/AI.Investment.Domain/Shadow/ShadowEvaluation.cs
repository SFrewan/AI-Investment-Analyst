using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Shadow;

/// <summary>
/// Re-asks the same gate what it would have decided one autonomy level up. Pure, and inert.
/// </summary>
/// <remarks>
/// <para>
/// The measurement half of "autonomy is earned". It runs the <em>same</em>
/// <see cref="IPolicyEngine"/> over the <em>same</em> proposal, changing exactly one input: the
/// resolved autonomy mode, raised by one level. Using the real engine rather than a model of it is
/// the point - a shadow measurement produced by a second implementation would measure the second
/// implementation.
/// </para>
/// <para>
/// <strong>Nothing here can act.</strong> It takes a proposal and returns a record. There is no
/// effect delegate in the signature, no gateway, no venue and no authorisation window, so the
/// shadow path has no way to reach one even by mistake. The one thing this method must never do is
/// exist in a form where somebody could pass it an effect.
/// </para>
/// <para>
/// The raised resolution is constructed here and discarded here. It is never returned, never stored
/// and never placed in a context anything else can see, so it cannot leak into a real evaluation.
/// </para>
/// </remarks>
public static class ShadowEvaluation
{
    /// <summary>The reason prefix recorded on a shadow resolution, so it is never mistaken for a grant.</summary>
    public const string ShadowResolutionRule = "autonomy.shadow-measurement@1";

    /// <summary>
    /// Produces the shadow decision for a proposal, or null when there is nothing to measure.
    /// </summary>
    /// <remarks>
    /// Returns null when the action was attended (no resolution), or when the resolved mode is
    /// already the highest there is. Both are cases where "one level up" names nothing, and
    /// recording a row saying so would dilute the count that promotion is judged on.
    /// </remarks>
    public static ShadowDecision? Evaluate(
        IPolicyEngine engine,
        ActionProposal proposal,
        PolicyContext context,
        PolicyDecision actual,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actual);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var autonomy = context.Autonomy;

        if (autonomy is null || autonomy.Mode >= AutonomyMode.ContinuousBounded)
        {
            return null;
        }

        var raisedMode = autonomy.NextModeUp;

        var raised = AutonomyResolution.Create(
            raisedMode,
            autonomy.Band,
            autonomy.AutonomyGrantId,
            $"{ShadowResolutionRule}: measuring what {raisedMode} would have decided. This resolution " +
            "is not in force and authorises nothing.");

        var shadowContext = PolicyContext.Create(
            context.EnvironmentName,
            context.KillSwitch,
            context.Capabilities.Values,
            raised);

        var shadowDecision = engine.Evaluate(proposal, shadowContext, nowUtc);

        return ShadowDecision.Record(
            proposal.CycleId,
            proposal.ProposalId,
            proposal.Capability,
            proposal.ActionType.Value,
            proposal.RiskTier,
            proposal.Economics.EstimatedExposure,
            autonomy.Mode,
            actual.Outcome,
            raisedMode,
            shadowDecision.Outcome,
            shadowDecision.Reason,
            nowUtc);
    }
}
