using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// How close a figure quoted by an agent must be to the claim it is supposed to come from.
/// </summary>
/// <remarks>
/// <para>
/// A tolerance is necessary and it is dangerous, so it is a value object with a stated default
/// rather than a magic number at the comparison site. Necessary, because a model asked for a
/// margin will write <c>18.4%</c> where the claim holds <c>0.18437...</c>, and rejecting that
/// would reject every correct answer. Dangerous, because a loose tolerance is indistinguishable
/// from no check at all: at ten per cent, a fabricated figure lands inside the window often enough
/// to pass.
/// </para>
/// <para>
/// The default is half of one per cent relative, which accommodates ordinary display rounding and
/// nothing else, plus a tiny absolute floor so that comparisons against zero do not divide by it.
/// </para>
/// </remarks>
public sealed record GroundednessTolerance
{
    private GroundednessTolerance(decimal relative, decimal absolute)
    {
        Relative = relative;
        Absolute = absolute;
    }

    /// <summary>Half of one per cent, relative: display rounding and no more.</summary>
    public static GroundednessTolerance Default { get; } = new(0.005m, 0.000001m);

    /// <summary>Exact match only. Used where a figure is copied rather than reported.</summary>
    public static GroundednessTolerance Exact { get; } = new(0m, 0m);

    public decimal Relative { get; }

    public decimal Absolute { get; }

    public static GroundednessTolerance Create(decimal relative, decimal absolute)
    {
        if (relative is < 0m or > 0.05m)
        {
            throw new DomainValidationException(
                nameof(relative),
                "A relative groundedness tolerance must be between 0 and 0.05. Beyond five per cent " +
                "the check stops distinguishing a rounded figure from an invented one.");
        }

        if (absolute < 0m)
        {
            throw new DomainValidationException(
                nameof(absolute),
                "An absolute groundedness tolerance may not be negative.");
        }

        return new GroundednessTolerance(relative, absolute);
    }

    /// <summary>Whether <paramref name="quoted"/> is close enough to <paramref name="claimed"/>.</summary>
    public bool Matches(decimal quoted, decimal claimed)
    {
        var difference = Math.Abs(quoted - claimed);

        if (difference <= Absolute)
        {
            return true;
        }

        var magnitude = Math.Max(Math.Abs(quoted), Math.Abs(claimed));

        return magnitude > 0m && difference <= magnitude * Relative;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"±{Relative:0.####} rel, ±{Absolute:0.########} abs");
}
