using System.Globalization;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Equity;

/// <summary>One share split, as the platform knows it.</summary>
/// <param name="EffectiveAtUtc">The first session quoted in the new shares.</param>
/// <param name="Ratio">
/// New shares per old share. A four-for-one split is <c>4</c>; a one-for-ten reverse split is
/// <c>0.1</c>.
/// </param>
public sealed record ShareSplit(DateTime EffectiveAtUtc, decimal Ratio);

/// <summary>Why a series could not be trusted. <see cref="None"/> is zero.</summary>
public enum SeriesRefusal
{
    /// <summary>The series was adjusted and is continuous.</summary>
    None = 0,

    /// <summary>A stored split ratio is zero, negative, or not a number the arithmetic can use.</summary>
    UnusableSplitRatio = 1,

    /// <summary>
    /// A gap remains between two consecutive sessions that no known split explains.
    /// </summary>
    UnexplainedDiscontinuity = 2,
}

/// <summary>
/// A price series that has been made continuous, or a refusal saying why it could not be.
/// </summary>
public sealed record AdjustedSeries
{
    private AdjustedSeries(
        IReadOnlyList<ClosingPrice> prices,
        SeriesRefusal refusal,
        string explanation)
    {
        Prices = prices;
        Refusal = refusal;
        Explanation = explanation;
    }

    public IReadOnlyList<ClosingPrice> Prices { get; }

    public SeriesRefusal Refusal { get; }

    /// <summary>Why it was refused, in the words an operator reads. Empty when it was not.</summary>
    public string Explanation { get; }

    public bool IsUsable => Refusal == SeriesRefusal.None;

    public static AdjustedSeries Usable(IReadOnlyList<ClosingPrice> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return new AdjustedSeries(prices, SeriesRefusal.None, string.Empty);
    }

    public static AdjustedSeries Refused(SeriesRefusal refusal, string explanation) =>
        new([], refusal, explanation);
}

/// <summary>
/// Restates a raw closing-price series in today's shares, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this has to exist before the history gets deeper.</strong> The market-data
/// normaliser stores the raw close and never <c>adjusted_close</c>, deliberately: the vendor's
/// adjusted figure is rewritten by every later split and dividend, so the same row means different
/// things on different days and a point-in-time store cannot hold it. The consequence is that a
/// split leaves a step in the stored series. A four-for-one split looks exactly like a
/// seventy-five per cent overnight collapse, and the price-recovery screen would read it as the
/// deepest drawdown it has ever seen and score it with complete confidence. That is the one
/// failure mode in this platform that produces a confident wrong number instead of a refusal.
/// </para>
/// <para>
/// <strong>The arithmetic.</strong> A close is quoted in the shares that existed on its own
/// session. To restate the whole series in the shares that exist at the end of it, each close is
/// divided by the product of every split effective strictly after that session. Splits are
/// therefore applied to the past, never to the present, which is the same direction a vendor's
/// adjusted close moves and the reason that figure is unstable.
/// </para>
/// <para>
/// <strong>And then it checks its own work.</strong> Adjusting by the splits we know about does
/// not prove there were no others. After adjustment the series is walked for any remaining
/// session-to-session move larger than the stated tolerance, and one is enough to refuse the whole
/// series - because a step that size is a corporate action nobody told us about, or a bad row, and
/// both are indistinguishable from a genuine crash by arithmetic alone. Refusing costs a candidate;
/// guessing costs the platform its claim to know what it is looking at.
/// </para>
/// </remarks>
public static class SplitAdjustment
{
    /// <summary>
    /// The largest single-session move left unexplained before the series is refused.
    /// </summary>
    /// <remarks>
    /// Half. A one-day fall or rise of that size in a liquid equity is, in practice, a corporate
    /// action or a data defect rather than trading - and the cost of being wrong in the cautious
    /// direction is one missed candidate, while the cost of being wrong in the other direction is
    /// a scored opportunity built on a number that never happened.
    /// </remarks>
    public static decimal DefaultMaxUnexplainedMove => 0.5m;

    /// <summary>
    /// Restates <paramref name="prices"/> in the shares in issue at the end of the series.
    /// </summary>
    /// <param name="prices">Closes, oldest first.</param>
    /// <param name="splits">Every split known to the platform, in any order.</param>
    /// <param name="maxUnexplainedMove">
    /// The fraction beyond which a remaining session-to-session move refuses the series.
    /// </param>
    public static AdjustedSeries Apply(
        IReadOnlyList<ClosingPrice> prices,
        IReadOnlyList<ShareSplit> splits,
        decimal maxUnexplainedMove)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(splits);

        if (maxUnexplainedMove <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxUnexplainedMove),
                maxUnexplainedMove,
                "A tolerance of zero or less would refuse every series, including a correct one.");
        }

        // An empty or single-point series has no continuity to check and nothing to restate. The
        // screen refuses it for its own reasons - too little history - and saying so there rather
        // than here keeps one refusal taxonomy per question.
        if (prices.Count == 0)
        {
            return AdjustedSeries.Usable(prices);
        }

        foreach (var split in splits)
        {
            if (split.Ratio <= 0m)
            {
                return AdjustedSeries.Refused(
                    SeriesRefusal.UnusableSplitRatio,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"a split effective {split.EffectiveAtUtc:yyyy-MM-dd} states a ratio of {CanonicalNumber.Text(split.Ratio)}, which cannot restate a price."));
            }
        }

        var adjusted = new List<ClosingPrice>(prices.Count);

        foreach (var price in prices)
        {
            var factor = 1m;

            foreach (var split in splits)
            {
                // Strictly after: a close printed on the first session in the new shares is already
                // in the new shares, and dividing it again would invent a fall.
                if (split.EffectiveAtUtc > price.SessionCloseUtc)
                {
                    factor *= split.Ratio;
                }
            }

            adjusted.Add(factor == 1m
                ? price
                : new ClosingPrice(price.SessionCloseUtc, price.Close / factor));
        }

        return Discontinuity(adjusted, maxUnexplainedMove) is { } refusal
            ? refusal
            : AdjustedSeries.Usable(adjusted);
    }

    /// <summary>
    /// The first remaining step too large to be trading, or null when the series is continuous.
    /// </summary>
    private static AdjustedSeries? Discontinuity(
        List<ClosingPrice> prices,
        decimal maxUnexplainedMove)
    {
        for (var i = 1; i < prices.Count; i++)
        {
            var previous = prices[i - 1];
            var current = prices[i];

            // A non-positive close is a malformed series rather than a discontinuity, and the
            // screen already names that refusal. Skipping it here avoids dividing by zero and
            // leaves the more precise refusal to the rule.
            if (previous.Close <= 0m || current.Close <= 0m)
            {
                continue;
            }

            var move = Math.Abs(current.Close - previous.Close) / previous.Close;

            if (move <= maxUnexplainedMove)
            {
                continue;
            }

            // Built in one interpolated string: concatenating several with '+' produces an
            // ordinary string expression, which does not bind to the culture-aware overload.
            var percent = CanonicalNumber.Text(decimal.Round(move * 100m, 1));
            var from = previous.SessionCloseUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = current.SessionCloseUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            return AdjustedSeries.Refused(
                SeriesRefusal.UnexplainedDiscontinuity,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the close moved {percent}% between {from} and {to}, which no known split explains. A step that size is a corporate action the platform has not been told about, or a bad row; either way the series cannot be screened as it stands."));
        }

        return null;
    }
}
