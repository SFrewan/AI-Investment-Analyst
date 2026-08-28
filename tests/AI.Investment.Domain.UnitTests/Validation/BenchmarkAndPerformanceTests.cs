using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Validation;

/// <summary>
/// The comparison the whole phase is judged against, and the arithmetic behind both sides of it.
/// </summary>
public sealed class BenchmarkAndPerformanceTests
{
    private static readonly DateTime Declared = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Index = IngestionSubject.Create("Security", "SPY");

    private static BenchmarkDefinition Benchmark(decimal cost = 0m, DateTime? declaredAt = null) =>
        BenchmarkDefinition.Create(
            "index buy-and-hold",
            Index,
            "security.close",
            BenchmarkRule.BuyAndHold,
            Money.Create(100_000m, Currency.Usd),
            Percentage.FromRatio(cost),
            declaredAt ?? Declared);

    private static PricePoint Price(int day, decimal price) =>
        new(Declared.AddDays(day), price, Declared.AddDays(day));

    // ---- the benchmark is fixed in advance and provable afterwards ---------------------------

    /// <summary>
    /// A benchmark chosen once the numbers are in is not a benchmark, and the run says so.
    /// </summary>
    [Fact]
    public void A_benchmark_declared_after_the_run_began_is_refused()
    {
        var benchmark = Benchmark(declaredAt: Declared.AddDays(10));

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            benchmark.EnsureDeclaredBefore(Declared.AddDays(1)));

        Assert.Equal(BenchmarkDefinition.DeclaredAfterTheRunRule, error.Rule);
        Assert.Contains("once the numbers are in", error.Message, StringComparison.Ordinal);

