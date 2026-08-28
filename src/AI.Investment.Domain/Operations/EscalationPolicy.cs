using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Operations;

/// <summary>
/// The facts an escalation decision is made from. Every one is observed, none is judged.
/// </summary>
/// <remarks>
/// Assembled by the caller and passed in, for the same reason <c>PolicyContext</c> is: it keeps
/// <see cref="EscalationPolicy"/> a pure function that can be enumerated exhaustively in tests. A
/// rule about when a human must be woken up is not a rule to discover the behaviour of by running
/// the system.
/// </remarks>
public sealed record EscalationSignals
{
    /// <summary>The computed risk tier of the action.</summary>
    public required RiskTier RiskTier { get; init; }

    /// <summary>The tier at or above which a human is required, whatever else is true.</summary>
    public required RiskTier EscalateAtOrAbove { get; init; }

    public required ReversibilityClass Reversibility { get; init; }

    /// <summary>Where the exposure sits against the grant that would cover it.</summary>
    public required ExposureBand ExposureBand { get; init; }

    /// <summary>The resolved autonomy mode for this action.</summary>
    public required AutonomyMode AutonomyMode { get; init; }

    /// <summary>True when the limit engine reported at least one breach.</summary>
    public required bool LimitBreached { get; init; }

    /// <summary>True when a cycle budget has been exhausted.</summary>
    public required bool BudgetExhausted { get; init; }

    /// <summary>True when a provider failed or a step has been retried past its allowance.</summary>
    public required bool ProviderFailed { get; init; }

    /// <summary>
    /// True when the evidence is stale, quarantined, or single-sourced where the type requires
    /// corroboration.
    /// </summary>
    public required bool EvidenceUntrustworthy { get; init; }

    /// <summary>True when the action falls outside the pattern its capability has operated within.</summary>
    public required bool IsNovel { get; init; }

    /// <summary>Stated confidence, when the step that produced this had one.</summary>
    public Confidence? Confidence { get; init; }

    /// <summary>The capability's confidence floor, when it has one.</summary>
    public Confidence? ConfidenceFloor { get; init; }
}

/// <summary>
/// Decides whether a human must be involved. Pure, total and deterministic.
/// </summary>
/// <remarks>
/// <para>
/// Evaluated in severity order, and the first match wins, so that an escalation carries the worst
/// thing that is true about it. An escalation headed "confidence was low" on an action that also
/// breached a limit would send the reader to the wrong question.
/// </para>
/// <para>
/// Nothing here is configurable away. The thresholds - which tier, which floor - are inputs, but
/// the rules themselves are code, because a rule about when to wake a human that configuration can
/// disable is a rule that will eventually be disabled at three in the morning.
/// </para>
/// </remarks>
public static class EscalationPolicy
{
    public static EscalationReason Required(EscalationSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        // Fail-closed first. Everything below assumes the system knows what it is allowed to do.
        if (signals.AutonomyMode <= AutonomyMode.Off)
        {
            return EscalationReason.NoAutonomyGrant;
        }

        if (signals.LimitBreached)
        {
            return EscalationReason.LimitBreach;
        }

        if (signals.Reversibility == ReversibilityClass.Irreversible)
        {
            return EscalationReason.Irreversible;
        }

        if (signals.RiskTier >= signals.EscalateAtOrAbove)
        {
            return EscalationReason.RiskTierAboveBand;
        }

        if (signals.ExposureBand is ExposureBand.Above or ExposureBand.Incomparable or ExposureBand.Unknown)
        {
            return EscalationReason.ExposureAboveBand;
        }

        if (signals.EvidenceUntrustworthy)
        {
            return EscalationReason.UntrustworthyEvidence;
        }

        if (BelowFloor(signals.Confidence, signals.ConfidenceFloor))
        {
            return EscalationReason.LowConfidence;
        }

        if (signals.BudgetExhausted)
        {
            return EscalationReason.BudgetExhausted;
        }

        if (signals.ProviderFailed)
        {
            return EscalationReason.ProviderFailure;
        }

        if (signals.IsNovel)
        {
            return EscalationReason.Novelty;
        }

        return EscalationReason.None;
    }

    /// <summary>
    /// True when a floor is configured and the stated confidence does not reach it.
    /// </summary>
    /// <remarks>
    /// A configured floor with no stated confidence escalates. "The step did not say how sure it
    /// was" is not evidence that it was sure, and reading a missing value as passing the check is
    /// how a threshold stops being one.
    /// </remarks>
    private static bool BelowFloor(Confidence? confidence, Confidence? floor)
    {
        if (floor is null)
        {
            return false;
        }

        return confidence is null || confidence.Value < floor.Value;
    }
}
