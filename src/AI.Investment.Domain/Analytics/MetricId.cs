using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// The stable name of something the platform measures.
/// </summary>
/// <remarks>
/// <para>
/// Dotted and namespaced - <c>financial.revenue.growth</c>, <c>market.volatility.realised</c> -
/// for two reasons. A stored result must remain attributable to a formula long after the code that
/// produced it has changed, and domains added later (energy output, clinical throughput, logistics
/// latency) must be able to name their own measures without colliding with these.
/// </para>
/// <para>
/// Deliberately not an enum. An enum would make the set of measurable things a compile-time
/// property of this assembly, which is exactly the constraint that turns a general analytics
/// foundation into a stock analyser.
/// </para>
/// <para>
/// At least two segments are required, so every metric states the family it belongs to. A bare
/// <c>growth</c> is ambiguous the moment a second domain measures growth of something else.
/// </para>
/// </remarks>
public sealed record MetricId
{
    public const int MaxLength = 120;

    public const char SegmentSeparator = '.';

    public const int MinimumSegments = 2;

    private MetricId(string value, string family)
    {
        Value = value;
        Family = family;
    }

    /// <summary>The full dotted identifier, lower-case.</summary>
    public string Value { get; }

    /// <summary>The leading segment: the family this metric belongs to.</summary>
    public string Family { get; }

    public static MetricId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                nameof(value),
                "A metric identifier is required. A measurement with no name cannot be stored, " +
                "compared or explained.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A metric identifier may not exceed {MaxLength} characters. Received '{value}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-' && c != SegmentSeparator)
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"A metric identifier may contain only lower-case letters, digits, '-' and " +
                    $"'{SegmentSeparator}'. Received '{value}'.");
            }
        }

        var segments = normalised.Split(SegmentSeparator);

        if (segments.Length < MinimumSegments)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A metric identifier needs at least {MinimumSegments} segments so it names the " +
                $"family it belongs to - 'financial.revenue', not 'revenue'. Received '{value}'.");
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"A metric identifier may not contain an empty segment. Received '{value}'.");
            }
        }

        return new MetricId(normalised, segments[0]);
    }

    public override string ToString() => Value;
}
