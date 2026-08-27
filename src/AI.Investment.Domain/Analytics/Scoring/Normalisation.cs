using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics.Scoring;

/// <summary>
/// How a measured value is placed on a common 0-to-1 scale before it can be combined with others.
/// </summary>
/// <remarks>
/// <para>
/// A margin of 0.44 and a current ratio of 2.1 are not comparable numbers, and adding them
/// produces something with no meaning. A score therefore has to state, for every component, what
/// counts as the bottom of the useful range and what counts as the top - and state it as data that
/// is versioned with the score, not as a constant buried in a formula.
/// </para>
/// <para>
/// <strong>A floor above a ceiling means lower is better</strong> and needs no separate flag: the
/// span is simply negative and the same arithmetic inverts. Leverage is declared
/// <c>Between(2.0, 0.0)</c> and reads exactly as intended - two is the bad end.
/// </para>
/// <para>
/// Values outside the range clamp rather than extrapolate. A company with a 90% net margin is at
/// the top of the scale, and letting it score four times the top would let one extraordinary
/// figure overwhelm every other component - which is how a composite score stops measuring what it
/// claims to.
/// </para>
/// </remarks>
public sealed record Normalisation
{
    private Normalisation(decimal floor, decimal ceiling)
    {
        Floor = floor;
        Ceiling = ceiling;
    }

    /// <summary>The raw value that scores zero.</summary>
    public decimal Floor { get; }

    /// <summary>The raw value that scores one.</summary>
    public decimal Ceiling { get; }

    /// <summary>Whether a smaller raw value is the better one.</summary>
    public bool LowerIsBetter => Floor > Ceiling;

    public static Normalisation Between(decimal floor, decimal ceiling)
    {
        if (floor == ceiling)
        {
            throw new DomainValidationException(
                nameof(ceiling),
                $"A normalisation range of zero width ({floor}) maps every possible value to the " +
                "same score, which makes the component contribute nothing while appearing to.");
        }

        return new Normalisation(floor, ceiling);
    }

    /// <summary>Places <paramref name="raw"/> on the 0-to-1 scale, clamped at both ends.</summary>
    public decimal Apply(decimal raw)
    {
        var position = (raw - Floor) / (Ceiling - Floor);

        return Math.Clamp(position, 0m, 1m);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Floor:0.####} -> {Ceiling:0.####}");
}
