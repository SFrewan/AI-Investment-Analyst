using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities.Equity;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The screen the first discoverer uses, and the base rate underneath it.
/// </summary>
/// <remarks>
/// <para>
/// The assertions that matter here are the refusals and the arithmetic of the base rate. A screen
/// that produced a candidate from a short series, or stated a probability it had not counted, would
/// be manufacturing exactly the evidence the promotion gate exists to weigh - and the resulting
/// numbers would look entirely reasonable.
/// </para>
/// <para>
/// The parameters are shrunk to test sizes rather than left at the shipped ones. Sixty sessions of
/// fixture per assertion would make the arithmetic unreadable, and the arithmetic is the point.
/// </para>
/// </remarks>
public sealed class PriceRecoveryRuleTests
{
    private static readonly DateTime FirstSession = new(2026, 1, 2, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>Five sessions of history, a ten per cent fall, a two-session horizon, one trial.</summary>
    private static readonly PriceRecoveryParameters Small =
        new(MinimumSessions: 5, DrawdownRatio: 0.10m, HorizonSessions: 2, MinimumTrials: 1);

    /// <summary>
    /// A series that falls, recovers twice and fails once. Hand-counted in the assertions below.
    /// </summary>
    private static readonly decimal[] FallsAndRecovers =
        [100m, 110m, 120m, 115m, 100m, 95m, 130m, 100m, 90m, 100m];

    /// <summary>Four sessions: fewer than the rule reads.</summary>
    private static readonly decimal[] TooShort = [100m, 90m, 80m, 70m];

    /// <summary>A series that has never been below its own high.</summary>
    private static readonly decimal[] AlwaysRising =
        [100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m];

    /// <summary>
    /// A quiet series that ends on a new high three times its own level. Nothing in the middle of it
    /// is a drawdown against the peak that existed then; all of it is against the peak that came
    /// later.
    /// </summary>
    private static readonly decimal[] LateSpike =
        [100m, 95m, 100m, 96m, 100m, 97m, 100m, 300m, 270m];

    // ---- refusals ------------------------------------------------------------------------------

    [Fact]
    public void A_series_shorter_than_the_rule_reads_is_refused()
    {
        var verdict = PriceRecoveryRule.Evaluate(Series(TooShort), Small);

        Assert.Equal(PriceRecoveryRefusal.NotEnoughHistory, verdict.Refusal);
        Assert.Null(verdict.Candidate);
        Assert.False(verdict.HasCandidate);
    }

    [Fact]
    public void A_series_that_moves_backwards_in_time_is_refused()
    {
        var series = Series(FallsAndRecovers).ToList();

        series[4] = new ClosingPrice(series[2].SessionCloseUtc, series[4].Close);

        var verdict = PriceRecoveryRule.Evaluate(series, Small);

        Assert.Equal(PriceRecoveryRefusal.MalformedSeries, verdict.Refusal);
    }

    [Fact]
    public void A_close_that_is_not_positive_is_refused()
    {
        var series = Series(FallsAndRecovers).ToList();

        series[3] = new ClosingPrice(series[3].SessionCloseUtc, 0m);

        var verdict = PriceRecoveryRule.Evaluate(series, Small);

        Assert.Equal(PriceRecoveryRefusal.MalformedSeries, verdict.Refusal);
    }

    [Fact]
    public void A_series_at_its_own_high_produces_nothing()
    {
        var verdict = PriceRecoveryRule.Evaluate(Series(AlwaysRising), Small);

        Assert.Equal(PriceRecoveryRefusal.NoDrawdown, verdict.Refusal);
        Assert.Null(verdict.Candidate);
    }

    /// <summary>
    /// The condition holds today and the series cannot say how often it has worked, so there is no
    /// candidate. This is the refusal that stops the rule inventing the one number the whole
    /// opportunity rests on.
    /// </summary>
    [Fact]
    public void A_condition_with_too_few_past_occurrences_is_refused()
    {
        var demanding = Small with { MinimumTrials = 5 };

        var verdict = PriceRecoveryRule.Evaluate(Series(FallsAndRecovers), demanding);

        Assert.Equal(PriceRecoveryRefusal.NotEnoughOccurrences, verdict.Refusal);
        Assert.Null(verdict.Candidate);
        Assert.Contains("nobody measured", PriceRecoveryRule.Explain(verdict.Refusal), StringComparison.Ordinal);
    }

    // ---- the arithmetic ------------------------------------------------------------------------

    /// <summary>Every number in the candidate, hand-counted from the fixture above.</summary>
    [Fact]
    public void The_base_rate_is_counted_over_the_series_it_came_from()
    {
        var verdict = PriceRecoveryRule.Evaluate(Series(FallsAndRecovers), Small);

        Assert.True(verdict.HasCandidate);

        var candidate = verdict.Candidate!;

        // Three occurrences with a full horizon after them; two of them returned to their own prior
        // high inside it.
        Assert.Equal(3, candidate.Trials);
        Assert.Equal(2, candidate.Successes);
        Assert.Equal(0.6667m, candidate.SuccessProbability);

        // The target is the highest close the series contains - a price this instrument traded at.
        Assert.Equal(130m, candidate.TargetPrice);
        Assert.Equal(100m, candidate.EntryPrice);
        Assert.Equal(0.2308m, candidate.Drawdown);

        // The horizon is the calendar span those sessions actually occupied, not an assumed month.
        Assert.Equal(2, candidate.HorizonDays);
    }

    /// <summary>
    /// The peak a past occurrence is measured against is the peak that existed then.
    /// </summary>
    /// <remarks>
    /// This series ends on a new high three times its early level. Measured against the running
    /// peak, nothing in the middle of it is a ten per cent drawdown and the rule refuses for want of
    /// occurrences. Measured against the series' final peak - look-ahead inside the base rate - most
    /// of the middle would count, and the rule would state a rate. The refusal is the assertion.
    /// </remarks>
    [Fact]
    public void A_past_occurrence_is_measured_against_the_peak_that_existed_then()
    {
        var verdict = PriceRecoveryRule.Evaluate(Series(LateSpike), Small);

        Assert.Equal(PriceRecoveryRefusal.NotEnoughOccurrences, verdict.Refusal);
    }

    /// <summary>Confidence rises with the number of occurrences and never reaches certainty.</summary>
    [Fact]
    public void Confidence_grows_with_the_evidence_and_stops_short_of_certainty()
    {
        var few = PriceRecoveryRule.Evaluate(Series(FallsAndRecovers), Small).Candidate!;
        var many = PriceRecoveryRule.Evaluate(Series(Repeated(6)), Small).Candidate!;

        Assert.True(many.Trials > few.Trials);
        Assert.True(many.Confidence.Value > few.Confidence.Value);
        Assert.True(many.Confidence.Value < 1m);
        Assert.True(few.Confidence.Value > 0m);
    }

    /// <summary>Pure and total: the same series produces the same verdict, every time.</summary>
    [Fact]
    public void The_same_series_produces_the_same_verdict()
    {
        var first = PriceRecoveryRule.Evaluate(Series(FallsAndRecovers), Small);
        var second = PriceRecoveryRule.Evaluate(Series(FallsAndRecovers), Small);

        Assert.Equal(first, second);
    }

    // ---- the parameters themselves -------------------------------------------------------------

    [Theory]
    [InlineData(1, 0.10, 2, 1)]
    [InlineData(5, 0.00, 2, 1)]
    [InlineData(5, 1.00, 2, 1)]
    [InlineData(5, 0.10, 0, 1)]
    [InlineData(5, 0.10, 5, 1)]
    [InlineData(5, 0.10, 2, 0)]
    public void Settings_that_would_make_the_rule_meaningless_are_refused(
        int minimumSessions,
        double drawdown,
        int horizon,
        int minimumTrials)
    {
        var parameters = new PriceRecoveryParameters(
            minimumSessions,
            (decimal)drawdown,
            horizon,
            minimumTrials);

        Assert.Throws<DomainValidationException>(() =>
            PriceRecoveryRule.Evaluate(Series(FallsAndRecovers), parameters));
    }

    /// <summary>The shipped settings are themselves valid.</summary>
    [Fact]
    public void The_shipped_settings_are_valid()
    {
        PriceRecoveryParameters.Standard.Validate();

        Assert.Equal(60, PriceRecoveryParameters.Standard.MinimumSessions);
        Assert.Equal(0.10m, PriceRecoveryParameters.Standard.DrawdownRatio);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static List<ClosingPrice> Series(IReadOnlyList<decimal> closes)
    {
        var series = new List<ClosingPrice>(closes.Count);

        for (var i = 0; i < closes.Count; i++)
        {
            series.Add(new ClosingPrice(FirstSession.AddDays(i), closes[i]));
        }

        return series;
    }

    /// <summary>The falling-and-recovering shape, repeated, so occurrences accumulate.</summary>
    private static List<decimal> Repeated(int times)
    {
        var closes = new List<decimal>();

        for (var cycle = 0; cycle < times; cycle++)
        {
            closes.AddRange(FallsAndRecovers);
        }

        return closes;
    }
}
