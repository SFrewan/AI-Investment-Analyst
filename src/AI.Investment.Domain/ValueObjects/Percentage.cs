using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// A proportion, stored internally as a ratio where 1.0 means 100 per cent.
/// </summary>
/// <remarks>
/// <para>
/// The representation is stated explicitly and there is no ambiguous constructor, because the
/// single most common bug in this shape of code is unit confusion: one caller passes 15 meaning
/// "15 per cent" while another passes 0.15 meaning the same thing, and nothing complains. Both
/// entry points are named - <see cref="FromRatio"/> and <see cref="FromPercent"/> - so the unit
/// is visible at every call site.
/// </para>
/// <para>
/// The sanity bound exists for the same reason. A ratio outside +/-100 (that is, +/-10,000 per
/// cent) is far more likely to be a percent value passed to <see cref="FromRatio"/> than a real
/// proportion, so it is rejected rather than propagated. Negative values are allowed: a margin,
/// a return and a growth rate can all legitimately be negative.
/// </para>
/// </remarks>
public sealed record Percentage
{
    /// <summary>Ratios beyond this magnitude are rejected as probable unit confusion.</summary>
    public const decimal MaxAbsoluteRatio = 100m;

    private Percentage(decimal ratio) => Ratio = ratio;

    /// <summary>The proportion, where 1.0 means 100 per cent.</summary>
    public decimal Ratio { get; }

    /// <summary>The same proportion expressed in per cent, where 100 means 100 per cent.</summary>
    public decimal Percent => Ratio * 100m;

    public static Percentage Zero { get; } = new(0m);

    /// <summary>Creates a percentage from a ratio: 0.155 means 15.5 per cent.</summary>
    public static Percentage FromRatio(decimal ratio)
    {
        if (Math.Abs(ratio) > MaxAbsoluteRatio)
        {
            throw new DomainValidationException(
                nameof(ratio),
                $"A ratio of {ratio} is outside the accepted range of +/-{MaxAbsoluteRatio}. " +
                "A value this large usually means a percent value was passed to FromRatio; " +
                "use FromPercent instead.");
        }

        return new Percentage(ratio);
    }

    /// <summary>Creates a percentage from a percent value: 15.5 means 15.5 per cent.</summary>
    public static Percentage FromPercent(decimal percent) => FromRatio(percent / 100m);

    public Percentage Negate() => new(-Ratio);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Percent:0.####}%");
}
