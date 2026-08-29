using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Equity;

/// <summary>One closing price, and the session it closed.</summary>
/// <remarks>
/// The domain's own shape rather than a reference to the application's price-series type, so this
/// rule stays a pure function of numbers and can be exercised without a store, a clock or a cutoff.
/// </remarks>
public sealed record ClosingPrice(DateTime SessionCloseUtc, decimal Close);

/// <summary>Why a series produced no candidate. <see cref="None"/> is zero.</summary>
public enum PriceRecoveryRefusal
{
    /// <summary>A candidate was produced.</summary>
    None = 0,

    /// <summary>Fewer sessions than the rule will look at.</summary>
    NotEnoughHistory = 1,

    /// <summary>The series is out of order, repeats a session, or carries a price that is not positive.</summary>
    MalformedSeries = 2,

    /// <summary>The latest close is not far enough below the highest close in the series.</summary>
    NoDrawdown = 3,

    /// <summary>
    /// The same condition has not occurred often enough in this series, with a full horizon after
    /// it, to state how often it worked.
    /// </summary>
    NotEnoughOccurrences = 4,
}

/// <summary>How far, how long, and how much history the rule requires.</summary>
/// <remarks>
/// Every number here is a stated judgement rather than a derived one, which is exactly why they are
/// arguments carried alongside a version rather than constants inside the formula. Changing one
/// changes what a stored opportunity meant, and the version is what makes that visible.
/// </remarks>
public sealed record PriceRecoveryParameters(
    int MinimumSessions,
    decimal DrawdownRatio,
    int HorizonSessions,
    int MinimumTrials)
{
    /// <summary>
    /// The shipped settings: sixty sessions of history, a ten per cent drawdown, a recovery measured
    /// over twenty-one sessions, and at least five past occurrences before any rate is stated.
    /// </summary>
    public static PriceRecoveryParameters Standard { get; } =
        new(MinimumSessions: 60, DrawdownRatio: 0.10m, HorizonSessions: 21, MinimumTrials: 5);

    /// <summary>Refuses settings that would make the rule meaningless rather than strict.</summary>
    public void Validate()
    {
        if (MinimumSessions < 2)
        {
            throw new DomainValidationException(
                nameof(MinimumSessions),
                $"At least two sessions are needed for a peak and a close to be different things. " +
                $"Received {MinimumSessions}.");
        }

        if (DrawdownRatio is <= 0m or >= 1m)
        {
            throw new DomainValidationException(
                nameof(DrawdownRatio),
                $"A drawdown must be a proportion strictly between 0 and 1. Received {DrawdownRatio}.");
        }

        if (HorizonSessions < 1 || HorizonSessions >= MinimumSessions)
        {
            throw new DomainValidationException(
                nameof(HorizonSessions),
                "A recovery horizon must be at least one session and shorter than the history the " +
                $"rule requires. Received {HorizonSessions} against {MinimumSessions} sessions.");
        }

        if (MinimumTrials < 1)
        {
            throw new DomainValidationException(
                nameof(MinimumTrials),
                "At least one past occurrence is needed before a rate can be measured at all. " +
                $"Received {MinimumTrials}.");
        }
    }
}

/// <summary>A candidate the rule produced, with the measurement behind every number in it.</summary>
/// <remarks>
/// <para>
/// <see cref="TargetPrice"/> is the highest close the series actually contains - a price this
/// instrument traded at - not a forecast. <see cref="SuccessProbability"/> is the proportion of past
/// occurrences of this same condition, in this same series, that recovered to their own prior peak
/// within the horizon. Neither is an opinion, and neither could be produced without the evidence
/// cited beside them.
/// </para>
/// <para>
/// <see cref="Confidence"/> grows with <see cref="Trials"/> and never reaches certainty: a rate
/// measured over six occurrences and a rate measured over sixty are not the same claim, and a single
/// probability would present them as though they were.
/// </para>
/// </remarks>
public sealed record PriceRecoveryCandidate(
    decimal EntryPrice,
    decimal TargetPrice,
    decimal Drawdown,
    decimal SuccessProbability,
    int HorizonDays,
    int Trials,
    int Successes,
    Confidence Confidence);

/// <summary>What the rule concluded: a candidate, or a named reason there is none.</summary>
public sealed record PriceRecoveryVerdict(PriceRecoveryRefusal Refusal, PriceRecoveryCandidate? Candidate)
{
    public bool HasCandidate => Refusal == PriceRecoveryRefusal.None && Candidate is not null;
}

