namespace AI.Investment.Application.Ai.Agents;

/// <summary>How serious an identified risk is, as the agent judges it.</summary>
/// <remarks>
/// This is an agent's opinion about the world, and it is deliberately not the same thing as
/// <c>RiskTier</c>, which the policy engine computes from an action's economics and reversibility.
/// Nothing here reaches the safety path: a model saying a risk is <see cref="Low"/> can never make
/// an action easier to authorise.
/// </remarks>
public enum RiskSeverity
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}
