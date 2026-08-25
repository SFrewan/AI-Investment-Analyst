using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// A closed interval between two UTC instants.
/// </summary>
/// <remarks>
/// Both endpoints must be UTC, enforced at construction. This is not pedantry: a system that
/// compares a local timestamp with a UTC one produces answers that are wrong by an offset that
/// changes twice a year, and in a platform where "what was known on this date" determines
/// whether a backtest is meaningful, that class of error is not recoverable after the fact.
/// </remarks>
public sealed record DateRange
{
    private DateRange(DateTime startUtc, DateTime endUtc)
    {
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public DateTime StartUtc { get; }

    public DateTime EndUtc { get; }

    public TimeSpan Duration => EndUtc - StartUtc;

    public static DateRange Create(DateTime startUtc, DateTime endUtc)
    {
        EnsureUtc(startUtc, nameof(startUtc));
        EnsureUtc(endUtc, nameof(endUtc));

        if (endUtc < startUtc)
        {
            throw new DomainValidationException(
                nameof(endUtc),
                $"The end of a date range may not precede its start. Received {startUtc:O} to {endUtc:O}.");
        }

        return new DateRange(startUtc, endUtc);
    }

    public bool Contains(DateTime instantUtc)
    {
        EnsureUtc(instantUtc, nameof(instantUtc));
        return instantUtc >= StartUtc && instantUtc <= EndUtc;
    }

    public bool Overlaps(DateRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return StartUtc <= other.EndUtc && other.StartUtc <= EndUtc;
    }

    public override string ToString() => $"{StartUtc:O} .. {EndUtc:O}";

    internal static void EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(
                parameterName,
                $"A timestamp must be UTC (DateTimeKind.Utc). Received Kind={value.Kind}.");
        }
    }
}
