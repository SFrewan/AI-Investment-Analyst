using AI.Investment.Domain.Opportunities.Equity;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// That a split is restated rather than screened, and that an unexplained step refuses.
/// </summary>
/// <remarks>
/// The stored close is the raw one, deliberately - the vendor's adjusted close is rewritten by
/// every later corporate action, so a point-in-time store cannot hold it. The consequence is that
/// a four-for-one split leaves a seventy-five per cent step in the series, which the price-recovery
/// screen reads as the deepest drawdown it has ever seen. These tests pin both halves of the
/// answer: restate what we know about, refuse what we do not.
/// </remarks>
public sealed class SplitAdjustmentTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 21, 0, 0, DateTimeKind.Utc);

    private const decimal Tolerance = 0.5m;

    // Hoisted rather than written inline at each call site: the analyzer objects to a constant
    // array argument passed repeatedly (CA1861), and naming each expectation says what it means.
    private static readonly decimal[] Unchanged = [100m, 101m, 102m, 103m];

    private static readonly decimal[] RestatedByFour = [100m, 101m, 101m, 102m];

    private static readonly decimal[] RestatedByReverse = [20m, 21m, 21m, 20m];

    private static readonly decimal[] FlatAfterTwoSplits = [100m, 100m, 100m, 100m];

    private static readonly decimal[] FlatPair = [100m, 100m];

    [Fact]
    public void A_series_with_no_splits_is_returned_unchanged()
    {
        var prices = Series(100m, 101m, 102m, 103m);

        var result = SplitAdjustment.Apply(prices, [], Tolerance);

        Assert.True(result.IsUsable);
        Assert.Equal(SeriesRefusal.None, result.Refusal);
        Assert.Equal(Unchanged, result.Prices.Select(p => p.Close).ToArray());
    }

    /// <summary>
    /// The case this exists for: a four-for-one split, which without adjustment is a 75% fall.
    /// </summary>
    [Fact]
    public void A_four_for_one_split_restates_the_history_rather_than_reading_as_a_collapse()
    {
        // Two sessions before the split at ~400, two after at ~100. Raw, this is a 75% crash.
        var prices = Series(400m, 404m, 101m, 102m);
        var splits = new[] { new ShareSplit(Start.AddDays(2), 4m) };

        var result = SplitAdjustment.Apply(prices, splits, Tolerance);

        Assert.True(result.IsUsable);

        // The two pre-split closes are divided by four; the two after are already in new shares.
        Assert.Equal(RestatedByFour, result.Prices.Select(p => p.Close).ToArray());
    }

    /// <summary>A reverse split moves the history the other way.</summary>
    [Fact]
    public void A_one_for_ten_reverse_split_restates_upward()
    {
        var prices = Series(2m, 2.1m, 21m, 20m);
        var splits = new[] { new ShareSplit(Start.AddDays(2), 0.1m) };

        var result = SplitAdjustment.Apply(prices, splits, Tolerance);

        Assert.True(result.IsUsable);
        Assert.Equal(RestatedByReverse, result.Prices.Select(p => p.Close).ToArray());
    }

    /// <summary>Two splits compound, and only on the sessions that precede each.</summary>
    [Fact]
    public void Consecutive_splits_compound_on_the_sessions_before_each_of_them()
    {
        var prices = Series(400m, 200m, 100m, 100m);

        var splits = new[]
        {
            new ShareSplit(Start.AddDays(1), 2m),
            new ShareSplit(Start.AddDays(2), 2m),
        };

        var result = SplitAdjustment.Apply(prices, splits, Tolerance);

        Assert.True(result.IsUsable);
        Assert.Equal(FlatAfterTwoSplits, result.Prices.Select(p => p.Close).ToArray());
    }

    /// <summary>
    /// A close printed on the split's own session is already in the new shares.
    /// </summary>
    /// <remarks>
    /// The off-by-one that would invent a fall on exactly one day. Strictly-after is the rule, and
    /// this is the test that holds it there.
    /// </remarks>
    [Fact]
    public void The_close_on_the_effective_session_is_not_adjusted_again()
    {
        var prices = Series(400m, 100m);
        var splits = new[] { new ShareSplit(Start.AddDays(1), 4m) };

        var result = SplitAdjustment.Apply(prices, splits, Tolerance);

        Assert.True(result.IsUsable);
        Assert.Equal(FlatPair, result.Prices.Select(p => p.Close).ToArray());
    }

    /// <summary>
    /// <strong>The guard.</strong> A step no split explains refuses the whole series.
    /// </summary>
    [Fact]
    public void An_unexplained_step_refuses_the_series_rather_than_screening_it()
    {
        var prices = Series(400m, 404m, 101m, 102m);

        var result = SplitAdjustment.Apply(prices, [], Tolerance);

        Assert.False(result.IsUsable);
        Assert.Equal(SeriesRefusal.UnexplainedDiscontinuity, result.Refusal);
        Assert.Empty(result.Prices);
        Assert.Contains("no known split explains", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("2026-01-07", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>A wrong split ratio must not silently half-fix the series.</summary>
    [Fact]
    public void A_split_that_does_not_account_for_the_step_still_refuses()
    {
        // A ten-for-one step described as a two-for-one leaves the series discontinuous.
        var prices = Series(400m, 404m, 40m, 41m);
        var splits = new[] { new ShareSplit(Start.AddDays(2), 2m) };

        var result = SplitAdjustment.Apply(prices, splits, Tolerance);

        Assert.False(result.IsUsable);
        Assert.Equal(SeriesRefusal.UnexplainedDiscontinuity, result.Refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_ratio_that_cannot_restate_a_price_refuses(int ratio)
    {
        var result = SplitAdjustment.Apply(
            Series(100m, 101m),
            [new ShareSplit(Start.AddDays(1), ratio)],
            Tolerance);

        Assert.False(result.IsUsable);
        Assert.Equal(SeriesRefusal.UnusableSplitRatio, result.Refusal);
    }

    /// <summary>
    /// An ordinary bad day is not a corporate action, and must not be refused.
    /// </summary>
    /// <remarks>
    /// The other side of the guard. A tolerance tight enough to catch every split would refuse
    /// real trading, and a screen that refuses everything is as useless as one that believes
    /// everything.
    /// </remarks>
    [Fact]
    public void A_large_but_believable_fall_is_still_screened()
    {
        // Twenty per cent in a session. Brutal, and entirely real.
        var prices = Series(100m, 80m, 78m, 79m);

        var result = SplitAdjustment.Apply(prices, [], Tolerance);

        Assert.True(result.IsUsable);
        Assert.Equal(4, result.Prices.Count);
    }

    [Fact]
    public void An_empty_series_is_left_for_the_screen_to_refuse_on_its_own_terms()
    {
        var result = SplitAdjustment.Apply([], [], Tolerance);

        Assert.True(result.IsUsable);
        Assert.Empty(result.Prices);
    }

    [Fact]
    public void A_tolerance_of_zero_is_rejected_rather_than_refusing_everything() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SplitAdjustment.Apply(Series(100m, 101m), [], 0m));

    private static List<ClosingPrice> Series(params decimal[] closes) =>
        closes
            .Select((close, index) => new ClosingPrice(Start.AddDays(index), close))
            .ToList();
}
