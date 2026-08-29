using System.Globalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Operators;

/// <summary>How an operator action ended.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero so that a default-initialised outcome is not mistaken for success.
/// The two refusals at the top are this layer's own; everything below is the seam's answer relayed
/// unchanged, because a controller that could not tell "policy denied this" from "the operator is
/// not permitted to ask" would report one as the other.
/// </remarks>
public enum OperatorOutcomeStatus
{
    Unknown = 0,

    /// <summary>Nobody was authenticated. The action was not proposed.</summary>
    NotAuthenticated = 1,

    /// <summary>Authenticated, but without the privilege this action requires.</summary>
    NotPermitted = 2,

    /// <summary>The thing being acted on does not exist.</summary>
    NotFound = 3,

    /// <summary>The domain refused: a lifecycle rule, a state that does not permit it.</summary>
    Refused = 4,

    /// <summary>The policy engine denied it.</summary>
    DeniedByPolicy = 5,

    /// <summary>The policy engine requires a human approval this path cannot supply.</summary>
    ApprovalRequired = 6,

    /// <summary>The same action was already performed; the effect did not run again.</summary>
    DuplicateSuppressed = 7,

    /// <summary>Done, audited, and recorded against the operator who asked.</summary>
    Done = 8,
}

/// <summary>What an operator action did, and why it did not.</summary>
public sealed record OperatorOutcome(OperatorOutcomeStatus Status, string Reason)
{
    public bool Succeeded => Status == OperatorOutcomeStatus.Done;

    public static OperatorOutcome Done(string reason) => new(OperatorOutcomeStatus.Done, reason);

    public static OperatorOutcome NotAuthenticated() =>
        new(OperatorOutcomeStatus.NotAuthenticated,
            "No operator is authenticated for this request.");

    public static OperatorOutcome NotPermitted(OperatorPrivilege required) =>
        new(OperatorOutcomeStatus.NotPermitted,
            string.Create(
                CultureInfo.InvariantCulture,
                $"This operator does not hold the '{required}' privilege."));

    public static OperatorOutcome NotFound(string what) =>
        new(OperatorOutcomeStatus.NotFound, what);

    public static OperatorOutcome Refused(string reason) =>
        new(OperatorOutcomeStatus.Refused, reason);
}

/// <summary>
/// The typed payload of an operator action, as the safety seam sees it.
/// </summary>
/// <remarks>
/// <para>
/// Describes what is being done and to what, and nothing else. <see cref="Describe"/> is one of the
/// components hashed into the action fingerprint an approval binds to, so every field that changes
/// what would actually happen appears in it.
/// </para>
/// <para>
/// The operator's identity is deliberately <em>not</em> in the payload. It belongs on the proposal's
/// <c>ProposedBy</c>, which is where the audit record reads its actor from; putting it in two places
/// would let them disagree.
/// </para>
/// </remarks>
public sealed record OperatorActionParameters : IActionParameters
{
    public OperatorActionParameters(string verb, string subject, string detail)
    {
        Verb = verb;
        Subject = subject;
        Detail = detail;
    }

    /// <summary>What is being done, as a short stable phrase.</summary>
    public string Verb { get; }

    /// <summary>What it is being done to.</summary>
    public string Subject { get; }

    /// <summary>The operator's stated reason, or a description of the change.</summary>
    public string Detail { get; }

    public string Describe() => $"{Verb} '{Subject}': {Detail}";
}

/// <summary>The action types an operator surface proposes. Declared once, in one place.</summary>
public static class OperatorActionTypes
{
    public static ActionType RejectOpportunity { get; } = ActionType.Create("operator.reject-opportunity");

    public static ActionType AcknowledgeEscalation { get; } =
        ActionType.Create("operator.acknowledge-escalation");

    public static ActionType ResolveEscalation { get; } = ActionType.Create("operator.resolve-escalation");

    public static ActionType EngageKillSwitch { get; } = ActionType.Create("operator.engage-kill-switch");

    public static ActionType CreateWatch { get; } = ActionType.Create("operator.create-watch");

    /// <summary>
    /// Switching a watch off. Separate from <see cref="CreateWatch"/> because the audit trail has
    /// to be able to answer "who stopped this, and when" without inferring it from an absence.
    /// </summary>
    public static ActionType DisableWatch { get; } = ActionType.Create("operator.disable-watch");

    /// <summary>
    /// Putting a watch on a different interval. Distinct from creating one, so the audit trail can
    /// answer "who changed how often this runs, and to what" without inferring it from two rows.
    /// </summary>
    public static ActionType RescheduleWatch { get; } = ActionType.Create("operator.reschedule-watch");
}

/// <summary>
/// What an operator must supply to put a scheduled watch on an instrument.
/// </summary>
/// <remarks>
/// <para>
/// Scheduled watches only, deliberately. A schedule is the trigger the observation window needs -
/// review this instrument every so often - and it is the one whose configuration cannot be got
/// subtly wrong from a form. Price-move and threshold triggers carry a comparison and a number
/// whose units depend on the attribute being watched, and an operator surface that let those be
/// typed in without the attribute in front of them would be a way to build a watch that never fires
/// or one that fires constantly.
/// </para>
/// <para>
/// The cooldown is the operator's, bounded by the domain's own minimum. It is asked for rather than
/// defaulted because it is the control that stands between one volatile session and a thousand
/// cycles.
/// </para>
/// </remarks>
public sealed record ScheduledWatchDefinition(
    string Name,
    string TargetKind,
    string TargetIdentifier,
    TimeSpan Interval,
    TimeSpan Cooldown,
    Capability Capability,
    string CycleTemplate);
