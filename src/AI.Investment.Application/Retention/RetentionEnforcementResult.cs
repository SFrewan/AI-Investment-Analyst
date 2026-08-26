using AI.Investment.Domain.Retention;

namespace AI.Investment.Application.Retention;

/// <summary>What was actually done about a payload, as opposed to what was required.</summary>
/// <remarks>
/// <see cref="RetentionDecision"/> answers "what does this payload's licence require?" - a pure
/// domain question. This answers "and what happened?", which is not the same thing, because the
/// deletion still has to pass the safety seam and may be denied, deferred for approval, or already
/// claimed by an earlier attempt.
/// </remarks>
public enum RetentionAction
{
    /// <summary>Not determined. The default, so an unset value never reads as a completed deletion.</summary>
    Unknown = 0,

    /// <summary>The licence still permits keeping it. Nothing was done, and nothing needed to be.</summary>
    NothingRequired = 1,

    /// <summary>Deletion was required, authorised, and carried out.</summary>
    Deleted = 2,

    /// <summary>
    /// Deletion was required but did not happen: policy denied it, it awaits human approval, or an
    /// earlier attempt already claimed the idempotency key.
    /// </summary>
    /// <remarks>
    /// Not a failure of the sweep, and not something to retry blindly. Retention deletion declares
    /// itself irreversible, so an installation that has not granted automatic execution for
    /// <c>Capability.DataRetention</c> will land here on every payload by design - which is the
    /// correct default and must be visible rather than mistaken for "nothing was due".
    /// </remarks>
    DeletionRefused = 3,
}

/// <summary>
/// The result of enforcing retention on one payload: the obligation, and what came of it.
/// </summary>
/// <remarks>
/// Both halves are returned because a caller needs both and they can disagree. A payload whose
/// licence required deletion and which still sits in the archive is a compliance exposure, and it
/// is invisible to anything that only sees the decision or only sees the effect.
/// </remarks>
/// <param name="Decision">What the licence required, and which rule said so.</param>
/// <param name="Action">What actually happened.</param>
public sealed record RetentionEnforcementResult(RetentionDecision Decision, RetentionAction Action)
{
    /// <summary>The payload was deleted.</summary>
    public bool WasDeleted => Action == RetentionAction.Deleted;

    /// <summary>Deletion was required and did not happen.</summary>
    public bool IsOutstanding => Action == RetentionAction.DeletionRefused;

    /// <summary>The versioned rule behind the obligation.</summary>
    public string RuleId => Decision.RuleId;

    public static RetentionEnforcementResult Retained(RetentionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new RetentionEnforcementResult(decision, RetentionAction.NothingRequired);
    }

    public override string ToString() => $"{Action} <- {Decision}";
}
