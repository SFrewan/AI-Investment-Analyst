namespace AI.Investment.Application.Ai.Agents;

/// <summary>How the news an agent read leans, overall.</summary>
/// <remarks>
/// <see cref="Unknown"/> is the zero member and is never a legitimate answer: the parser refuses
/// any value outside the named set rather than falling back to it, so a model writing something
/// unexpected produces a schema failure instead of a neutral-looking default.
/// <see cref="Mixed"/> exists so that "there is important news in both directions" does not have to
/// be flattened into <see cref="Neutral"/>, which would report the noisiest weeks as the quietest.
/// </remarks>
public enum NewsSentiment
{
    Unknown = 0,
    Negative = 1,
    Mixed = 2,
    Neutral = 3,
    Positive = 4,
}
