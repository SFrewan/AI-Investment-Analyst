using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Autonomy;

/// <summary>What caused autonomy to be lowered automatically, in order of severity.</summary>
/// <remarks>
/// <see cref="None"/> is zero. Every other value demotes, and the ordering is the order they are
/// tested in, so the reason recorded on a grant is the most serious thing that was true rather than
/// whichever check happened to run first.
/// </remarks>
public enum DemotionTrigger
{
    /// <summary>Nothing crossed a threshold. The grant keeps its level.</summary>
    None = 0,

    /// <summary>Something about the state could not be determined. Demote rather than assume.</summary>
    StateUnknown = 1,

    /// <summary>The kill switch is engaged, or could not be read.</summary>
    KillSwitchEngaged = 2,

    /// <summary>The warrant behind the grant was revoked, expired, or no longer covers it.</summary>
    WarrantNoLongerValid = 3,

    /// <summary>An unattended action breached a policy or a limit.</summary>
    PolicyBreach = 4,

    /// <summary>Unattended actions have been failing.</summary>
    ExecutionFailures = 5,

    /// <summary>Escalations raised by unattended work are going unanswered.</summary>
    UnhandledEscalations = 6,

    /// <summary>The measured evidence that justified promotion no longer holds.</summary>
    EvidenceNoLongerJustifies = 7,

    /// <summary>The grant has been running longer than the evidence behind it is good for.</summary>
    EvidenceStale = 8,
}

/// <summary>What is known about a capability's recent unattended behaviour.</summary>
/// <remarks>
/// <para>
/// Every field is required, and the nullable ones are nullable because "not known" is a distinct
/// answer from any number. <see cref="DemotionPolicy.Required"/> treats not-known as a reason to
/// demote, which is the opposite of the usual convenience and is the whole point: a circuit breaker
/// that keeps the circuit closed when its sensor fails is not a circuit breaker.
/// </para>
/// <para>
/// Counts are over a window the caller chooses and states. This type does not know how long the
/// window is, and deliberately does not: the arithmetic of "too many" belongs with the thresholds,
/// which are arguments.
/// </para>
/// </remarks>
public sealed record DemotionSignals
{
    /// <summary>True when the kill switch is engaged or its state could not be read.</summary>
    public required bool KillSwitchEngagedOrUnknown { get; init; }

    /// <summary>True when the warrant behind the grant is missing, revoked, expired or mismatched.</summary>
    public required bool WarrantNoLongerValid { get; init; }

    /// <summary>Policy denials or limit breaches on unattended actions in the window. Null if unknown.</summary>
    public required int? PolicyBreaches { get; init; }

    /// <summary>Unattended executions that failed in the window. Null if unknown.</summary>
    public required int? ExecutionFailures { get; init; }

    /// <summary>Escalations that reached their expiry unanswered. Null if unknown.</summary>
    public required int? UnhandledEscalations { get; init; }

    /// <summary>True when a fresh assessment of the evidence no longer justifies the grant.</summary>
    public required bool EvidenceNoLongerJustifies { get; init; }

    /// <summary>How old the evidence behind the grant is now. Null if unknown.</summary>
    public required TimeSpan? EvidenceAge { get; init; }
}

/// <summary>How much of any of the above is too much.</summary>
public sealed record DemotionThresholds(
    int MaxPolicyBreaches,
    int MaxExecutionFailures,
    int MaxUnhandledEscalations,
    TimeSpan MaxEvidenceAge)
{
    /// <summary>
    /// Deliberately unforgiving. One policy breach by something nobody was watching is one more than
    /// the evidence for promotion accounted for.
    /// </summary>
    public static DemotionThresholds Standard { get; } =
        new(MaxPolicyBreaches: 0, MaxExecutionFailures: 2, MaxUnhandledEscalations: 0,
            MaxEvidenceAge: TimeSpan.FromDays(90));
}