/// <summary>
/// A deterministic screen over one instrument's closing prices, and the base rate behind it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a screen, not a strategy engine.</strong> It answers one question - has this
/// instrument fallen a stated distance below its own highest close in the observed window, and how
/// often has that recovered in this same series - and it answers it from closing prices and nothing
/// else. There is no model, no fitting, no parameter search and no second rule waiting to be added
/// underneath this one.
/// </para>
/// <para>
/// <strong>Every number an opportunity needs is measured rather than asserted.</strong> An equity
/// opportunity's detail payload demands an entry price, a target price, a success probability and a
/// horizon, and an implementation that supplied any of them from nowhere would be manufacturing the
/// evidence the promotion gate exists to weigh. So the entry is the latest close, the target is the
/// highest close in the series, the probability is a base rate counted over the same series, and the
/// horizon is the calendar span the horizon's sessions actually occupied.
/// </para>
/// <para>
/// <strong>It refuses rather than guesses.</strong> Too little history, a malformed series, no
/// drawdown, or too few past occurrences to count a rate all produce a named refusal and no
/// candidate. "We could not measure this" is a first-class answer here for the same reason it is one
/// in the validation report: a probability nobody measured is worse than no opportunity at all.
/// </para>
/// <para>
/// Pure and total. No clock, no store, no randomness: the same series produces the same verdict on
/// every machine and on every replay, which is what makes a discovered opportunity reproducible
/// evidence rather than an event.
/// </para>
/// </remarks>
public static class PriceRecoveryRule
{
    /// <summary>The rule's identity, written into the score of every opportunity it produces.</summary>
    public static MetricId Metric { get; } = MetricId.Create("score.price-recovery-base-rate");

    /// <summary>The producer identity recorded on every opportunity this rule underlies.</summary>
    public const string DiscovererId = "discovery.price-recovery";

    /// <summary>
    /// The version of this arithmetic. Stored scores are comparable only within one.
    /// </summary>
    public static CalculationVersion Version { get; } = CalculationVersion.Create(1, 0);

    /// <summary>
    /// How much measurement it takes for the rule to be half-confident in its own base rate.
    /// </summary>
    /// <remarks>
    /// Ten occurrences. Confidence is <c>trials / (trials + 10)</c>, so five occurrences state a
    /// third and fifty state five sixths, and nothing ever states certainty. The shape is ordinary
    /// shrinkage towards "we do not know", and the constant is a judgement rather than a measurement
    /// - which is why it is named, documented and versioned with the rest of the rule.
    /// </remarks>
    public const int ConfidenceShrinkage = 10;

    /// <summary>Applies the rule to one instrument's closing prices, oldest first.</summary>
    public static PriceRecoveryVerdict Evaluate(
        IReadOnlyList<ClosingPrice> series,
        PriceRecoveryParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.Validate();

        if (series.Count < parameters.MinimumSessions)
        {
            return Refused(PriceRecoveryRefusal.NotEnoughHistory);
        }

        if (!IsWellFormed(series))
        {
            return Refused(PriceRecoveryRefusal.MalformedSeries);
        }

        var peak = Peak(series, series.Count - 1);
        var entry = series[^1].Close;
        var drawdown = (peak - entry) / peak;

        if (drawdown < parameters.DrawdownRatio)
        {
            return Refused(PriceRecoveryRefusal.NoDrawdown);
        }

        var (trials, successes) = BaseRate(series, parameters);

        if (trials < parameters.MinimumTrials)
        {
            // The condition holds today, and the series does not say how often it has worked.
            // Stating a probability here would be inventing the one number the whole opportunity
            // rests on.
            return Refused(PriceRecoveryRefusal.NotEnoughOccurrences);
        }

        var probability = decimal.Round((decimal)successes / trials, 4, MidpointRounding.ToEven);

        return new PriceRecoveryVerdict(
            PriceRecoveryRefusal.None,
            new PriceRecoveryCandidate(
                entry,
                peak,
                decimal.Round(drawdown, 4, MidpointRounding.ToEven),
                probability,
                HorizonDays(series, parameters.HorizonSessions),
                trials,
                successes,
                Confidence.Create(
                    decimal.Round(
                        (decimal)trials / (trials + ConfidenceShrinkage),
                        4,
                        MidpointRounding.ToEven))));
    }

