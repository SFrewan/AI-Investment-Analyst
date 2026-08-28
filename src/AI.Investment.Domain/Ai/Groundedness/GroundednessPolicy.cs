namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>How thoroughly an agent's output is checked against its evidence.</summary>
/// <remarks>
/// <see cref="Strict"/> is zero so that the default - a value nobody set, a field that was never
/// assigned - is the strongest check rather than the weakest. Getting this the other way round
/// means a configuration mistake silently relaxes the one control that stands between a model's
/// invention and a stored score.
/// </remarks>
public enum GroundednessPolicy
{
    /// <summary>Structured figures and every numeral in prose must trace to the bundle.</summary>
    Strict = 0,

    /// <summary>
    /// Only the structured figure list is checked. For agents whose output carries no prose, where
    /// the narrative scan has nothing to look at.
    /// </summary>
    Structural = 1,
}
