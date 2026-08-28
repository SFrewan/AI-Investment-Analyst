namespace AI.Investment.Domain.Ai;

/// <summary>
/// How an agent run ended.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> is zero deliberately, and is a failure. Making <see cref="Ok"/> the
/// default would mean that any result which skipped initialisation - a deserialised record with a
/// missing field, a struct that was never assigned - would present itself as a successful
/// analysis. The safety-relevant default is the one that refuses, which is the same choice
/// <c>KillSwitchState</c> makes for the same reason.
/// </para>
/// <para>
/// Every non-<see cref="Ok"/> value is a refusal to produce a number, not a degraded number. That
/// is the point: a missing figure is recoverable downstream, and a confidently invented one is
/// not, because it is indistinguishable from a real one.
/// </para>
/// </remarks>
public enum AgentStatus
{
    /// <summary>Never valid on a completed run. Treated exactly like a provider error.</summary>
    Unknown = 0,

    /// <summary>The output parsed, validated against its schema, and was grounded in the bundle.</summary>
    Ok = 1,

    /// <summary>The output did not satisfy its schema after the permitted retries.</summary>
    SchemaFailed = 2,

    /// <summary>The output was well-formed but cited figures that are not in the evidence bundle.</summary>
    Ungrounded = 3,

    /// <summary>The agent declined to answer, which is a legitimate and useful outcome.</summary>
    Refused = 4,

    /// <summary>The provider failed, or none was configured.</summary>
    ProviderError = 5,

    /// <summary>The run would have exceeded its cost or latency budget.</summary>
    BudgetExceeded = 6,
}
