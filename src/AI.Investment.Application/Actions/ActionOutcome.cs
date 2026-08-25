using AI.Investment.Domain.Actions;

namespace AI.Investment.Application.Actions;

/// <summary>
/// The result of dispatching an action through the safety seam.
/// </summary>
/// <typeparam name="TResult">What the effect returns when it runs.</typeparam>
/// <remarks>
/// <para>
/// <see cref="Result"/> is populated only when <see cref="Status"/> is
/// <see cref="ActionOutcomeStatus.Executed"/>. In every other case it is <c>default</c>,
/// because the effect was never invoked - not attempted and failed, not partially applied.
/// Never invoked.
/// </para>
/// <para>
/// <see cref="Decision"/> is always present, including for a denial. Callers - and the audit
/// trail - can therefore always answer "which rule decided this, and why".
/// </para>
/// </remarks>
public sealed class ActionOutcome<TResult>
{
    private ActionOutcome(
        ActionOutcomeStatus status,
        PolicyDecision decision,
        TResult? result,
        ActionExecution? execution)
    {
        Status = status;
        Decision = decision;
        Result = result;
        Execution = execution;
    }

    public ActionOutcomeStatus Status { get; }

    /// <summary>The policy decision. Always present, whatever the outcome.</summary>
    public PolicyDecision Decision { get; }

    /// <summary>The effect's return value. Only meaningful when <see cref="WasExecuted"/>.</summary>
    public TResult? Result { get; }

    /// <summary>The execution record, when the effect ran.</summary>
    public ActionExecution? Execution { get; }

    public bool WasExecuted => Status == ActionOutcomeStatus.Executed;

    /// <summary>Why, for a caller that needs to explain the outcome to a user.</summary>
    public string Reason => Decision.Reason;

    internal static ActionOutcome<TResult> Executed(
        PolicyDecision decision,
        TResult result,
        ActionExecution execution) =>
        new(ActionOutcomeStatus.Executed, decision, result, execution);

    internal static ActionOutcome<TResult> ApprovalRequired(PolicyDecision decision) =>
        new(ActionOutcomeStatus.ApprovalRequired, decision, default, null);

    internal static ActionOutcome<TResult> Denied(PolicyDecision decision) =>
        new(ActionOutcomeStatus.Denied, decision, default, null);

    internal static ActionOutcome<TResult> DuplicateSuppressed(PolicyDecision decision) =>
        new(ActionOutcomeStatus.DuplicateSuppressed, decision, default, null);
}
