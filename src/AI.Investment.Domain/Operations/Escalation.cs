using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Operations;

/// <summary>
/// A question the platform could not answer on its own, put to a human, with an expiry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Escalations expire.</strong> An unanswered escalation is not a pending action
/// indefinitely: the market context a human was asked to judge goes stale, and answering yesterday's
/// question today is a different decision from the one that was put. An expired escalation is
/// <see cref="IsUnhandled"/>, which is the number the unattended-operation criterion measures - "no
/// unhandled escalation" means none of these reached its expiry without an answer.
/// </para>
/// <para>
/// It carries the reason, the cycle, the proposal and the explanation, so that what reaches a person
/// is the complete case rather than a notification they have to go and research. An escalation that
/// costs its reader ten minutes of investigation is an escalation they will start skimming.
/// </para>
/// </remarks>
public sealed class Escalation
{
    public const int MaxExplanationLength = 2000;

    public const int MaxActorLength = 120;

    public const int MaxResolutionLength = 500;

    private Escalation(
        Guid escalationId,
        Guid? cycleId,
        Guid? proposalId,
        Capability capability,
        EscalationReason reason,
        string explanation,
        DateTime raisedAtUtc,
        DateTime expiresAtUtc)
    {
        EscalationId = escalationId;
        CycleId = cycleId;
        ProposalId = proposalId;
        Capability = capability;
        Reason = reason;
        Explanation = explanation;
        RaisedAtUtc = raisedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Escalation() => Explanation = string.Empty;

    public Guid EscalationId { get; private set; }

    public Guid? CycleId { get; private set; }

    public Guid? ProposalId { get; private set; }

    public Capability Capability { get; private set; }

    public EscalationReason Reason { get; private set; }

    public string Explanation { get; private set; }

    public DateTime RaisedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? AcknowledgedAtUtc { get; private set; }

    public string? AcknowledgedBy { get; private set; }

    public DateTime? ResolvedAtUtc { get; private set; }

    public string? Resolution { get; private set; }

    public bool IsResolved => ResolvedAtUtc is not null;

    public bool IsAcknowledged => AcknowledgedAtUtc is not null;

    public bool HasExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>
    /// True when this escalation reached its expiry without an answer. The measurement behind "no
    /// unhandled escalation".
    /// </summary>
    public bool IsUnhandled(DateTime nowUtc) => !IsResolved && HasExpired(nowUtc);

    public static Escalation Raise(
        Capability capability,
        EscalationReason reason,
        string explanation,
        DateTime nowUtc,
        TimeSpan validFor,
        Guid? cycleId = null,
        Guid? proposalId = null)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enum.IsDefined(capability))
        {
            throw new DomainValidationException(nameof(capability), $"Unrecognised capability '{capability}'.");
        }

        if (!Enum.IsDefined(reason) || reason == EscalationReason.None)
        {
            throw new DomainValidationException(
                nameof(reason),
                "An escalation must name the condition that required a human. None is the value the " +
                "policy returns when nothing does, and raising one anyway would put a question with " +
                "no subject in front of a person.");
        }

        if (validFor <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(validFor),
                "An escalation must expire. An answer given long after the question was asked is an " +
                "answer to a different question.");
        }

        return new Escalation(
            Guid.NewGuid(),
            cycleId,
            proposalId,
            capability,
            reason,
            Text(explanation, nameof(explanation), MaxExplanationLength,
                "An escalation must explain itself. A notification that costs its reader ten minutes " +
                "of investigation is one they will start skimming."),
            nowUtc,
            nowUtc.Add(validFor));
    }

    /// <summary>Records that a person has seen it. Does not answer it.</summary>
    public void Acknowledge(string by, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var who = Text(by, nameof(by), MaxActorLength, "An acknowledgement must name who made it.");

        if (IsResolved)
        {
            throw new DomainRuleViolationException(
                "Escalation.AlreadyResolved",
                $"Escalation {EscalationId} is already resolved.");
        }

        if (IsAcknowledged)
        {
            return;
        }

        AcknowledgedAtUtc = nowUtc;
        AcknowledgedBy = who;
    }

    /// <summary>Answers it. An escalation is resolved once and stays resolved.</summary>
    public void Resolve(string by, string resolution, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var who = Text(by, nameof(by), MaxActorLength, "A resolution must name who made it.");
        var what = Text(resolution, nameof(resolution), MaxResolutionLength,
            "A resolution must say what was decided.");

        if (IsResolved)
        {
            throw new DomainRuleViolationException(
                "Escalation.AlreadyResolved",
                $"Escalation {EscalationId} was resolved at {ResolvedAtUtc:O}. Answering it twice " +
                "would leave the record showing two different decisions.");
        }

        AcknowledgedAtUtc ??= nowUtc;
        AcknowledgedBy ??= who;
        ResolvedAtUtc = nowUtc;
        Resolution = $"{who}: {what}";
    }

    public override string ToString() =>
        $"escalation {EscalationId} [{Capability}/{Reason}] expires {ExpiresAtUtc:O}";

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
