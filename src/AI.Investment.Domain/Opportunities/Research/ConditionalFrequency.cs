using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Research;

/// <summary>One occasion a strategy could have spoken on, with its signal and what happened.</summary>
/// <param name="Subject">Whose case this is, for clustering and reporting.</param>
/// <param name="DecidedAtUtc">The instant the claim would have been made.</param>
/// <param name="ResolvedAtUtc">The instant the outcome became known.</param>
/// <param name="SignalBucket">Which side of the strategy's condition this case fell on.</param>
/// <param name="Occurred">Whether the declared event happened.</param>
/// <remarks>
/// Both instants are carried because the point-in-time rule needs them separately. A case informs
/// a later decision only once it has <em>resolved</em>, not merely once it has been decided - and
/// the gap between the two is exactly the window in which a naive backtest leaks the future.
/// </remarks>
public sealed record ResearchCase(
    string Subject,
    DateTime DecidedAtUtc,
    DateTime ResolvedAtUtc,
    int SignalBucket,
    bool Occurred);

/// <summary>A stated probability and what it turned out to be.</summary>
public sealed record ResearchPrediction(
    string Subject,
    DateTime DecidedAtUtc,
    DateTime ResolvedAtUtc,
    int SignalBucket,
    decimal Probability,
    bool Occurred,
    int PriorCases);

/// <summary>
/// States a probability from the frequency of past cases that shared this case's signal.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> Every strategy in this research stage is the same shape: a
/// condition that splits occasions into buckets, and an event that either happens or does not. What
/// distinguishes them is which condition, not how the probability is formed. Putting the probability
/// here means each strategy is a few lines that build cases, the point-in-time discipline is written
/// once, and a mistake in it is one mistake rather than five.
/// </para>
/// <para>
/// <strong>The rule that matters.</strong> A case may inform a decision only if it had
/// <em>resolved</em> strictly before that decision was taken. Using cases that had been decided but
/// not yet resolved is the subtlest look-ahead there is: the signal was public, so it feels
/// available, but the outcome that makes it informative was not. With a twenty-one session horizon
/// that is a month of borrowed hindsight on every prediction, and it inflates a score without ever
/// looking like a bug.
/// </para>
/// <para>
/// <strong>No free parameters.</strong> The frequency is Laplace-smoothed, so a bucket that has been
/// right three times out of three states four fifths rather than certainty. The minimum prior count
/// is fixed here rather than per strategy, so no strategy can be helped by choosing it.
/// </para>
/// </remarks>
public static class ConditionalFrequency
{
    /// <summary>
    /// How many resolved cases a bucket needs before the strategy will speak from it.
    /// </summary>
    /// <remarks>
    /// Twenty. Fixed once, for every strategy, and never varied against any evidence: a per-strategy
    /// minimum would be a parameter, and a parameter chosen after the scores were seen is the thing
    /// the admission gate refuses by name.
    /// </remarks>
    public const int MinimumPriorCases = 20;

    /// <summary>
    /// Walks the cases in decision order and states a probability wherever it has enough history.
    /// </summary>
    public static IReadOnlyList<ResearchPrediction> Score(
        IEnumerable<ResearchCase> cases,
        int minimumPriorCases = MinimumPriorCases)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (minimumPriorCases < 1)
        {
            throw new DomainValidationException(
                nameof(minimumPriorCases),
                "A frequency taken from no observations is not a frequency. One is the least that " +
                "can be called history, and the shipped value is twenty.");
        }

        var material = new List<ResearchCase>();

        foreach (var one in cases)
        {
            ArgumentNullException.ThrowIfNull(one);
            DateRange.EnsureUtc(one.DecidedAtUtc, nameof(cases));
            DateRange.EnsureUtc(one.ResolvedAtUtc, nameof(cases));

            if (one.ResolvedAtUtc <= one.DecidedAtUtc)
            {
                throw new DomainRuleViolationException(
                    "Research.ResolvedBeforeItWasDecided",
                    $"a case for '{one.Subject}' resolves at {one.ResolvedAtUtc:O}, at or before the " +
                    $"decision at {one.DecidedAtUtc:O}. An outcome already known at the moment of " +
                    "the claim is not something the claim was about.");
            }

            material.Add(one);
        }

        // Decision order, and separately resolution order with a pointer into it. Every case whose
        // outcome had landed by the current decision - and no case whose outcome had not - is what
        // the running counts hold.
        var byDecision = material
            .OrderBy(c => c.DecidedAtUtc)
            .ThenBy(c => c.Subject, StringComparer.Ordinal)
            .ToList();

        var byResolution = material
            .OrderBy(c => c.ResolvedAtUtc)
            .ThenBy(c => c.Subject, StringComparer.Ordinal)
            .ToList();

        var totals = new Dictionary<int, int>();
        var successes = new Dictionary<int, int>();
        var predictions = new List<ResearchPrediction>();
        var resolved = 0;

        foreach (var decision in byDecision)
        {
            while (resolved < byResolution.Count &&
                   byResolution[resolved].ResolvedAtUtc <= decision.DecidedAtUtc)
            {
                var landed = byResolution[resolved];

                totals[landed.SignalBucket] = totals.GetValueOrDefault(landed.SignalBucket) + 1;

                if (landed.Occurred)
                {
                    successes[landed.SignalBucket] = successes.GetValueOrDefault(landed.SignalBucket) + 1;
                }

                resolved++;
            }

            var seen = totals.GetValueOrDefault(decision.SignalBucket);

            if (seen < minimumPriorCases)
            {
                continue;
            }

            var won = successes.GetValueOrDefault(decision.SignalBucket);

            predictions.Add(new ResearchPrediction(
                decision.Subject,
                decision.DecidedAtUtc,
                decision.ResolvedAtUtc,
                decision.SignalBucket,
                (won + 1m) / (seen + 2m),
                decision.Occurred,
                seen));
        }

        return predictions;
    }

    /// <summary>How much the buckets actually differ, which is the only place any skill can come from.</summary>
    /// <remarks>
    /// A strategy whose buckets resolve at the same rate has conditioned on nothing, whatever its
    /// Brier score says. Reported alongside the score so that a number close to the base rate can be
    /// read as what it is.
    /// </remarks>
    public static decimal BucketSeparation(IEnumerable<ResearchPrediction> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        var byBucket = predictions
            .GroupBy(p => p.SignalBucket)
            .Select(g => (decimal)g.Count(p => p.Occurred) / g.Count())
            .ToList();

        return byBucket.Count < 2 ? 0m : byBucket.Max() - byBucket.Min();
    }

    public static string Describe(IReadOnlyList<ResearchPrediction> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        return predictions.Count == 0
            ? "no prediction"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{predictions.Count} predictions, separation {BucketSeparation(predictions):P2}");
    }
}
