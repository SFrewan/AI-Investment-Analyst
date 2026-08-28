using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Safety.Tests;

/// <summary>Builders for the continuous-operation safety tests.</summary>
/// <remarks>
/// Everything here produces a valid, permitted object, so each test breaks exactly one thing and the
/// name of the test says which. A fixture that quietly produced an already-refused proposal would
/// make a safety test pass for the wrong reason - which is the failure mode these tests exist to
/// catch elsewhere.
/// </remarks>
internal static class Phase6Fixtures
{
    internal const string Environment = "Test";

    internal static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    internal static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    /// <summary>A proposal the operating loop produced. Carries a cycle identifier.</summary>
    internal static ActionProposal Unattended(
        Capability capability = Capability.SimulatedExecution,
        string actionType = "execution.simulated-order",
        decimal exposure = 1_000m,
        ReversibilityClass reversibility = ReversibilityClass.ReversibleWithCost,
        ProposedBy? proposedBy = null,
        Guid? cycleId = null) =>
        Build(capability, actionType, exposure, reversibility, proposedBy, cycleId ?? Guid.NewGuid());

    /// <summary>A proposal a human or a request produced. Carries no cycle identifier.</summary>
    internal static ActionProposal Attended(
        Capability capability = Capability.SimulatedExecution,
        string actionType = "execution.simulated-order",
        decimal exposure = 1_000m,
        ReversibilityClass reversibility = ReversibilityClass.ReversibleWithCost,
        ProposedBy? proposedBy = null) =>
        Build(capability, actionType, exposure, reversibility, proposedBy, cycleId: null);

    internal static ProposedBy Agent() =>
        ProposedBy.AiAgent("agent.test", "1.0", "prompts/test", "1.0");

    internal static AutonomyGrant Grant(
        Capability capability = Capability.SimulatedExecution,
        string? actionType = null,
        AutonomyMode mode = AutonomyMode.AutoExecuteBounded,
        RiskTier maxRiskTier = RiskTier.Critical,
        decimal maxExposure = 100_000m,
        string environment = Environment,
        TimeSpan? validFor = null) =>
        AutonomyGrant.Issue(
            capability,
            actionType,
            environment,
            mode,
            maxRiskTier,
            Usd(maxExposure),
            "limits.default",
            "operator@example.test",
            Now,
            validFor ?? TimeSpan.FromDays(7));

    /// <summary>
    /// The resolution a grant of <paramref name="mode"/> produces for a permitted action.
    /// </summary>
    /// <remarks>
    /// Built by running the real resolver over a real grant rather than by constructing a resolution
    /// directly. A test that fabricated the value would be testing the fabrication; this way the
    /// modes under test are the ones the resolver can actually produce.
    /// <see cref="AutonomyMode.Unknown"/> is produced the only way it ever is - by resolving against
    /// no grants at all.
    /// </remarks>
    internal static AutonomyResolution Resolution(
        AutonomyMode mode = AutonomyMode.AutoExecuteBounded,
        Capability capability = Capability.SimulatedExecution,
        string actionType = "execution.simulated-order",
        decimal exposure = 1_000m)
    {
        var request = AutonomyRequest.Create(
            capability,
            actionType,
            RiskTier.Medium,
            Usd(exposure),
            Environment);

        AutonomyGrant[] grants = mode == AutonomyMode.Unknown
            ? []
            : [Grant(capability, mode: mode)];

        return AutonomyResolver.Resolve(request, grants, Now);
    }

    /// <summary>A permissive context, optionally carrying a resolved autonomy.</summary>
    internal static PolicyContext Context(
        AutonomyResolution? autonomy = null,
        KillSwitchState killSwitch = KillSwitchState.Disengaged,
        params Capability[] capabilities)
    {
        var enabled = capabilities.Length > 0
            ? capabilities
            :
            [
                Capability.SimulatedExecution,
                Capability.OpportunityManagement,
                Capability.Analysis,
                Capability.AutonomyAdministration,
                Capability.PolicyAdministration,
                Capability.ApprovalAdministration,
            ];

        var policies = enabled
            .Select(capability => CapabilityPolicy.Create(
                capability,
                enabled: true,
                RiskTier.Critical,
                allowIrreversibleAutoExecute: false,
                allowAiProposers: true))
            .ToList();

        return PolicyContext.Create(Environment, killSwitch, policies, autonomy);
    }

    private static ActionProposal Build(
        Capability capability,
        string actionType,
        decimal exposure,
        ReversibilityClass reversibility,
        ProposedBy? proposedBy,
        Guid? cycleId)
    {
        proposedBy ??= ProposedBy.Service("operations.test", "1.0");

        var isAi = proposedBy.IsAi;

        return ActionProposal.Create(
            CorrelationId.New(),
            capability,
            ActionType.Create(actionType),
            ActionTarget.Create("Instrument", "AAPL"),
            new OperationsTestParameters(actionType),
            ActionEconomics.Create(Usd(0m), Usd(exposure), reversibility),
            proposedBy,
            idempotencyKey: Guid.NewGuid().ToString("n"),
            Now,
            cycleId,
            evidence: isAi ? [ClaimId.New()] : null,
            confidence: isAi ? Confidence.Create(0.8m) : null);
    }

    private sealed record OperationsTestParameters(string What) : IActionParameters
    {
        public string Describe() => "operations test: " + What;
    }
}
