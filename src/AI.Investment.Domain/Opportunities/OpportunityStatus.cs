namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// Where an opportunity is in its lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Draft"/> is zero, so the state a value defaults to is the one from which nothing can
/// happen. Every other transition is explicit, and the terminal states are terminal: the aggregate
/// refuses to move out of <see cref="Closed"/>, <see cref="Rejected"/> or <see cref="Expired"/>.
/// </para>
/// <para>
/// Ordering is meaningful for the forward path only. It is deliberately not used as a comparison
/// for "further along than", because the terminal states are not further along than anything.
/// </para>
/// </remarks>
public enum OpportunityStatus
{
    /// <summary>Discovered. Not yet complete enough to evaluate.</summary>
    Draft = 0,

    /// <summary>Economics, risk and evidence are present and the type's requirements are met.</summary>
    Evaluated = 1,

    /// <summary>Scored deterministically, and therefore comparable with other opportunities.</summary>
    Ranked = 2,

    /// <summary>An action has been proposed for it and is awaiting a policy decision.</summary>
    Proposed = 3,

    /// <summary>A human approved the exact action that was presented.</summary>
    Approved = 4,

    /// <summary>The approved action is being carried out.</summary>
    Executing = 5,

    /// <summary>The action succeeded and the position or commitment is live.</summary>
    Active = 6,

    /// <summary>Finished, with an outcome recorded.</summary>
    Closed = 7,

    /// <summary>Refused - by policy, by a human, or by the type's own requirements.</summary>
    Rejected = 8,

    /// <summary>Its time horizon passed before it was acted on.</summary>
    Expired = 9,
}
