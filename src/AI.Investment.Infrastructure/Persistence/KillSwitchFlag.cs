using AI.Investment.Domain.Enums;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// The database half of the kill switch: one row per scope.
/// </summary>
/// <remarks>
/// <para>
/// A storage record rather than a domain entity, in the same way <c>ProcessedAction</c> is. The
/// domain already has the concept it needs - <c>KillSwitchState</c> - and what the database adds is
/// durability, not meaning.
/// </para>
/// <para>
/// Two mechanisms back the switch and either can engage it: this row, which survives a restart, and
/// an environment variable, which stops a process without needing the database to be healthy. They
/// fail in different ways and are reachable by different people, which is the entire reason for
/// having both.
/// </para>
/// <para>
/// <see cref="Capability"/> is null for the global switch. A null row engages everything; a
/// capability row engages one class of action.
/// </para>
/// </remarks>
public sealed class KillSwitchFlag
{
    public const int MaxReasonLength = 500;

    private KillSwitchFlag(Guid killSwitchFlagId, Capability? capability, bool engaged, string reason, DateTime updatedAtUtc)
    {
        KillSwitchFlagId = killSwitchFlagId;
        Capability = capability;
        Engaged = engaged;
        Reason = reason;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private KillSwitchFlag() => Reason = string.Empty;

    public Guid KillSwitchFlagId { get; private set; }

    /// <summary>Null for the global switch.</summary>
    public Capability? Capability { get; private set; }

    public bool Engaged { get; private set; }

    public string Reason { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static KillSwitchFlag Create(Capability? capability, bool engaged, string reason, DateTime updatedAtUtc)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? "No reason recorded." : reason.Trim();

        return new KillSwitchFlag(
            Guid.NewGuid(),
            capability,
            engaged,
            trimmed.Length <= MaxReasonLength ? trimmed : trimmed[..MaxReasonLength],
            updatedAtUtc);
    }

    /// <summary>Changes the switch, recording why and when.</summary>
    public void Set(bool engaged, string reason, DateTime nowUtc)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? "No reason recorded." : reason.Trim();

        Engaged = engaged;
        Reason = trimmed.Length <= MaxReasonLength ? trimmed : trimmed[..MaxReasonLength];
        UpdatedAtUtc = nowUtc;
    }
}
