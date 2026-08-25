namespace AI.Investment.Domain.Enums;

/// <summary>
/// Deterministically computed risk classification of a proposed action.
/// </summary>
/// <remarks>
/// NEVER assigned by an AI model, and never asserted by whoever proposes the action.
/// It is computed by <c>RiskTierCalculator</c> from capability, reversibility and exposure.
/// Ordering is meaningful: higher values are more dangerous and comparisons rely on it.
/// </remarks>
public enum RiskTier
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}
