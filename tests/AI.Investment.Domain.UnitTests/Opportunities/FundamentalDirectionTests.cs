using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities.Fundamentals;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The fundamentals rule, and in particular the ways it can be handed the future by accident.
/// </summary>
/// <remarks>
/// <para>
/// The interesting tests here are not the arithmetic ones. They are the two that check the rule
/// refuses a series it should never have been given: one containing a figure published after the
/// decision, and one containing a figure published before the period it describes. Both are the
/// caller's mistake, and both produce a perfectly plausible probability if the rule simply trusts
/// what it is handed. A look-ahead that has to be prevented by discipline at every call site is one
/// that will eventually happen at one of them.
/// </para>
/// <para>
/// The restatement test is the other one that matters. Company filings republish old periods, so a
/// series read naively hands the decision point a value nobody had at the time. The rule must read
/// the latest publication <em>at or before</em> the decision, not the latest publication.
/// </para>
/// </remarks>
public sealed class FundamentalDirectionTests
{
    private const string Attribute = "financials.revenue";

    private static readonly DateTime Decision = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- what it claims -------------------------------------------------------------------------

    /// <summary>Four rising figures give three rising transitions, and a Laplace-smoothed 4/5.</summary>
    [Fact]
    public void A_rising_series_claims_a_high_probability()
    {
        var verdict = FundamentalDirectionRule.Evaluate(Series(100m, 110m, 120m, 130m), Decision);

        Assert.True(verdict.Fired);
        Assert.Equal(DirectionRefusal.None, verdict.Refusal);
        Assert.Equal(3, verdict.PriorTransitions);
        Assert.Equal(3, verdict.PriorIncreases);
        Assert.Equal(4m / 5m, verdict.Probability);
        Assert.Equal(130m, verdict.CurrentValue);
    }

    /// <summary>And a falling one gives 1/5, not zero.</summary>
    /// <remarks>
    /// The smoothing is the point of this test. An unsmoothed rule would state that the next figure
    /// cannot possibly rise, on the evidence of three observations, and would be scored as certain
    /// and wrong the first time it is. Stating certainty from three observations is an overclaim
    /// whatever happens next.
    /// </remarks>
    [Fact]
    public void A_falling_series_still_leaves_room_for_a_rise()
    {
        var verdict = FundamentalDirectionRule.Evaluate(Series(130m, 120m, 110m, 100m), Decision);

        Assert.True(verdict.Fired);
        Assert.Equal(0, verdict.PriorIncreases);
        Assert.Equal(1m / 5m, verdict.Probability);
        Assert.NotEqual(0m, verdict.Probability);
    }

    /// <summary>A flat transition counts as an increase, matching the event's own comparison.</summary>
    [Fact]
    public void A_flat_transition_counts_the_way_the_event_is_scored()
    {
        var verdict = FundamentalDirectionRule.Evaluate(Series(100m, 100m, 100m, 100m), Decision);

        Assert.Equal(3, verdict.PriorIncreases);
        Assert.True(FundamentalDirectionRule.Occurred(100m, 100m));
    }

    /// <summary>The declared event and the resolution use one comparison, so they cannot drift.</summary>
    [Fact]
    public void The_event_threshold_is_a_growth_ratio_of_zero()
    {
        Assert.Equal(0m, FundamentalDirectionRule.EventThreshold.Ratio);
        Assert.True(FundamentalDirectionRule.Occurred(101m, 100m));
        Assert.False(FundamentalDirectionRule.Occurred(99m, 100m));
    }

    // ---- when it stays silent -------------------------------------------------------------------

    /// <summary>Three figures are only two transitions, one short of what the rule will speak from.</summary>
    [Fact]
    public void Too_little_history_produces_no_claim()
    {
        var verdict = FundamentalDirectionRule.Evaluate(Series(100m, 110m, 120m), Decision);

        Assert.False(verdict.Fired);
        Assert.Equal(DirectionRefusal.NotEnoughPriorTransitions, verdict.Refusal);
    }

