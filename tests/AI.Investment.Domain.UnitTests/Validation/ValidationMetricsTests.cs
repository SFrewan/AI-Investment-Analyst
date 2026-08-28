using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Validation;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Validation;

/// <summary>
/// The rates, the calibration curve, and the refusal to print a number that is not one.
/// </summary>
/// <remarks>
/// Most of these are about the denominators. A hit rate is trivially arithmetic; what makes it
/// honest or dishonest is which predictions are in the bottom of the fraction, and whether it is
/// printed at all when there are four of them.
/// </remarks>
public sealed class ValidationMetricsTests
{
    private static IEnumerable<OutcomeLabel> Repeat(OutcomeLabel label, int times) =>
        Enumerable.Repeat(label, times);

    private static ConfusionMatrix Matrix(int tp, int fp, int tn, int fn, int minimum = 1) =>
        ConfusionMatrix.From(
            Repeat(OutcomeLabel.TruePositive, tp)
                .Concat(Repeat(OutcomeLabel.FalsePositive, fp))
                .Concat(Repeat(OutcomeLabel.TrueNegative, tn))
                .Concat(Repeat(OutcomeLabel.FalseNegative, fn)),
            minimum);

    // ---- hit rate is precision, and only precision ------------------------------------------

    /// <summary>
    /// Seven right out of ten calls is a hit rate of seventy per cent, whatever the abstentions did.
    /// </summary>
    [Fact]
    public void The_hit_rate_is_the_share_of_calls_to_act_that_were_right()
    {
        var matrix = Matrix(tp: 7, fp: 3, tn: 40, fn: 5);

        Assert.Equal(0.7m, matrix.HitRate.Value);
        Assert.Equal(10, matrix.HitRate.SampleSize);
        Assert.Equal(10, matrix.PositiveCalls);
    }

    /// <summary>
    /// The distinction that matters most in the report: a system that abstains from everything it is
    /// unsure of can post a superb accuracy and a mediocre hit rate, and the headline must be the
    /// second one.
    /// </summary>
    [Fact]
    public void Accuracy_and_the_hit_rate_are_different_numbers_and_are_not_confused()
    {
        var matrix = Matrix(tp: 2, fp: 8, tn: 88, fn: 2);

        Assert.Equal(0.2m, matrix.HitRate.Value);
        Assert.Equal(0.9m, matrix.Accuracy.Value);
        Assert.NotEqual(matrix.HitRate.Value, matrix.Accuracy.Value);
    }

    [Fact]
    public void False_positives_and_false_negatives_are_reported_against_their_own_denominators()
    {
        var matrix = Matrix(tp: 6, fp: 4, tn: 30, fn: 10);

        // Of ten calls to act, four were wrong.
        Assert.Equal(0.4m, matrix.FalsePositiveRate.Value);

        // Of sixteen occasions the event happened, ten were missed.
        Assert.Equal(10m / 16m, matrix.FalseNegativeRate.Value);
        Assert.Equal(6m / 16m, matrix.Recall.Value);
    }

    /// <summary>
    /// The excluded predictions stay visible. A sample that loses members silently selects itself.
    /// </summary>
    [Fact]
    public void Unresolved_unavailable_and_abstained_are_counted_separately_and_never_scored()
    {
        var matrix = ConfusionMatrix.From(
            Repeat(OutcomeLabel.TruePositive, 3)
                .Concat(Repeat(OutcomeLabel.Unresolved, 5))
                .Concat(Repeat(OutcomeLabel.Unavailable, 7))
                .Concat(Repeat(OutcomeLabel.Abstained, 11))
                .Concat(Repeat(OutcomeLabel.Unknown, 2)),
            minimumSample: 1);

        Assert.Equal(3, matrix.Scored);
        Assert.Equal(5, matrix.Unresolved);

        // The two unlabelled predictions are folded into unavailable rather than dropped, so the
        // total still accounts for every prediction that entered.
        Assert.Equal(9, matrix.Unavailable);
        Assert.Equal(11, matrix.Abstained);
        Assert.Equal(28, matrix.Total);
    }

