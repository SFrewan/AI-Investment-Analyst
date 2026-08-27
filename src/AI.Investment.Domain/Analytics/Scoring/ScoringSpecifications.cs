using AI.Investment.Domain.Analytics.Financial;

namespace AI.Investment.Domain.Analytics.Scoring;

/// <summary>
/// The scores this build ships, declared as data and versioned with the results they produce.
/// </summary>
/// <remarks>
/// <para>
/// Every range here is a stated judgement about what counts as good, and none of it is derived from
/// anything - a 25% net margin is the top of the scale because this specification says so, not
/// because the data proved it. That is exactly why the numbers live in a versioned specification
/// rather than inside a formula: they are arguable, they will be argued with, and when they change
/// the version changes with them so every score already stored keeps meaning what it meant.
/// </para>
/// <para>
/// A score is not a recommendation and not a prediction. It is an arithmetic summary of
/// measurements that were themselves derived from filings.
/// </para>
/// </remarks>
public static class ScoringSpecifications
{
    public static MetricId FinancialHealth { get; } = MetricId.Create("score.financial-health");

    /// <summary>
    /// Profitability, liquidity, leverage and cash generation, equally weighted.
    /// </summary>
    /// <remarks>
    /// Equal weights on purpose for v1. Unequal weights are a stronger claim about relative
    /// importance than anything measured so far supports, and inventing them would put a judgement
    /// inside a number that presents itself as deterministic.
    /// </remarks>
    public static ScoringSpecification FinancialHealthV1 { get; } = ScoringSpecification.Create(
        FinancialHealth,
        CalculationVersion.Create(1, 0),
        [
            ScoreComponent.Create(
                FinancialMetrics.NetMargin,
                weight: 1m,
                Normalisation.Between(0m, 0.25m)),

            ScoreComponent.Create(
                FinancialMetrics.CurrentRatio,
                weight: 1m,
                Normalisation.Between(1.0m, 3.0m)),

            // Lower is better, which the range says by running downwards.
            ScoreComponent.Create(
                FinancialMetrics.DebtToEquity,
                weight: 1m,
                Normalisation.Between(2.0m, 0m)),

            ScoreComponent.Create(
                FinancialMetrics.FreeCashFlowMargin,
                weight: 1m,
                Normalisation.Between(0m, 0.20m)),
        ],
        minimumCoverage: 0.75m,
        "Profitability, liquidity, leverage and cash generation, equally weighted, each placed on a " +
        "declared range. Three of the four components must be present.");

    public static ScoringEngine FinancialHealthEngine { get; } = new(FinancialHealthV1);
}
