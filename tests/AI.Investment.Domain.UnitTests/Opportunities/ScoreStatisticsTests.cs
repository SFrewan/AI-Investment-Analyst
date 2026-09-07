using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities.Admission;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The two statistics the sample bar is derived from.
/// </summary>
/// <remarks>
/// These were written twice, once inside each rehearsal, before they were moved here. That is the
/// failure mode these tests exist against: two copies of an estimator drift, and because each one
/// only ever appears as a single number in a report, the drift is invisible. There is one
/// implementation now and it is checked against hand-computable cases.
/// </remarks>
public sealed class ScoreStatisticsTests
{
    // ---- component spread -------------------------------------------------------------------

    /// <summary>A forecaster who is always exactly right has no spread to speak of.</summary>
    [Fact]
    public void Perfect_predictions_have_no_spread()
    {
        var predictions = new[]
        {
            (1m, true), (1m, true), (0m, false), (0m, false),
        };

        Assert.Equal(0m, ScoreStatistics.ComponentSpread(predictions));
    }

    /// <summary>Two components of 0 and two of 1 have a sample standard deviation of 0.5774.</summary>
    /// <remarks>
    /// Hand-computable: components {0, 0, 1, 1}, mean 0.5, sum of squares 1, divided by n-1 = 3,
    /// square root 0.5774. Checked rather than asserted, because the sample size the gate derives
    /// scales with the square of this number.
    /// </remarks>
    [Fact]
    public void The_spread_is_the_sample_standard_deviation_of_the_components()
    {
        var predictions = new[]
        {
            (1m, true), (0m, false), (1m, false), (0m, true),
        };

        Assert.Equal(0.5774m, Math.Round(ScoreStatistics.ComponentSpread(predictions), 4));
    }

    /// <summary>One observation has no spread, and saying so beats dividing by zero.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Too_few_predictions_have_no_spread(int count) =>
        Assert.Equal(0m, ScoreStatistics.ComponentSpread(
            Enumerable.Repeat((0.5m, true), count)));

    [Fact]
    public void A_stated_probability_outside_the_scale_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            ScoreStatistics.ComponentSpread(new[] { (0.5m, true), (1.5m, false) }));

    // ---- intra-cluster correlation ----------------------------------------------------------

    /// <summary>
    /// Clusters that all behave alike carry no more information than one of them.
    /// </summary>
    [Fact]
    public void Clusters_that_agree_perfectly_are_perfectly_correlated()
    {
        var clusters = new[] { (10, 10), (10, 0), (10, 10), (10, 0) };

        Assert.Equal(1m, ScoreStatistics.IntraClusterCorrelation(clusters));
    }

    /// <summary>
    /// Clusters that each mirror the overall rate carry no clustering at all.
    /// </summary>
    [Fact]
    public void Clusters_that_all_match_the_grand_rate_are_uncorrelated()
    {
        var clusters = new[] { (10, 5), (10, 5), (10, 5), (10, 5) };

        Assert.Equal(0m, ScoreStatistics.IntraClusterCorrelation(clusters));
    }

    /// <summary>
    /// A negative estimate is noise around zero, and is reported as no discount rather than as a bonus.
    /// </summary>
    /// <remarks>
    /// The direction matters. Letting a negative estimate through would make the design effect less
    /// than one, which would <em>inflate</em> the effective sample above the number of predictions
    /// actually made - a strategy would gain evidence it does not have.
    /// </remarks>
    [Fact]
    public void A_negative_estimate_is_clamped_to_no_discount()
    {
        var clusters = new[] { (4, 2), (4, 2), (4, 2), (4, 2), (4, 2), (4, 2) };
        var rho = ScoreStatistics.IntraClusterCorrelation(clusters);

        Assert.InRange(rho, 0m, 1m);
        Assert.True(SampleRequirement.DesignEffect(24, 6, rho) >= 1m);
        Assert.True(SampleRequirement.EffectiveSample(24, 6, rho) <= 24);
    }

    /// <summary>Nothing to estimate from is nothing to estimate, and is reported as no discount.</summary>
    [Fact]
    public void Too_little_structure_produces_no_discount()
    {
        Assert.Equal(0m, ScoreStatistics.IntraClusterCorrelation(new[] { (10, 4) }));
        Assert.Equal(0m, ScoreStatistics.IntraClusterCorrelation(new[] { (1, 1), (1, 0) }));
        Assert.Equal(0m, ScoreStatistics.IntraClusterCorrelation(Array.Empty<(int, int)>()));
    }

    [Fact]
    public void A_malformed_cluster_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            ScoreStatistics.IntraClusterCorrelation(new[] { (0, 0), (4, 2) }));

        Assert.Throws<DomainValidationException>(() =>
            ScoreStatistics.IntraClusterCorrelation(new[] { (4, 5), (4, 2) }));

        Assert.Throws<DomainValidationException>(() =>
            ScoreStatistics.IntraClusterCorrelation(new[] { (4, -1), (4, 2) }));
    }

    /// <summary>
    /// The estimate is always usable by the discount that consumes it.
    /// </summary>
    /// <remarks>
    /// The contract between the two: whatever this returns, <see cref="SampleRequirement"/> must
    /// accept it, and the result must never exceed the raw count. Anything else and the two halves
    /// of the independence correction would only agree by luck.
    /// </remarks>
    [Theory]
    [InlineData(10, 10, 1)]
    [InlineData(3, 20, 7)]
    [InlineData(50, 4, 2)]
    public void Whatever_is_estimated_can_be_spent(int clusterCount, int size, int successes)
    {
        var clusters = Enumerable.Range(0, clusterCount)
            .Select(i => (size, i % 2 == 0 ? successes : size - successes))
            .ToList();

        var rho = ScoreStatistics.IntraClusterCorrelation(clusters);
        var total = clusterCount * size;

        Assert.InRange(rho, 0m, 1m);
        Assert.InRange(SampleRequirement.EffectiveSample(total, clusterCount, rho), 1, total);
    }
}
