namespace AI.Investment.Domain.Enums;

/// <summary>
/// The three possible answers of the policy engine. There is no fourth, and no "unknown".
/// </summary>
public enum PolicyOutcome
{
    /// <summary>Refused. The effect must not be attempted.</summary>
    Deny = 0,

    /// <summary>Permitted only after a human approves this exact action.</summary>
    RequireApproval = 1,

    /// <summary>Permitted now.</summary>
    Execute = 2,
}