        // Declared before, or at the very instant of, the run is fine.
        benchmark.EnsureDeclaredBefore(Declared.AddDays(10));
    }

    /// <summary>
    /// The fingerprint is what lets a reader check that the benchmark described is the one used.
    /// </summary>
    [Fact]
    public void The_fingerprint_is_stable_and_changes_when_any_field_does()
    {
        var original = Benchmark();

        Assert.Equal(original.Fingerprint, Benchmark().Fingerprint);
        Assert.NotEqual(original.Fingerprint, Benchmark(cost: 0.001m).Fingerprint);
        Assert.NotEqual(original.Fingerprint, Benchmark(declaredAt: Declared.AddDays(1)).Fingerprint);

        var renamed = BenchmarkDefinition.Create(
            "something else",
            Index,
            "security.close",
            BenchmarkRule.BuyAndHold,
            Money.Create(100_000m, Currency.Usd),
            Percentage.Zero,
            Declared);

        Assert.NotEqual(original.Fingerprint, renamed.Fingerprint);
    }

    [Fact]
    public void A_benchmark_must_name_an_instrument_a_rule_and_real_capital()
    {
        Assert.Throws<DomainValidationException>(() => BenchmarkDefinition.Create(
            "no rule", Index, "security.close", BenchmarkRule.Unknown,
            Money.Create(1m, Currency.Usd), Percentage.Zero, Declared));

        Assert.Throws<DomainValidationException>(() => BenchmarkDefinition.Create(
            "the market", IngestionSubject.Sweep("Security"), "security.close", BenchmarkRule.BuyAndHold,
            Money.Create(1m, Currency.Usd), Percentage.Zero, Declared));

        Assert.Throws<DomainValidationException>(() => BenchmarkDefinition.Create(
            "no capital", Index, "security.close", BenchmarkRule.BuyAndHold,
            Money.Create(0m, Currency.Usd), Percentage.Zero, Declared));

        Assert.Throws<DomainValidationException>(() => BenchmarkDefinition.Create(
            "subsidised", Index, "security.close", BenchmarkRule.BuyAndHold,
            Money.Create(1m, Currency.Usd), Percentage.FromRatio(-0.01m), Declared));
    }

    // ---- the arithmetic, shared by both sides ------------------------------------------------

    [Fact]
    public void Buy_and_hold_is_the_move_from_the_first_price_to_the_last()
    {
        var result = PerformanceCalculator.BuyAndHold(
            [Price(0, 100m), Price(10, 110m), Price(20, 120m)],
            Percentage.Zero);

        Assert.True(result.IsMeasured);
        Assert.Equal(0.2m, result.Value);
    }

    /// <summary>
    /// Costs are charged on entry and on exit, and to both sides. An asymmetry here is the quietest
    /// way to make a backtest flatter itself.
    /// </summary>
    [Fact]
    public void Costs_are_charged_on_both_legs()
    {
        var free = PerformanceCalculator.RoundTripReturn(100m, 110m, Percentage.Zero);
        var charged = PerformanceCalculator.RoundTripReturn(100m, 110m, Percentage.FromRatio(0.01m));

        Assert.Equal(0.1m, free);
        Assert.True(charged < free);

        // Entry costs raise what was paid; exit costs reduce what was received.
        var expected = ((110m * 0.99m) - (100m * 1.01m)) / (100m * 1.01m);

        Assert.Equal(expected, charged);
    }

    [Fact]
    public void A_position_cannot_be_entered_at_a_price_of_zero()
    {
        Assert.Throws<DomainValidationException>(() =>
            PerformanceCalculator.RoundTripReturn(0m, 110m, Percentage.Zero));

        Assert.Throws<DomainValidationException>(() =>
            PerformanceCalculator.RoundTripReturn(-1m, 110m, Percentage.Zero));
    }

    /// <summary>
    /// An unordered series means the filtering that produced it is wrong, and a return computed from
    /// it would be meaningless rather than merely inaccurate.
    /// </summary>
    [Fact]
    public void An_out_of_order_price_series_fails_rather_than_returning_a_number()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            PerformanceCalculator.BuyAndHold([Price(10, 100m), Price(0, 110m)], Percentage.Zero));

        Assert.Equal("Validation.PriceSeriesOutOfOrder", error.Rule);
    }

    [Fact]
    public void A_series_with_no_price_at_each_end_is_unavailable_rather_than_flat()
    {
        var none = PerformanceCalculator.BuyAndHold([], Percentage.Zero);
        var one = PerformanceCalculator.BuyAndHold([Price(0, 100m)], Percentage.Zero);

        Assert.Equal(MetricAvailability.Unavailable, none.Availability);
        Assert.Equal(MetricAvailability.Unavailable, one.Availability);
        Assert.Null(none.Value);
    }

    [Fact]
    public void The_strategy_return_is_the_equal_weighted_mean_of_its_round_trips()
    {
        var trips = Enumerable.Range(0, 10)
            .Select(index => new RoundTrip(Declared, Declared.AddDays(30), index % 2 == 0 ? 0.10m : -0.04m))
            .ToList();

        var result = PerformanceCalculator.MeanRoundTripReturn(trips);

        Assert.True(result.IsMeasured);
        Assert.Equal(0.03m, result.Value);
        Assert.Equal(10, result.SampleSize);
    }

    [Fact]
    public void A_strategy_that_took_no_positions_has_no_return_rather_than_a_zero_one()
    {
        var result = PerformanceCalculator.MeanRoundTripReturn([]);

        Assert.Equal(MetricAvailability.Unavailable, result.Availability);
        Assert.Null(result.Value);
        Assert.Contains("took no positions", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_few_round_trips_withholds_the_return_rather_than_reporting_one_trade()
    {
        var result = PerformanceCalculator.MeanRoundTripReturn(
            [new RoundTrip(Declared, Declared.AddDays(30), 5m)]);

        Assert.Equal(MetricAvailability.Insufficient, result.Availability);
        Assert.Null(result.Value);
    }

    // ---- the comparison ------------------------------------------------------------------------

    [Fact]
    public void The_excess_is_the_difference_when_both_sides_were_measured()
    {
        var strategy = Measurement.Measured(0.12m, 30, "strategy");
        var benchmark = Measurement.Measured(0.08m, 250, "benchmark");

        var excess = PerformanceCalculator.Excess(strategy, benchmark);

        Assert.True(excess.IsMeasured);
        Assert.Equal(0.04m, excess.Value);

        // The sample size of a comparison is the smaller of its two sides.
        Assert.Equal(30, excess.SampleSize);
    }

    /// <summary>
    /// Two absences subtracted from each other is not a result, and must not be printed as zero.
    /// </summary>
    [Fact]
    public void An_excess_over_an_unmeasured_side_is_unavailable_rather_than_zero()
    {
        var excess = PerformanceCalculator.Excess(
            Measurement.Measured(0.12m, 30, "strategy"),
            Measurement.Unavailable("no benchmark prices"));

        Assert.False(excess.IsMeasured);
        Assert.Null(excess.Value);
        Assert.Contains("two absences", excess.Explanation, StringComparison.Ordinal);
    }
}
