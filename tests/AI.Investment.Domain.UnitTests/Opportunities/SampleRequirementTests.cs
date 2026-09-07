using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The bar that is derived from what a strategy claims rather than fixed for every strategy.
/// </summary>
/// <remarks>
/// <para>
/// The property under test throughout is the one that makes this a tightening rather than a
/// negotiation: <c>Required = max(floor, derived)</c>. A strategy can be asked for more than the
/// shipped hundred and can never be asked for less, whatever it claims and whatever the arithmetic
/// says. Several tests below exist only to pin that down, because a future change that let the
/// derived term win when it is smaller would be invisible in every other test in the suite.
/// </para>
/// <para>
/// The second property is that saying nothing is never cheaper than saying something. A strategy
/// that declares no claim is read as claiming the admission ceiling, which is the weakest claim that
/// could still be admitted and therefore needs the largest sample.
/// </para>
/// </remarks>
public sealed class SampleRequirementTests
{
    private static readonly DateTime Declared = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstPrediction = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Measured = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string Evidence = "evidence-base-a";
    private const string OtherEvidence = "evidence-base-b";

    private static readonly OpportunityType Type = OpportunityType.Create("equity-price-recovery");

    private static readonly AdmissionCriteria Criteria = AdmissionCriteria.Standard;

    // ---- the inverse normal ---------------------------------------------------------------------

