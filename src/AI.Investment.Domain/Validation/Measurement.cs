using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Validation;

/// <summary>Whether a number in a report is a result, or the absence of one.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero so that a defaulted measurement is never read as a measured value.
/// The distinction between <see cref="Insufficient"/> and <see cref="Unavailable"/> is kept because
/// they call for different responses: too little data is a reason to wait, no data at all is a reason
/// to go and get some.
/// </remarks>
public enum MetricAvailability
{
    Unknown = 0,

    /// <summary>Measured from enough data to be worth printing.</summary>
    Measured = 1,

    /// <summary>Some data, but below the stated minimum. The value is withheld rather than shown small.</summary>
    Insufficient = 2,

    /// <summary>No data at all for this metric.</summary>
    Unavailable = 3,
}

/// <summary>
/// A number, or an honest statement that there is no number.
/// </summary>
/// <remarks>
/// <para>
/// The type exists so that "we could not measure this" is as easy to return as a value, and so that
/// it cannot be rendered as a zero. A calibration curve over four samples and a calibration curve
/// over four thousand are both a list of buckets; only one of them means anything, and a report that
/// prints both the same way is misleading whatever its footnotes say.
/// </para>
/// <para>
/// <see cref="Value"/> is null for every state except <see cref="MetricAvailability.Measured"/>, and
/// the factories are the only way to construct one, so an unmeasured metric cannot carry a number
/// that a caller might read anyway.
/// </para>
/// </remarks>
public sealed record Measurement
{
    private Measurement(MetricAvailability availability, decimal? value, int sampleSize, string explanation)
    {
        Availability = availability;
        Value = value;
        SampleSize = sampleSize;
        Explanation = explanation;
    }

    public MetricAvailability Availability { get; }

    /// <summary>The measured value. Null unless <see cref="IsMeasured"/>.</summary>
    public decimal? Value { get; }

    /// <summary>How many observations it was measured from. Meaningful even when there is no value.</summary>
    public int SampleSize { get; }

    public string Explanation { get; }

    public bool IsMeasured => Availability == MetricAvailability.Measured && Value is not null;

    public static Measurement Measured(decimal value, int sampleSize, string explanation)
    {
        if (sampleSize < 1)
        {
            throw new DomainValidationException(
                nameof(sampleSize),
                "A measured value must have been measured from something.");
        }

        return new Measurement(MetricAvailability.Measured, value, sampleSize, Text(explanation));
    }

    public static Measurement Insufficient(int sampleSize, int minimum) =>
        new(
            MetricAvailability.Insufficient,
            null,
            Math.Max(sampleSize, 0),
            string.Create(
                CultureInfo.InvariantCulture,
                $"insufficient data: {Math.Max(sampleSize, 0)} of the {minimum} observations this metric needs."));

    public static Measurement Unavailable(string reason) =>
        new(MetricAvailability.Unavailable, null, 0, Text(reason));

    public override string ToString() =>
        IsMeasured
            ? string.Create(CultureInfo.InvariantCulture, $"{Value} (n={SampleSize})")
            : Explanation;

    private static string Text(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainValidationException(
                nameof(value),
                "A measurement must say what it is, or why it is absent.")
            : value.Trim();
}
