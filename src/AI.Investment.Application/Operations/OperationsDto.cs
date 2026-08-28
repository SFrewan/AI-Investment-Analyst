using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Shadow;

namespace AI.Investment.Application.Operations;

/// <summary>One operating cycle, as an operator reads it.</summary>
public sealed record CycleDto(
    Guid CycleId,
    string CorrelationId,
    string Capability,
    string Template,
    string TriggerKey,
    Guid? WatchId,
    string Status,
    string Stage,
    string Budget,
    string Consumption,
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? StoppedAtUtc,
    string? StoppedReason,
    int EscalationCount);

/// <summary>One question put to a human.</summary>
public sealed record EscalationDto(
    Guid EscalationId,
    Guid? CycleId,
    Guid? ProposalId,
    string Capability,
    string Reason,
    string Explanation,
    DateTime RaisedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? AcknowledgedAtUtc,
    DateTime? ResolvedAtUtc,
    string? Resolution,
    bool IsUnhandled);

/// <summary>One measurement of what a higher autonomy level would have decided.</summary>
public sealed record ShadowDecisionDto(
    Guid ShadowDecisionId,
    Guid? CycleId,
    Guid ProposalId,
    string Capability,
    string ActionType,
    string RiskTier,
    decimal Exposure,
    string Currency,
    string ActualMode,
    string ActualOutcome,
    string ShadowMode,
    string ShadowOutcome,
    bool WouldHaveExecuted,
    bool Agreed,
    DateTime RecordedAtUtc);

/// <summary>One grant, and what is actually in force under it.</summary>
public sealed record AutonomyGrantDto(
    Guid AutonomyGrantId,
    string Capability,
    string? ActionType,
    string Environment,
    string GrantedMode,
    string EffectiveMode,
    string MaxRiskTier,
    decimal MaxExposure,
    string Currency,
    string LimitSet,
    string GrantedBy,
    DateTime GrantedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    int DemotionCount,
    bool IsActive);

/// <summary>Maps the operations aggregates to their read models.</summary>
/// <remarks>
/// Hand-written, like every other mapper here. A mapping library would save a few lines and cost the
/// ability to see, in one place, exactly what the API exposes - which for a surface that reports on
/// the safety controls is the wrong trade.
/// </remarks>
public static class OperationsMapper
{
    public static CycleDto ToDto(OperatingCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        return new CycleDto(
            cycle.CycleId,
            cycle.CorrelationId.Value,
            cycle.Capability.ToString(),
            cycle.TemplateName,
            cycle.TriggerKey,
            cycle.WatchId,
            cycle.Status.ToString(),
            cycle.Stage.ToString(),
            cycle.Budget.ToString(),
            cycle.Consumption.ToString(),
            cycle.StartedAtUtc,
            cycle.UpdatedAtUtc,
            cycle.StoppedAtUtc,
            cycle.StoppedReason,
            cycle.EscalationCount);
    }

    public static EscalationDto ToDto(Escalation escalation, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(escalation);

        return new EscalationDto(
            escalation.EscalationId,
            escalation.CycleId,
            escalation.ProposalId,
            escalation.Capability.ToString(),
            escalation.Reason.ToString(),
            escalation.Explanation,
            escalation.RaisedAtUtc,
            escalation.ExpiresAtUtc,
            escalation.AcknowledgedAtUtc,
            escalation.ResolvedAtUtc,
            escalation.Resolution,
            escalation.IsUnhandled(nowUtc));
    }

    public static ShadowDecisionDto ToDto(ShadowDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new ShadowDecisionDto(
            decision.ShadowDecisionId,
            decision.CycleId,
            decision.ProposalId,
            decision.Capability.ToString(),
            decision.ActionType,
            decision.RiskTier.ToString(),
            decision.Exposure.Amount,
            decision.Exposure.Currency.Code,
            decision.ActualMode.ToString(),
            decision.ActualOutcome.ToString(),
            decision.ShadowMode.ToString(),
            decision.ShadowOutcome.ToString(),
            decision.WouldHaveExecuted,
            decision.Agreed,
            decision.RecordedAtUtc);
    }

    public static AutonomyGrantDto ToDto(AutonomyGrant grant, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return new AutonomyGrantDto(
            grant.AutonomyGrantId,
            grant.Capability.ToString(),
            grant.ActionType,
            grant.EnvironmentName,
            grant.GrantedMode.ToString(),
            grant.EffectiveMode.ToString(),
            grant.MaxRiskTier.ToString(),
            grant.MaxExposure.Amount,
            grant.MaxExposure.Currency.Code,
            grant.LimitSetName,
            grant.GrantedBy,
            grant.GrantedAtUtc,
            grant.ExpiresAtUtc,
            grant.RevokedAtUtc,
            grant.DemotionCount,
            grant.IsActive(nowUtc));
    }
}
