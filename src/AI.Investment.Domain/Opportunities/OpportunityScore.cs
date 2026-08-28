using System.Globalization;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// The deterministic score an opportunity was ranked by, with the specification version that
/// produced it.
/// </summary>
/// <remarks>
/// <para>
/// The version is the whole point. Ranking without it produces a list that cannot be compared with
/// last week's, because a change to the weights is indistinguishable from a change in the world -
/// and nobody notices, because both look like the ordering moving.
/// </para>
/// <para>
/// Built from a Phase 3 <see cref="MetricResult"/> rather than from a loose number, so a score can
/// only exist if a versioned calculator produced it. There is no factory taking a bare decimal.
/// </para>
/// </remarks>
public sealed record OpportunityScore
{
    private OpportunityScore(MetricId metric, decimal value, CalculationVersion version, DateTime asOfUtc)
    {
        Metric = metric;
        Value = value;
        Version = version;
        AsOfUtc = asOfUtc;
    }

    public MetricId Metric { get; }

    public decimal Value { get; }

    /// <summary>The scoring specification's version. A change here makes stored scores incomparable.</summary>
    public CalculationVersion Version { get; }

    /// <summary>The period the score describes.</summary>
    public DateTime AsOfUtc { get; }

    /// <summary>The only way to make a score: from a result a versioned calculator produced.</summary>
    public static OpportunityScore From(MetricResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Value.Unit != UnitOfMeasure.Ratio)
        {
            throw new DomainValidationException(
                nameof(result),
                $"An opportunity score must be a dimensionless ratio; received {result.Value.Unit}. " +
                "Ranking on a figure with units compares quantities of different things.");
        }

        return new OpportunityScore(result.Metric, result.Value.Amount, result.Version, result.AsOfUtc);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Metric}={Value:0.####} ({Version})");
}
