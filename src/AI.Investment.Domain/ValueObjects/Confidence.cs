using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// How strongly the system believes a non-factual claim, on a scale from 0 to 1.
/// </summary>
/// <remarks>
/// <para>
/// Confidence attaches to interpretation and prediction. It must NOT attach to a fact: a filed
/// revenue figure is either what the source says or it is not, and dressing it in a probability
/// makes a sourced observation look like a judgement. That rule is enforced in <c>Claim</c>,
/// where the two kinds meet.
/// </para>
/// <para>
/// A number between 0 and 1 is not by itself meaningful. It becomes meaningful only when it has
/// been measured against outcomes - when claims stated at 0.8 turn out correct about 80 per cent
/// of the time. That measurement is the calibration work in the validation phase. Until then a
/// confidence value is an assertion by the producer, and <see cref="Band"/> exists to discourage
/// reading more precision into it than it has earned.
/// </para>
/// </remarks>
public sealed record Confidence
{
    private Confidence(decimal value) => Value = value;

    /// <summary>The confidence, in the closed interval [0, 1].</summary>
    public decimal Value { get; }

    /// <summary>
    /// A coarse band. Prefer this for display and for thresholds: it does not invite the reader
    /// to treat an uncalibrated 0.78 as meaningfully different from 0.75.
    /// </summary>
    public ConfidenceBand Band => Value switch
    {
        < 0.20m => ConfidenceBand.VeryLow,
        < 0.40m => ConfidenceBand.Low,
        < 0.60m => ConfidenceBand.Moderate,
        < 0.80m => ConfidenceBand.High,
        _ => ConfidenceBand.VeryHigh,
    };

    public static Confidence Create(decimal value)
    {
        if (value is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(value),
                $"Confidence must be between 0 and 1 inclusive. Received {value}.");
        }

        return new Confidence(value);
    }

    public bool IsAtLeast(Confidence other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Value >= other.Value;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value:0.##} ({Band})");
}

/// <summary>Coarse confidence bands. See <see cref="Confidence.Band"/> for why these exist.</summary>
public enum ConfidenceBand
{
    VeryLow = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
    VeryHigh = 4,
}
