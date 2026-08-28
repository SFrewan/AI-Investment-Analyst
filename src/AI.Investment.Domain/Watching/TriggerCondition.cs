using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Watching;

/// <summary>How an observed value is compared against the threshold a watch names.</summary>
public enum TriggerComparison
{
    /// <summary>Not determined. Never fires.</summary>
    Unknown = 0,

    /// <summary>The observed value is at or above the threshold.</summary>
    AtOrAbove = 1,

    /// <summary>The observed value is at or below the threshold.</summary>
    AtOrBelow = 2,

    /// <summary>The observed value, ignoring direction, is at least the threshold.</summary>
    MovedAtLeast = 3,

    /// <summary>No value is compared; the interval since the last firing has elapsed.</summary>
    IntervalElapsed = 4,

    /// <summary>Any observation of the right type and target fires it.</summary>
    AnyObservation = 5,
}

/// <summary>
/// The deterministic predicate a watch fires on.
/// </summary>
/// <remarks>
/// <para>
/// A closed set of comparisons over a number and an interval. There is no expression language and
/// no callback: a condition that could run arbitrary logic would be a condition nobody could reason
/// about from the stored row, and the stored row is the thing an operator reads when they ask why
/// the platform woke up four hundred times last night.
/// </para>
/// <para>
/// <strong>Fail-closed.</strong> Every path that cannot answer returns false. An unrecognised
/// comparison, a missing threshold, a missing observation - none of them fire. The dangerous
/// misreading is a watch that fires on everything, because that is the one that costs money.
/// </para>
/// </remarks>
public sealed record TriggerCondition
{
    private TriggerCondition(TriggerComparison comparison, decimal? threshold, TimeSpan? interval)
    {
        Comparison = comparison;
        Threshold = threshold;
        Interval = interval;
    }

    public TriggerComparison Comparison { get; }

    public decimal? Threshold { get; }

    /// <summary>The interval for <see cref="TriggerComparison.IntervalElapsed"/>.</summary>
    public TimeSpan? Interval { get; }

    /// <summary>A threshold comparison against an observed number.</summary>
    public static TriggerCondition Compare(TriggerComparison comparison, decimal threshold)
    {
        if (comparison is TriggerComparison.Unknown
            or TriggerComparison.IntervalElapsed
            or TriggerComparison.AnyObservation)
        {
            throw new DomainValidationException(
                nameof(comparison),
                $"'{comparison}' does not compare an observed value against a threshold.");
        }

        if (comparison == TriggerComparison.MovedAtLeast && threshold < 0m)
        {
            throw new DomainValidationException(
                nameof(threshold),
                "A movement threshold is a magnitude and may not be negative; the comparison already " +
                "ignores direction.");
        }

        return new TriggerCondition(comparison, threshold, interval: null);
    }

    /// <summary>A schedule: fires when the interval has elapsed since the last firing.</summary>
    public static TriggerCondition Every(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(interval),
                "A schedule must have a positive interval. Zero would mean firing continuously, which " +
                "is the trigger storm cooldowns exist to prevent, written into the configuration.");
        }

        return new TriggerCondition(TriggerComparison.IntervalElapsed, threshold: null, interval);
    }

    /// <summary>Fires on any observation of the watch's type and target.</summary>
    /// <remarks>
    /// For the trigger types where the arrival of the thing is the event - a new filing, a news
    /// item - and there is no number to compare. The watch's cooldown is what keeps this bounded.
    /// </remarks>
    public static TriggerCondition OnAnyObservation() =>
        new(TriggerComparison.AnyObservation, threshold: null, interval: null);

    /// <summary>
    /// Whether the condition holds. Deterministic, and false whenever it cannot be determined.
    /// </summary>
    public bool IsMet(decimal? observedValue, DateTime? lastFiredAtUtc, DateTime referenceUtc, DateTime nowUtc)
    {
        switch (Comparison)
        {
            case TriggerComparison.AnyObservation:
                return true;

            case TriggerComparison.IntervalElapsed:
                if (Interval is null)
                {
                    return false;
                }

                var since = lastFiredAtUtc ?? referenceUtc;

                return nowUtc - since >= Interval.Value;

            case TriggerComparison.AtOrAbove:
                return Threshold is not null && observedValue is not null && observedValue.Value >= Threshold.Value;

            case TriggerComparison.AtOrBelow:
                return Threshold is not null && observedValue is not null && observedValue.Value <= Threshold.Value;

            case TriggerComparison.MovedAtLeast:
                return Threshold is not null && observedValue is not null &&
                    Math.Abs(observedValue.Value) >= Threshold.Value;

            case TriggerComparison.Unknown:
            default:
                // A condition this build cannot interpret does not fire. The alternative reading -
                // "we could not tell, so go ahead" - is the one that spends money.
                return false;
        }
    }

    public override string ToString() =>
        Comparison switch
        {
            TriggerComparison.IntervalElapsed => $"every {Interval}",
            TriggerComparison.AnyObservation => "on any observation",
            _ => string.Create(CultureInfo.InvariantCulture, $"{Comparison} {Threshold}"),
        };
}