    [Fact]
    public void An_empty_series_produces_no_claim()
    {
        var verdict = FundamentalDirectionRule.Evaluate(Array.Empty<FiledFigure>(), Decision);

        Assert.False(verdict.Fired);
        Assert.Equal(DirectionRefusal.NoCurrentFigure, verdict.Refusal);
    }

    // ---- the look-ahead guards ------------------------------------------------------------------

    /// <summary>
    /// A figure published after the decision is refused rather than quietly used.
    /// </summary>
    [Fact]
    public void A_figure_from_after_the_decision_is_refused()
    {
        var series = Series(100m, 110m, 120m, 130m).ToList();

        series.Add(new FiledFigure(
            Attribute,
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            Decision.AddDays(1),
            999m));

        var verdict = FundamentalDirectionRule.Evaluate(series, Decision);

        Assert.False(verdict.Fired);
        Assert.Equal(DirectionRefusal.MalformedSeries, verdict.Refusal);
    }

    /// <summary>A figure public before the period it describes is impossible, and is refused.</summary>
    [Fact]
    public void A_figure_published_before_its_own_period_is_refused()
    {
        var series = new[]
        {
            new FiledFigure(
                Attribute,
                new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                100m),
        };

        var verdict = FundamentalDirectionRule.Evaluate(series, Decision);

        Assert.False(verdict.Fired);
        Assert.Equal(DirectionRefusal.MalformedSeries, verdict.Refusal);
    }

    // ---- restatements ---------------------------------------------------------------------------

    /// <summary>
    /// A decision reads the value that had been published by then, restatements included.
    /// </summary>
    /// <remarks>
    /// The 2023 figure is first reported as 120 and later revised to 300. Before the revision the
    /// decision knows 120; after it, 300 - and both are correct, because both are what was on the
    /// record at the time. What must never happen is the first case returning 300, which is what
    /// "read the latest publication" gives instead of "read the latest publication that had
    /// happened". The refusal of a publication dated after the decision is covered separately.
    /// </remarks>
    [Fact]
    public void The_value_used_is_the_one_that_had_been_published()
    {
        var series = new[]
        {
            Figure(2020, 100m, 2021),
            Figure(2021, 105m, 2022),
            Figure(2022, 110m, 2023),
            Figure(2023, 120m, 2024),
        };

        var verdict = FundamentalDirectionRule.Evaluate(series, Decision);

        Assert.Equal(120m, verdict.CurrentValue);

        // The same series with the 2023 period restated upwards, before the decision this time.
        var restated = series.Append(
            new FiledFigure(
                Attribute,
                new DateTime(2023, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                300m)).ToList();

        var afterRestatement = FundamentalDirectionRule.Evaluate(restated, Decision);

        Assert.Equal(300m, afterRestatement.CurrentValue);

        // A restatement is not a new period, so the transition count is unchanged.
        Assert.Equal(verdict.PriorTransitions, afterRestatement.PriorTransitions);
    }

    // ---- arguments -------------------------------------------------------------------------------

    [Fact]
    public void A_minimum_of_zero_transitions_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            FundamentalDirectionRule.Evaluate(Series(100m, 110m), Decision, minimumPriorTransitions: 0));

    [Fact]
    public void A_local_decision_time_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            FundamentalDirectionRule.Evaluate(
                Series(100m, 110m, 120m, 130m),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local)));

    // ---- fixtures ---------------------------------------------------------------------------------

    /// <summary>Consecutive December year-ends, each published the following March.</summary>
    private static List<FiledFigure> Series(params decimal[] values)
    {
        var figures = new List<FiledFigure>(values.Length);

        for (var i = 0; i < values.Length; i++)
        {
            figures.Add(Figure(2020 + i, values[i], 2021 + i));
        }

        return figures;
    }

    private static FiledFigure Figure(int periodYear, decimal value, int publishedYear) =>
        new(
            Attribute,
            new DateTime(periodYear, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(publishedYear, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            value);
}
