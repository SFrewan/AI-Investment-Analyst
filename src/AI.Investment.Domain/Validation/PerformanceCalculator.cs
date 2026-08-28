using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>One price, and when it became public.</summary>
/// <remarks>
/// The publication time travels with the price for the same reason it travels with everything else
/// here: a backtest that reads a close before it was published is measuring a system nobody could
/// have operated. The point-in-time guard is applied to these before they reach the calculator, and
/// the calculator refuses a series whose points are out of order so that a filtering mistake shows up
/// as a failure rather than as a return.
/// </remarks>
public sealed record PricePoint(DateTime AtUtc, decimal Price, DateTime PublishedAtUtc);

/// <summary>One round trip: in at a price, out at a price, costs charged both ways.</summary>
public sealed record RoundTrip(DateTime EnteredAtUtc, DateTime ExitedAtUtc, decimal Return);

/// <summary>
/// The arithmetic of "did it make money", used identically by the strategy and the benchmark.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, two callers, deliberately. The commonest way a backtest flatters itself is not
/// a dramatic bug but a small asymmetry: the strategy priced on closes and the benchmark on opens,
/// costs charged to one and not the other, a rounding convention that differs by a basis point. None
/// of those survive both sides going through the same function with the same cost model.
/// </para>
/// <para>
/// Returns are simple, not compounded, and equal-weighted across round trips. That is a choice, it
/// is stated in the report, and it is applied to both sides. It is also the least flattering
/// reasonable choice for a strategy that trades often, which is the right direction for a measurement
/// to err in.
/// </para>
/// </remarks>
public static class PerformanceCalculator
{
    /// <summary>Below this many round trips, a total return is a story about one or two trades.</summary>
    public const int MinimumRoundTrips = 5;

    /// <summary>The return of one round trip, with costs charged on entry and on exit.</summary>
    public static decimal RoundTripReturn(decimal entryPrice, decimal exitPrice, Percentage costPerTrade)
    {
        ArgumentNullException.ThrowIfNull(costPerTrade);

        if (entryPrice <= 0m)
        {
            throw new DomainValidationException(
                nameof(entryPrice),
                "A position cannot be entered at a price of zero or less. A zero price is missing " +
                "data wearing a number's clothes.");
        }

        var cost = costPerTrade.Ratio;

        // Charged on both legs, to both sides. Entry costs raise the price paid; exit costs reduce
        // the price received.
        var effectiveEntry = entryPrice * (1m + cost);
        var effectiveExit = exitPrice * (1m - cost);

        return (effectiveExit - effectiveEntry) / effectiveEntry;
    }

    /// <summary>
    /// Buy at the first price in the series, hold, sell at the last. The naive benchmark.
    /// </summary>
    public static Measurement BuyAndHold(
        IReadOnlyList<PricePoint> prices,
        Percentage costPerTrade)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(costPerTrade);

        if (prices.Count < 2)
        {
            return Measurement.Unavailable(
                $"buy-and-hold needs a price at each end of the window and there {(prices.Count == 1 ? "is 1" : "are 0")}.");
        }

        EnsureOrdered(prices);

        var first = prices[0];
        var last = prices[^1];

        return Measurement.Measured(
            RoundTripReturn(first.Price, last.Price, costPerTrade),
            prices.Count,
            string.Create(
                CultureInfo.InvariantCulture,
                $"buy-and-hold from {first.AtUtc:O} to {last.AtUtc:O}, costs charged on both legs"));
    }

    /// <summary>
    /// The equal-weighted mean return of a set of round trips. The strategy side of the comparison.
    /// </summary>
    public static Measurement MeanRoundTripReturn(
        IReadOnlyList<RoundTrip> roundTrips,
        int minimumRoundTrips = MinimumRoundTrips)
    {
        ArgumentNullException.ThrowIfNull(roundTrips);

        if (minimumRoundTrips < 1)
        {
            throw new DomainValidationException(
                nameof(minimumRoundTrips),
                "A minimum of zero would report a total return computed from nothing.");
        }

        if (roundTrips.Count == 0)
        {
            return Measurement.Unavailable(
                "the strategy took no positions in the window, so it has no return to compare.");
        }

        return roundTrips.Count < minimumRoundTrips
            ? Measurement.Insufficient(roundTrips.Count, minimumRoundTrips)
            : Measurement.Measured(
                roundTrips.Sum(trip => trip.Return) / roundTrips.Count,
                roundTrips.Count,
                "equal-weighted mean round-trip return, costs charged on both legs");
    }

    /// <summary>
    /// The difference between the two, when both were measurable. Positive means the system won.
    /// </summary>
    public static Measurement Excess(
        Measurement strategy,
        Measurement benchmark)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(benchmark);

        if (!strategy.IsMeasured || !benchmark.IsMeasured)
        {
            return Measurement.Unavailable(
                "one side of the comparison could not be measured, so the difference between them " +
                "is not a result. It is two absences subtracted from each other.");
        }

        return Measurement.Measured(
            strategy.Value!.Value - benchmark.Value!.Value,
            Math.Min(strategy.SampleSize, benchmark.SampleSize),
            "strategy return minus benchmark return; positive means the system beat buying the index");
    }

    private static void EnsureOrdered(IReadOnlyList<PricePoint> prices)
    {
        for (var index = 1; index < prices.Count; index++)
        {
            if (prices[index].AtUtc < prices[index - 1].AtUtc)
            {
                throw new DomainRuleViolationException(
                    "Validation.PriceSeriesOutOfOrder",
                    $"the price at {prices[index].AtUtc:O} follows one at {prices[index - 1].AtUtc:O}. " +
                    "An unordered series means the filtering that produced it is wrong, and a return " +
                    "computed from it would be meaningless rather than merely inaccurate.");
            }
        }
    }
}