    /// <summary>The refusal in words, recorded where a discoverer explains why it found nothing.</summary>
    public static string Explain(PriceRecoveryRefusal refusal) => refusal switch
    {
        PriceRecoveryRefusal.None =>
            "the condition holds and the series says how often it has worked.",

        PriceRecoveryRefusal.NotEnoughHistory =>
            "the series is shorter than the history this rule reads. A peak measured over a few " +
            "sessions is a recent high, not a peak.",

        PriceRecoveryRefusal.MalformedSeries =>
            "the series is out of order, repeats a session, or carries a price that is not positive. " +
            "A screen over a series it cannot trust is a screen over noise.",

        PriceRecoveryRefusal.NoDrawdown =>
            "the latest close is not far enough below the highest close in the series.",

        PriceRecoveryRefusal.NotEnoughOccurrences =>
            "the same condition has not occurred often enough in this series, with a full horizon " +
            "after it, for a recovery rate to be counted. The candidate is refused rather than " +
            "stated with a probability nobody measured.",

        _ => "an unrecognised refusal, which is itself a reason to produce nothing.",
    };

    /// <summary>Strictly increasing sessions, and every price positive.</summary>
    private static bool IsWellFormed(IReadOnlyList<ClosingPrice> series)
    {
        for (var i = 0; i < series.Count; i++)
        {
            var point = series[i];

            if (point is null || point.Close <= 0m || point.SessionCloseUtc.Kind != DateTimeKind.Utc)
            {
                return false;
            }

            if (i > 0 && point.SessionCloseUtc <= series[i - 1].SessionCloseUtc)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The highest close from the start of the series up to and including one index.</summary>
    private static decimal Peak(IReadOnlyList<ClosingPrice> series, int lastIndex)
    {
        var peak = series[0].Close;

        for (var i = 1; i <= lastIndex; i++)
        {
            if (series[i].Close > peak)
            {
                peak = series[i].Close;
            }
        }

        return peak;
    }

    /// <summary>
    /// How often the same condition has occurred in this series, and how often it recovered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each trial uses only the prices at or before it, so the peak a past occurrence is measured
    /// against is the peak that existed then. Using the whole series' peak would compare every past
    /// day against a high that had not happened yet, which is look-ahead inside a base rate - the
    /// most flattering kind, because it makes the rate look worse in one direction and better in the
    /// other depending on the shape of the series, and neither is checkable afterwards.
    /// </para>
    /// <para>
    /// A trial only counts when a full horizon of sessions follows it. Counting a half-finished one
    /// as a failure would understate the rate, and counting it as a success would overstate it.
    /// </para>
    /// </remarks>
    private static (int Trials, int Successes) BaseRate(
        IReadOnlyList<ClosingPrice> series,
        PriceRecoveryParameters parameters)
    {
        var trials = 0;
        var successes = 0;
        var runningPeak = series[0].Close;

        for (var i = 0; i < series.Count - parameters.HorizonSessions; i++)
        {
            if (series[i].Close > runningPeak)
            {
                runningPeak = series[i].Close;
            }

            if (i < parameters.MinimumSessions - 1)
            {
                // The same history requirement the live check applies. A rate counted over
                // occurrences the rule would have refused to look at is a rate for a different rule.
                continue;
            }

            var drawdown = (runningPeak - series[i].Close) / runningPeak;

            if (drawdown < parameters.DrawdownRatio)
            {
                continue;
            }

            trials++;

            for (var ahead = i + 1; ahead <= i + parameters.HorizonSessions; ahead++)
            {
                if (series[ahead].Close >= runningPeak)
                {
                    successes++;

                    break;
                }
            }
        }

        return (trials, successes);
    }

    /// <summary>
    /// The calendar span the horizon's sessions actually occupied at the end of this series.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed. Twenty-one sessions is about a month of weekdays, but how many
    /// calendar days that is depends on the market's holidays, and the opportunity's horizon is what
    /// decides when it expires.
    /// </remarks>
    private static int HorizonDays(IReadOnlyList<ClosingPrice> series, int horizonSessions)
    {
        var span = series[^1].SessionCloseUtc - series[series.Count - 1 - horizonSessions].SessionCloseUtc;
        var days = (int)Math.Ceiling(span.TotalDays);

        return days < 1 ? 1 : days;
    }

    private static PriceRecoveryVerdict Refused(PriceRecoveryRefusal refusal) => new(refusal, null);
}
