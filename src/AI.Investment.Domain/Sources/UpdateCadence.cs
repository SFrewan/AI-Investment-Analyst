using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Sources;

/// <summary>How often a source is expected to publish something new.</summary>
public enum CadenceKind
{
    /// <summary>Publication is unpredictable - a filing, a press release, a news story.</summary>
    EventDriven = 0,

    Realtime = 1,
    Intraday = 2,
    Daily = 3,
    Weekly = 4,
    Monthly = 5,
    Quarterly = 6,
    Annual = 7,

    /// <summary>Fetched only when asked. Never considered stale on a timer.</summary>
    OnDemand = 8,
}

/// <summary>
/// The refresh interval a source is expected to meet.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes staleness decidable. "Is this data old?" has no answer without an
/// expectation to measure against: a quarterly filing three weeks old is perfectly current, a
/// price three weeks old is worthless. Freshness monitoring in a later stage compares elapsed
/// time against <see cref="ExpectedInterval"/>.
/// </para>
/// <para>
/// <see cref="CadenceKind.EventDriven"/> and <see cref="CadenceKind.OnDemand"/> have no interval,
/// and that is correct rather than missing - a source that publishes when something happens
/// cannot be late.
/// </para>
/// </remarks>
public sealed record UpdateCadence
{
    private UpdateCadence(CadenceKind kind, TimeSpan? expectedInterval)
    {
        Kind = kind;
        ExpectedInterval = expectedInterval;
    }

    public CadenceKind Kind { get; }

    /// <summary>Null for event-driven and on-demand sources, which cannot be late.</summary>
    public TimeSpan? ExpectedInterval { get; }

    public bool HasExpectedInterval => ExpectedInterval.HasValue;

    public static UpdateCadence EventDriven { get; } = new(CadenceKind.EventDriven, null);

    public static UpdateCadence OnDemand { get; } = new(CadenceKind.OnDemand, null);

    public static UpdateCadence Every(CadenceKind kind, TimeSpan expectedInterval)
    {
        if (kind is CadenceKind.EventDriven or CadenceKind.OnDemand)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"{kind} has no expected interval; use the corresponding factory instead.");
        }

        if (expectedInterval <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(expectedInterval),
                $"An expected refresh interval must be positive. Received {expectedInterval}.");
        }

        return new UpdateCadence(kind, expectedInterval);
    }

    public static UpdateCadence Daily() => Every(CadenceKind.Daily, TimeSpan.FromDays(1));

    public static UpdateCadence Quarterly() => Every(CadenceKind.Quarterly, TimeSpan.FromDays(92));

    /// <summary>
    /// Whether data last refreshed at <paramref name="lastRefreshedUtc"/> is overdue at
    /// <paramref name="nowUtc"/>. Always false when the source has no expected interval.
    /// </summary>
    public bool IsOverdue(DateTime lastRefreshedUtc, DateTime nowUtc, TimeSpan grace)
    {
        if (ExpectedInterval is not { } interval)
        {
            return false;
        }

        return nowUtc - lastRefreshedUtc > interval + grace;
    }

    public override string ToString() =>
        ExpectedInterval is { } interval ? $"{Kind} (~{interval})" : Kind.ToString();
}