    [Fact]
    public void A_rate_over_too_few_observations_is_withheld_rather_than_printed()
    {
        var matrix = Matrix(tp: 3, fp: 1, tn: 0, fn: 0, minimum: ConfusionMatrix.MinimumSample);

        Assert.False(matrix.HitRate.IsMeasured);
        Assert.Equal(MetricAvailability.Insufficient, matrix.HitRate.Availability);
        Assert.Equal(4, matrix.HitRate.SampleSize);
        Assert.Null(matrix.HitRate.Value);
        Assert.Contains("insufficient data", matrix.HitRate.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rate_with_an_empty_denominator_is_unavailable_rather_than_zero()
    {
        var matrix = Matrix(tp: 0, fp: 0, tn: 30, fn: 0);

        Assert.Equal(MetricAvailability.Unavailable, matrix.HitRate.Availability);
        Assert.Null(matrix.HitRate.Value);
        Assert.Contains("never put to the system", matrix.HitRate.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matrix_refuses_a_minimum_sample_of_zero()
    {
        Assert.Throws<DomainValidationException>(() => ConfusionMatrix.From([], 0));
    }

    // ---- calibration -------------------------------------------------------------------------

    /// <summary>
    /// A perfectly calibrated system: it says seventy per cent and it happens seventy per cent of
    /// the time, so the band's gap is zero.
    /// </summary>
    [Fact]
    public void A_well_calibrated_band_shows_no_gap()
    {
        var samples = Enumerable.Range(0, 100)
            .Select(index => (StatedRatio: 0.75m, Occurred: index % 4 != 0))
            .ToList();

        var curve = CalibrationCurve.From(samples);
        var band = curve.Bins.Single(bin => bin.LowerRatio == 0.7m);

        Assert.Equal(100, band.Count);
        Assert.Equal(0.75m, band.MeanStated.Value);
        Assert.Equal(0.75m, band.ObservedFrequency.Value);
        Assert.Equal(0m, band.Gap.Value);
    }

    /// <summary>Overconfidence shows as a positive gap, which is the direction that matters.</summary>
    [Fact]
    public void An_overconfident_band_shows_a_positive_gap()
    {
        var samples = Enumerable.Range(0, 100)
            .Select(index => (StatedRatio: 0.95m, Occurred: index % 2 == 0))
            .ToList();

        var curve = CalibrationCurve.From(samples);
        var band = curve.Bins.Single(bin => bin.LowerRatio == 0.9m);

        Assert.Equal(0.95m, band.MeanStated.Value);
        Assert.Equal(0.5m, band.ObservedFrequency.Value);
        Assert.Equal(0.45m, band.Gap.Value);
    }

    /// <summary>
    /// The number to beat: always saying fifty per cent scores 0.25, and this asserts the scale is
    /// the one everybody means by a Brier score.
    /// </summary>
    [Fact]
    public void The_brier_score_is_on_the_scale_it_claims_to_be_on()
    {
        var coinFlips = Enumerable.Range(0, 100)
            .Select(index => (StatedRatio: 0.5m, Occurred: index % 2 == 0))
            .ToList();

        Assert.Equal(0.25m, CalibrationCurve.From(coinFlips).BrierScore.Value);

        var perfect = Enumerable.Range(0, 100)
            .Select(index => (StatedRatio: index % 2 == 0 ? 1m : 0m, Occurred: index % 2 == 0))
            .ToList();

        Assert.Equal(0m, CalibrationCurve.From(perfect).BrierScore.Value);
    }

    [Fact]
    public void A_band_with_too_few_predictions_withholds_its_frequency()
    {
        var samples = Enumerable.Range(0, 30)
            .Select(index => (StatedRatio: index < 3 ? 0.15m : 0.85m, Occurred: true))
            .ToList();

        var curve = CalibrationCurve.From(samples);

        var thin = curve.Bins.Single(bin => bin.LowerRatio == 0.1m);
        var thick = curve.Bins.Single(bin => bin.LowerRatio == 0.8m);

        Assert.Equal(3, thin.Count);
        Assert.Equal(MetricAvailability.Insufficient, thin.ObservedFrequency.Availability);
        Assert.False(thin.Gap.IsMeasured);

        Assert.Equal(27, thick.Count);
        Assert.True(thick.ObservedFrequency.IsMeasured);
    }

    [Fact]
    public void An_empty_band_is_unavailable_and_the_curve_still_has_ten_of_them()
    {
        var curve = CalibrationCurve.From([]);

        Assert.Equal(CalibrationCurve.BinCount, curve.Bins.Count);
        Assert.All(curve.Bins, bin => Assert.Equal(MetricAvailability.Unavailable, bin.ObservedFrequency.Availability));
        Assert.Equal(MetricAvailability.Unavailable, curve.BrierScore.Availability);
        Assert.Contains("nothing to score", curve.BrierScore.Explanation, StringComparison.Ordinal);
    }

    /// <summary>The topmost band is closed at 1.0, or a stated certainty would fall out of the curve.</summary>
    [Fact]
    public void A_stated_probability_of_one_falls_in_the_top_band()
    {
        var curve = CalibrationCurve.From([(1m, true)], minimumSample: 1, minimumPerBin: 1);

        Assert.Equal(1, curve.Bins[^1].Count);
        Assert.Equal(1, curve.ResolvedCount);
    }

    [Fact]
    public void A_stated_probability_outside_zero_to_one_is_a_defect_and_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => CalibrationCurve.From([(1.5m, true)]));
        Assert.Throws<DomainValidationException>(() => CalibrationCurve.From([(-0.1m, true)]));
    }

    // ---- the measurement wrapper --------------------------------------------------------------

    [Fact]
    public void An_unmeasured_metric_cannot_carry_a_value()
    {
        Assert.Null(Measurement.Unavailable("nothing to measure").Value);
        Assert.Null(Measurement.Insufficient(3, 20).Value);
        Assert.Equal(3, Measurement.Insufficient(3, 20).SampleSize);
        Assert.Throws<DomainValidationException>(() => Measurement.Measured(1m, 0, "none"));
        Assert.Throws<DomainValidationException>(() => Measurement.Unavailable("  "));
    }
}
