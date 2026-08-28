using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Shadow;

/// <summary>
/// What a more autonomous system would have decided, recorded and never acted on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type has no execution surface, and that is its most important property.</strong>
/// It holds no delegate, no venue, no proposal object and no decision object - only the identifiers
/// and the outcomes, as data. There is no method on it that does anything, and an architecture test
/// asserts that nothing in this namespace references the action gateway, the write authorisation,
/// an execution venue or the executor. A shadow decision cannot become an action by being passed to
/// the wrong method, because there is no method to pass it to.
/// </para>
/// <para>
/// The distinction it preserves is the whole point of measuring at all:
/// <see cref="ActualOutcome"/> is what the platform did, under the autonomy a human granted;
/// <see cref="ShadowOutcome"/> is what it would have done one level up. Promotion is then a human
/// decision informed by a number, rather than a guess dressed as one - and the number only means
/// something if the shadow half never touched anything.
/// </para>
/// <para>
/// It is worth being explicit about what <see cref="WouldHaveExecuted"/> means: an action that the
/// platform escalated and a person may well have approved. A high count is not evidence that the
/// system should be promoted; it is the input to the comparison against what those approvals
/// actually decided.
/// </para>
/// </remarks>
public sealed class ShadowDecision
{
    public const int MaxActionTypeLength = 100;

    public const int MaxReasonLength = 1000;

    private ShadowDecision(
        Guid shadowDecisionId,
        Guid? cycleId,
        Guid proposalId,
        Capability capability,
        string actionType,
        RiskTier riskTier,
        Money exposure,
        AutonomyMode actualMode,
        PolicyOutcome actualOutcome,
        AutonomyMode shadowMode,
        PolicyOutcome shadowOutcome,
        string reason,
        DateTime recordedAtUtc)
    {
        ShadowDecisionId = shadowDecisionId;
        CycleId = cycleId;
        ProposalId = proposalId;
        Capability = capability;
        ActionType = actionType;
        RiskTier = riskTier;
        Exposure = exposure;
        ActualMode = actualMode;
        ActualOutcome = actualOutcome;
        ShadowMode = shadowMode;
        ShadowOutcome = shadowOutcome;
        Reason = reason;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private ShadowDecision()
    {
        ActionType = string.Empty;
        Exposure = null!;
        Reason = string.Empty;
    }

    public Guid ShadowDecisionId { get; private set; }

    public Guid? CycleId { get; private set; }

    public Guid ProposalId { get; private set; }

    public Capability Capability { get; private set; }

    public string ActionType { get; private set; }

    public RiskTier RiskTier { get; private set; }

    public Money Exposure { get; private set; }

    /// <summary>The autonomy actually in force.</summary>
    public AutonomyMode ActualMode { get; private set; }

    /// <summary>What the platform actually decided, and acted on.</summary>
    public PolicyOutcome ActualOutcome { get; private set; }

    /// <summary>The autonomy this measurement is about. One level up, and never in force.</summary>
    public AutonomyMode ShadowMode { get; private set; }

    /// <summary>What the same gate would have decided at <see cref="ShadowMode"/>. Never acted on.</summary>
    public PolicyOutcome ShadowOutcome { get; private set; }

    public string Reason { get; private set; }

    public DateTime RecordedAtUtc { get; private set; }

    /// <summary>
    /// True when the shadow gate would have executed and the real one did not.
    /// </summary>
    /// <remarks>
    /// The measurement that matters, and the one to read carefully. It counts actions a human was
    /// asked about instead; whether promoting the capability would have been right depends on what
    /// those humans decided, which is the comparison this record exists to make possible.
    /// </remarks>
    public bool WouldHaveExecuted =>
        ShadowOutcome == PolicyOutcome.Execute && ActualOutcome != PolicyOutcome.Execute;

    /// <summary>True when raising the autonomy level would have changed nothing.</summary>
    public bool Agreed => ShadowOutcome == ActualOutcome;

    public static ShadowDecision Record(
        Guid? cycleId,
        Guid proposalId,
        Capability capability,
        string actionType,
        RiskTier riskTier,
        Money exposure,
        AutonomyMode actualMode,
        PolicyOutcome actualOutcome,
        AutonomyMode shadowMode,
        PolicyOutcome shadowOutcome,
        string reason,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exposure);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (proposalId == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(proposalId),
                "A shadow decision is about a specific proposal. Without one it cannot be compared " +
                "with what actually happened, which is the only reason to record it.");
        }

        if (shadowMode <= actualMode)
        {
            throw new DomainRuleViolationException(
                "ShadowDecision.MeasuresHigherAutonomy",
                $"A shadow decision measures a HIGHER autonomy than the one in force. Actual is " +
                $"{actualMode} and shadow is {shadowMode}, which measures nothing.");
        }

        return new ShadowDecision(
            Guid.NewGuid(),
            cycleId,
            proposalId,
            capability,
            Text(actionType, nameof(actionType), MaxActionTypeLength,
                "A shadow decision records which action type it was about."),
            riskTier,
            exposure,
            actualMode,
            actualOutcome,
            shadowMode,
            shadowOutcome,
            Text(reason, nameof(reason), MaxReasonLength,
                "A shadow decision records why the shadow gate reached its outcome. Without it the " +
                "comparison is two numbers and no explanation."),
            nowUtc);
    }

    public override string ToString() =>
        $"shadow {ShadowDecisionId}: {ActualMode}->{ActualOutcome} vs {ShadowMode}->{ShadowOutcome}";

    private static string Text(string? value, string parameterName, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, message);
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