/// <summary>
/// The circuit breaker on autonomy: what lowers it, automatically, without anybody deciding to.
/// </summary>
/// <remarks>
/// <para>
/// §K.6 asks for automatic demotion, and the two words carry the whole design. <em>Automatic</em>
/// means no human has to notice; a breaker that needs somebody to trip it protects nothing at four
/// in the morning. <em>Demotion</em> means one level at a time rather than an off switch, so that a
/// measurement drifting past its threshold degrades the platform into asking permission rather than
/// stopping it dead - and so that repeated crossings walk it down to Off on their own.
/// </para>
/// <para>
/// <strong>Fail closed, and count "unknown" as a reason.</strong> Every nullable signal that arrives
/// null demotes. That is deliberately the inconvenient choice: the situations in which a metric
/// cannot be read - a store that is down, a query that timed out, a deployment mid-flight - are
/// exactly the situations in which the platform should be doing less rather than the same amount.
/// </para>
/// <para>
/// Pure and total, like every other decision in this system that decides whether something may
/// happen. There is no clock, no store and no logger here; the service that applies the verdict has
/// all three.
/// </para>
/// </remarks>
public static class DemotionPolicy
{
    /// <summary>
    /// The most serious reason to demote, or <see cref="DemotionTrigger.None"/>.
    /// </summary>
    public static DemotionTrigger Required(DemotionSignals signals, DemotionThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (thresholds.MaxPolicyBreaches < 0 ||
            thresholds.MaxExecutionFailures < 0 ||
            thresholds.MaxUnhandledEscalations < 0 ||
            thresholds.MaxEvidenceAge <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(thresholds),
                "A demotion threshold may not be negative, and evidence must be allowed to be at " +
                "least a moment old. Thresholds that cannot be satisfied would demote everything on " +
                "every pass, which is a stopped platform rather than a governed one.");
        }

        // Unknown first, and before anything that reads a number. A signal set that cannot answer is
        // not a signal set that answered favourably.
        if (signals.PolicyBreaches is null ||
            signals.ExecutionFailures is null ||
            signals.UnhandledEscalations is null ||
            signals.EvidenceAge is null)
        {
            return DemotionTrigger.StateUnknown;
        }

        if (signals.KillSwitchEngagedOrUnknown)
        {
            return DemotionTrigger.KillSwitchEngaged;
        }

        if (signals.WarrantNoLongerValid)
        {
            return DemotionTrigger.WarrantNoLongerValid;
        }

        if (signals.PolicyBreaches > thresholds.MaxPolicyBreaches)
        {
            return DemotionTrigger.PolicyBreach;
        }

        if (signals.ExecutionFailures > thresholds.MaxExecutionFailures)
        {
            return DemotionTrigger.ExecutionFailures;
        }

        if (signals.UnhandledEscalations > thresholds.MaxUnhandledEscalations)
        {
            return DemotionTrigger.UnhandledEscalations;
        }

        if (signals.EvidenceNoLongerJustifies)
        {
            return DemotionTrigger.EvidenceNoLongerJustifies;
        }

        return signals.EvidenceAge > thresholds.MaxEvidenceAge
            ? DemotionTrigger.EvidenceStale
            : DemotionTrigger.None;
    }

    /// <summary>The trigger in words, recorded on the grant it demotes.</summary>
    public static string Explain(DemotionTrigger trigger) => trigger switch
    {
        DemotionTrigger.None =>
            "nothing crossed a threshold.",

        DemotionTrigger.StateUnknown =>
            "at least one signal could not be read. A platform that cannot tell how its unattended " +
            "work is going does less of it, not the same amount.",

        DemotionTrigger.KillSwitchEngaged =>
            "the kill switch is engaged or could not be read.",

        DemotionTrigger.WarrantNoLongerValid =>
            "the promotion warrant behind this grant is revoked, expired or no longer covers it. The " +
            "grant outlived the evidence it was issued on.",

        DemotionTrigger.PolicyBreach =>
            "an unattended action breached a policy or a limit. One breach by something nobody was " +
            "watching is one more than the evidence for promotion accounted for.",

        DemotionTrigger.ExecutionFailures =>
            "unattended executions have been failing more often than the threshold allows.",

        DemotionTrigger.UnhandledEscalations =>
            "escalations raised by unattended work are reaching their expiry unanswered, which is how " +
            "a human-in-the-loop control fails in practice.",

        DemotionTrigger.EvidenceNoLongerJustifies =>
            "a fresh assessment of the measured evidence no longer justifies this level.",

        DemotionTrigger.EvidenceStale =>
            "the evidence behind this grant is older than it is allowed to be.",

        _ =>
            "the signals were not judged, so the grant is lowered.",
    };
}
