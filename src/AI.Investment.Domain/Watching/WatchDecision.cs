using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Watching;

/// <summary>Why a watch did not fire.</summary>
public enum WatchRefusal
{
    /// <summary>It did fire.</summary>
    None = 0,

    /// <summary>The watch is switched off.</summary>
    Disabled = 1,

    /// <summary>The observation is about something else.</summary>
    TargetMismatch = 2,

    /// <summary>The observation is of a different kind.</summary>
    TypeMismatch = 3,

    /// <summary>The condition did not hold.</summary>
    ConditionNotMet = 4,

    /// <summary>The watch fired too recently. The single most important refusal here.</summary>
    WithinCooldown = 5,

    /// <summary>The observation is older than this watch is willing to act on.</summary>
    SignalTooOld = 6,

    /// <summary>The observation is dated in the future, which cannot be acted on safely.</summary>
    SignalInFuture = 7,
}

/// <summary>Whether a watch fires on an observation, and why.</summary>
/// <remarks>
/// Returned rather than thrown for every outcome including the refusals, because "did not fire" is
/// the overwhelmingly common answer and the normal case must not be an exception. The reason is
/// carried because the question an operator actually asks is not "did it fire" but "why did it not".
/// </remarks>
public sealed record WatchDecision
{
    private WatchDecision(bool fires, WatchRefusal refusal, string reason)
    {
        Fires = fires;
        Refusal = refusal;
        Reason = reason;
    }

    public bool Fires { get; }

    public WatchRefusal Refusal { get; }

    public string Reason { get; }

    internal static WatchDecision Fired(string reason) => Create(true, WatchRefusal.None, reason);

    internal static WatchDecision Refused(WatchRefusal refusal, string reason) =>
        Create(false, refusal, reason);

    private static WatchDecision Create(bool fires, WatchRefusal refusal, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(
                nameof(reason),
                "A watch decision must state its reason. 'It did not fire' with no explanation is the " +
                "hardest kind of quiet to debug.");
        }

        return new WatchDecision(fires, refusal, reason.Trim());
    }

    public override string ToString() => Reason;
}
