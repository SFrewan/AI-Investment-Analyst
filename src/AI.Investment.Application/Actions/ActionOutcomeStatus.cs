namespace AI.Investment.Application.Actions;

/// <summary>What became of a dispatched action.</summary>
public enum ActionOutcomeStatus
{
    /// <summary>Policy permitted it and the effect ran successfully.</summary>
    Executed = 0,

    /// <summary>Policy requires a human decision. The effect was NOT invoked.</summary>
    ApprovalRequired = 1,

    /// <summary>Policy refused it. The effect was NOT invoked.</summary>
    Denied = 2,

    /// <summary>The idempotency key was already claimed. The effect was NOT invoked again.</summary>
    DuplicateSuppressed = 3,
}
