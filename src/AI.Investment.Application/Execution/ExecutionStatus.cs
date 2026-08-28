namespace AI.Investment.Application.Execution;

/// <summary>How an attempt to act on an opportunity ended.</summary>
/// <remarks>
/// Every refusal has its own value rather than collapsing into one. They call for completely
/// different responses - a limit breach is a configuration or sizing question, a kill switch is an
/// operator decision, a policy denial is a permissions question, and a venue rejection is an
/// infrastructure one - and a single "refused" would send every investigation to the wrong place.
/// </remarks>
public enum ExecutionStatus
{
    /// <summary>Never valid on a completed attempt.</summary>
    Unknown = 0,

    /// <summary>The order was placed and the books were posted.</summary>
    Executed = 1,

    /// <summary>The kill switch was engaged, or its state could not be read.</summary>
    RefusedByKillSwitch = 2,

    /// <summary>One or more configured ceilings would have been exceeded.</summary>
    RefusedByLimits = 3,

    /// <summary>The approval token could not authorise this action.</summary>
    RefusedByApproval = 4,

    /// <summary>The policy engine denied the action.</summary>
    DeniedByPolicy = 5,

    /// <summary>The policy engine requires a human approval that has not been given.</summary>
    ApprovalRequired = 6,

    /// <summary>This action had already been performed. It was not repeated.</summary>
    DuplicateSuppressed = 7,

    /// <summary>The venue refused the order.</summary>
    VenueRejected = 8,
}