    /// <summary>
    /// Written out rather than taken from a package, so it is checked against known values.
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(0.80, 0.8416212)]
    [InlineData(0.90, 1.2815516)]
    [InlineData(0.95, 1.6448536)]
    [InlineData(0.975, 1.9599640)]
    [InlineData(0.99, 2.3263479)]
    [InlineData(0.001, -3.0902323)]
    public void The_quantile_matches_the_published_values(double probability, double expected) =>
        Assert.Equal(expected, StandardNormal.Quantile(probability), 5);

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(-0.1d)]
    [InlineData(1.5d)]
    public void The_quantile_refuses_the_infinities(double probability) =>
        Assert.Throws<DomainValidationException>(() => StandardNormal.Quantile(probability));

    /// <summary>The tails are a separate branch of the approximation, so they are checked apart.</summary>
    [Fact]
    public void The_quantile_is_symmetric_about_the_median()
    {
        for (var p = 0.001; p < 0.5; p += 0.037)
        {
            Assert.Equal(-StandardNormal.Quantile(p), StandardNormal.Quantile(1 - p), 6);
        }
    }

    // ---- the floor ------------------------------------------------------------------------------

    /// <summary>
    /// The whole design in one assertion: a very strong claim needs a small sample on the
    /// arithmetic, and still may not go below the shipped floor.
    /// </summary>
    [Fact]
    public void A_strong_claim_never_buys_a_smaller_sample_than_the_floor()
    {
        var requirement = SampleRequirement.For(
            Declaration(claimedBrier: 0.02m),
            Scored(baseRate: 0.5m, spread: 0.30m),
            Criteria);

        Assert.True(requirement.Derived < Criteria.MinimumResolvedPredictions);
        Assert.Equal(Criteria.MinimumResolvedPredictions, requirement.Required);
        Assert.Equal(Criteria.MinimumResolvedPredictions, requirement.Floor);
    }

    /// <summary>
    /// And the converse: a claim that only just clears the ceiling needs far more than the floor.
    /// </summary>
    /// <remarks>
    /// This is the case the fixed hundred got wrong. Against a fifty-fifty reference of 0.25, a
    /// claim of 0.20 is a difference of 0.05 - and a hundred observations put that about 1.7
    /// standard errors from the reference, which establishes nothing.
    /// </remarks>
    [Fact]
    public void A_marginal_claim_is_asked_for_more_than_the_floor()
    {
        var requirement = SampleRequirement.For(
            Declaration(claimedBrier: 0.20m),
            Scored(baseRate: 0.5m, spread: 0.30m),
            Criteria);

        Assert.True(requirement.WasDerived);
        Assert.True(requirement.Derived > Criteria.MinimumResolvedPredictions);
        Assert.Equal(requirement.Derived, requirement.Required);

        // 0.25 reference, 0.05 gap, spread 0.30, 80% power at 5% one-sided.
        Assert.InRange(requirement.Derived, 200, 250);
    }

    /// <summary>The requirement falls as the claim strengthens, and never below the floor.</summary>
    [Fact]
    public void The_requirement_is_monotone_in_the_claim()
    {
        var previous = int.MaxValue;

        foreach (var claim in new[] { 0.24m, 0.22m, 0.20m, 0.15m, 0.10m, 0.05m })
        {
            var requirement = SampleRequirement.For(
                Declaration(claimedBrier: claim),
                Scored(baseRate: 0.5m, spread: 0.30m),
                Criteria);

            Assert.True(requirement.Derived <= previous);
            Assert.True(requirement.Required >= Criteria.MinimumResolvedPredictions);

            previous = requirement.Derived;
        }
    }

    /// <summary>Declaring nothing is read as claiming the ceiling, which is the costliest claim.</summary>
    [Fact]
    public void Declaring_no_claim_costs_the_same_as_claiming_the_ceiling()
    {
        var silent = SampleRequirement.For(
            Declaration(claimedBrier: null),
            Scored(baseRate: 0.5m, spread: 0.30m),
            Criteria);

        var atTheCeiling = SampleRequirement.For(
            Declaration(claimedBrier: Criteria.MaximumBrierScore),
            Scored(baseRate: 0.5m, spread: 0.30m),
            Criteria);

        Assert.Equal(atTheCeiling.Required, silent.Required);
        Assert.Equal(Criteria.MaximumBrierScore, silent.ClaimedBrier);
    }

    // ---- what cannot be derived ------------------------------------------------------------------

    /// <summary>
    /// A measurement that carries no base rate leaves the shipped floor standing, unchanged.
    /// </summary>
    /// <remarks>
    /// This is the compatibility property. Every measurement written before this feature existed
    /// omits these fields, and each one must land on exactly the bar it landed on before - never
    /// lower, and not accidentally higher either.
    /// </remarks>
    [Fact]
    public void A_measurement_without_a_base_rate_falls_back_to_the_floor()
    {
        var requirement = SampleRequirement.For(
            Declaration(claimedBrier: 0.20m),
            Scored(baseRate: null, spread: null),
            Criteria);

        Assert.False(requirement.WasDerived);
        Assert.Equal(Criteria.MinimumResolvedPredictions, requirement.Required);
    }

    /// <summary>
    /// A claim no better than the base rate cannot be established by any sample, and says so.
    /// </summary>
    /// <remarks>
    /// The rare-event hole in a fixed ceiling. When an event happens one time in ten, always saying
    /// "one in ten" scores 0.09 - so a strategy claiming 0.20 passes a 0.20 ceiling while carrying
    /// strictly less information than saying nothing. No sample size fixes that, and reporting a
    /// very large number would imply one does.
    /// </remarks>
    [Fact]
    public void A_claim_no_better_than_the_base_rate_is_unattainable()
    {
        var requirement = SampleRequirement.For(
            Declaration(claimedBrier: 0.20m),
            Scored(baseRate: 0.10m, spread: 0.30m),
            Criteria);

        Assert.True(requirement.IsUnattainable);
        Assert.Equal(0.09m, requirement.ReferenceBrier);
    }

    // ---- the independence discount ---------------------------------------------------------------

    /// <summary>Uncorrelated observations are worth themselves.</summary>
    [Fact]
    public void Independent_observations_are_not_discounted()
    {
        Assert.Equal(1m, SampleRequirement.DesignEffect(100, 10, 0m));
        Assert.Equal(100, SampleRequirement.EffectiveSample(100, 10, 0m));
    }

    /// <summary>One cluster of perfectly correlated observations is worth one observation.</summary>
    [Fact]
    public void Perfectly_correlated_observations_collapse_to_their_cluster_count()
    {
        Assert.Equal(1, SampleRequirement.EffectiveSample(100, 1, 1m));
        Assert.Equal(10, SampleRequirement.EffectiveSample(100, 10, 1m));
    }

    /// <summary>The worked example from the price rehearsal: 77 episodes in 8 months.</summary>
    [Theory]
    [InlineData(0.1, 41)]
    [InlineData(0.2, 28)]
    [InlineData(0.3, 21)]
    [InlineData(0.5, 14)]
    public void Clustered_episodes_are_worth_fewer_than_their_count(double rho, int expected) =>
        Assert.Equal(expected, SampleRequirement.EffectiveSample(77, 8, (decimal)rho));

    [Fact]
    public void The_discount_refuses_impossible_clustering()
    {
        Assert.Throws<DomainValidationException>(() => SampleRequirement.DesignEffect(100, 0, 0.2m));
        Assert.Throws<DomainValidationException>(() => SampleRequirement.DesignEffect(-1, 4, 0.2m));
        Assert.Throws<DomainValidationException>(() => SampleRequirement.DesignEffect(100, 4, 1.5m));
        Assert.Throws<DomainValidationException>(() => SampleRequirement.DesignEffect(100, 4, -0.1m));
    }

    /// <summary>A measurement that says nothing about clustering is counted as it always was.</summary>
    [Fact]
    public void An_unclustered_measurement_counts_its_rows()
    {
        var measurement = Scored(baseRate: null, spread: null);

        Assert.Equal(measurement.ResolvedPredictions, measurement.EffectiveResolvedPredictions);
    }

    /// <summary>Half a clustering correction is refused, because it looks like a whole one.</summary>
    [Fact]
    public void Clustering_needs_both_halves()
    {
        Assert.Throws<DomainValidationException>(() => StrategyMeasurement.Create(
            Type,
            120,
            Measurement.Measured(0.10m, 120, "measured"),
            Percentage.FromRatio(0m),
            FirstPrediction,
            Measured,
            Evidence,
            independentClusters: 8));

        Assert.Throws<DomainValidationException>(() => StrategyMeasurement.Create(
            Type,
            120,
            Measurement.Measured(0.10m, 120, "measured"),
            Percentage.FromRatio(0m),
            FirstPrediction,
            Measured,
            Evidence,
            intraClusterCorrelation: 0.2m));
    }

    // ---- the claim is inside the declaration ------------------------------------------------------

    /// <summary>
    /// A claim edited after the fact is a different declaration, not a kinder bar.
    /// </summary>
    [Fact]
    public void Editing_the_claim_changes_the_fingerprint()
    {
        var original = Declaration(claimedBrier: 0.20m);
        var softened = Declaration(claimedBrier: 0.24m);

        Assert.NotEqual(original.Fingerprint, softened.Fingerprint);
        Assert.NotEqual(Declaration(claimedBrier: null).Fingerprint, original.Fingerprint);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void A_claim_outside_the_scale_is_refused(double claim) =>
        Assert.Throws<DomainValidationException>(() => Declaration(claimedBrier: (decimal)claim));

    // ---- fixtures ---------------------------------------------------------------------------------

    private static StrategyEvent Declaration(decimal? claimedBrier) =>
        StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0m),
            horizonSessions: 21,
            Evidence,
            Declared,
            tuningBasisFingerprint: OtherEvidence,
            claimedBrier: claimedBrier);

    private static StrategyMeasurement Scored(decimal? baseRate, decimal? spread) =>
        StrategyMeasurement.Create(
            Type,
            120,
            Measurement.Measured(0.10m, 120, "measured"),
            Percentage.FromRatio(0m),
            FirstPrediction,
            Measured,
            Evidence,
            trialsInFamily: 1,
            baseRate: baseRate,
            componentStandardDeviation: spread);
}
