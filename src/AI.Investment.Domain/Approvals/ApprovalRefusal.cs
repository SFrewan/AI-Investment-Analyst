namespace AI.Investment.Domain.Approvals;

/// <summary>Why an approval token could not be used.</summary>
/// <remarks>
/// <see cref="None"/> is zero and means the token was usable. Every other value is a refusal, so a
/// reason nobody set reads as "no objection" only in the one case where that is true - and the
/// consuming code checks the boolean, not this, precisely so a new member added later cannot
/// silently become permissive.
/// </remarks>
public enum ApprovalRefusal
{
    /// <summary>The token was valid for this action.</summary>
    None = 0,

    /// <summary>It has already been used. Approvals are single-use.</summary>
    AlreadyConsumed = 1,

    /// <summary>It was withdrawn before it could be used.</summary>
    Revoked = 2,

    /// <summary>Its window has passed.</summary>
    Expired = 3,

    /// <summary>The action presented for execution is not the action that was approved.</summary>
    FingerprintMismatch = 4,

    /// <summary>The action would commit more than the approver saw.</summary>
    AmountExceeded = 5,

    /// <summary>The token belongs to a different opportunity.</summary>
    WrongOpportunity = 6,

    /// <summary>The token belongs to a different proposal.</summary>
    WrongProposal = 7,
}
