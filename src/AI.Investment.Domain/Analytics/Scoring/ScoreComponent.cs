using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics.Scoring;

/// <summary>One measurement's part in a score: which metric, how much it counts, and on what scale.</summary>
public sealed record ScoreComponent
{
    private ScoreComponent(MetricId metric, decimal weight, Normalisation normalisation)
    {
        Metric = metric;
        Weight = weight;
        Normalisation = normalisation;
    }

    public MetricId Metric { get; }

    /// <summary>Relative importance. Weights are not required to sum to anything in particular.</summary>
    public decimal Weight { get; }

    public Normalisation Normalisation { get; }

    public static ScoreComponent Create(MetricId metric, decimal weight, Normalisation normalisation)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(normalisation);

        if (weight <= 0m)
        {
            throw new DomainValidationException(
                nameof(weight),
                $"A component's weight must be positive. A weight of {weight} either removes the " +
                "component while leaving it listed, or subtracts it - and subtraction is what the " +
                "normalisation range is for.");
        }

        return new ScoreComponent(metric, weight, normalisation);
    }

    public override string ToString() => $"{Metric} x{Weight} ({Normalisation})";
}
