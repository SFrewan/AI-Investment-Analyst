using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Validation;

/// <summary>Everything a validation run needs, declared before it starts.</summary>
/// <param name="Window">The period measured, and the horizon each prediction is judged over.</param>
/// <param name="EventThreshold">The realised move at or above which the event counts as having happened.</param>
/// <param name="Methodology">The version of the method under test.</param>
/// <param name="Benchmark">The naive comparison, declared in advance.</param>
/// <param name="PriceAttribute">The observation attribute prices are read from.</param>
public sealed record ValidationRequest(
    EvaluationWindow Window,
    Percentage EventThreshold,
    CalculationVersion Methodology,
    BenchmarkDefinition Benchmark,
    string PriceAttribute);

/// <summary>
/// Runs a validation and produces the report. Measures; never tunes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this class changes the system it measures. It reads history, judges what was knowable,
/// labels what happened, counts, compares against a benchmark declared in advance, and writes down the
/// answer. There is no feedback path: no threshold is adjusted from a result, no model is refitted, no
/// prediction is re-scored. That separation is the difference between validation and the far more
/// common activity of searching a parameter space until the report looks good.
/// </para>
/// <para>
/// <strong>The run fails rather than guesses.</strong> If any candidate's admissibility cannot be
/// established, the run says so in the report and excludes it; if the benchmark was declared after the
/// run began, the run refuses outright. Both are cases where continuing would produce a number that
/// looks exactly like a real one.
/// </para>
/// <para>
/// Autonomy is untouched. The shadow comparison reads Phase 6's measurement records and counts them.
/// It cannot execute anything: it never sees a gateway, an effect or an authorisation window, and the
/// records it reads were inert when they were written.
/// </para>
/// </remarks>
public sealed class ValidationService
{
    private readonly IValidationHistory _history;
    private readonly IPredictionCatalogue _predictions;
    private readonly IShadowDecisionStore _shadow;
    private readonly IClock _clock;

    public ValidationService(
        IValidationHistory history,
        IPredictionCatalogue predictions,
        IShadowDecisionStore shadow,
        IClock clock)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _predictions = predictions ?? throw new ArgumentNullException(nameof(predictions));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ValidationReport> RunAsync(
        ValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _clock.UtcNow;
        var runId = Guid.NewGuid();

        // The benchmark must predate the run. Checked first, because everything after this point
        // would otherwise be measured against a comparison chosen with the answer in view.
        request.Benchmark.EnsureDeclaredBefore(startedAt);

        var observationCutoff = KnowledgeCutoff.At(startedAt);
        var candidates = await _predictions.GetAsync(request.Window, cancellationToken).ConfigureAwait(false);
        var replay = BacktestEngine.Replay(request.Window, candidates);

        var gaps = new List<DataGap>();
        var labels = new List<OutcomeLabel>();
        var calibrationSamples = new List<(decimal StatedRatio, bool Occurred)>();
        var withoutProbability = 0;
        var roundTrips = new List<RoundTrip>();
        var labelsByProposal = new Dictionary<Guid, OutcomeLabel>();

        var proposalIds = candidates
            .Where(c => c.ProposalId is not null)
            .ToDictionary(c => c.PredictionId, c => c.ProposalId!.Value);

        foreach (var prediction in replay.Admitted)
        {
            var outcome = await ResolveAsync(prediction, request, observationCutoff, cancellationToken)
                .ConfigureAwait(false);

            var label = OutcomeLabeller.Label(prediction, outcome, request.EventThreshold, startedAt);

            labels.Add(label);

            if (proposalIds.TryGetValue(prediction.PredictionId, out var proposalId))
            {
                labelsByProposal[proposalId] = label;
            }

            if (label is OutcomeLabel.TruePositive or OutcomeLabel.FalsePositive
                or OutcomeLabel.TrueNegative or OutcomeLabel.FalseNegative)
            {
                if (prediction.StatedProbability is null)
                {
                    withoutProbability++;
                }
                else
                {
                    calibrationSamples.Add((
                        prediction.StatedProbability.Ratio,
                        label is OutcomeLabel.TruePositive or OutcomeLabel.FalseNegative));
                }
            }

            // Only the calls to act become positions. A prediction the system declined to act on
            // costs nothing and earns nothing, and counting it as a flat trade would dilute the
            // return towards zero and make a bad strategy look merely unexciting.
            if (prediction.Direction == PredictionDirection.Positive && outcome is not null)
            {
                roundTrips.Add(new RoundTrip(
                    prediction.DecidedAtUtc,
                    prediction.ResolvesAtUtc,
                    outcome.RealisedReturn.Ratio));
            }
        }

        if (replay.HasUndeterminableHistory)
        {
            gaps.Add(new DataGap(
                "point-in-time admissibility",
                $"{replay.Refused.Count(r => r.WasUndeterminable)} predictions carry no record of when " +
                "their evidence became public. They were excluded rather than assumed sound, so every " +
                "rate below is over a smaller sample than the repository holds."));
        }

        var benchmarkPrices = await _history
            .GetPriceSeriesAsync(
                request.Benchmark.Subject,
                request.Benchmark.PriceAttribute,
                request.Window.FromUtc,
                request.Window.ToUtc,
                observationCutoff,
                cancellationToken)
            .ConfigureAwait(false);

        if (benchmarkPrices.Count < 2)
        {
            gaps.Add(new DataGap(
                "benchmark",
                $"the repository holds {benchmarkPrices.Count} admissible price(s) for " +
                $"{request.Benchmark.Subject} on '{request.Benchmark.PriceAttribute}' in this window, " +
                "and buy-and-hold needs one at each end. The comparison could not be made."));
        }

        var unreadable = await _history
            .CountUnreadableAsync(request.Benchmark.Subject, request.Benchmark.PriceAttribute, cancellationToken)
            .ConfigureAwait(false);

        if (unreadable > 0)
        {
            gaps.Add(new DataGap(
                "price data quality",
                $"{unreadable} stored values for '{request.Benchmark.PriceAttribute}' could not be read " +
                "as numbers and were omitted rather than coerced."));
        }

        var shadowDecisions = await _shadow
            .GetBetweenAsync(request.Window.FromUtc, request.Window.ToUtc, cancellationToken)
            .ConfigureAwait(false);

        if (shadowDecisions.Count == 0)
        {
            gaps.Add(new DataGap(
                "shadow versus actual",
                "no shadow measurements were recorded in this window, so there is nothing to compare " +
                "against what the platform actually decided."));
        }

        var sources = await _history
            .GetSourceIdsAsync(request.Window.FromUtc, request.Window.ToUtc, cancellationToken)
            .ConfigureAwait(false);

        if (sources.Count == 0)
        {
            gaps.Add(new DataGap(
                "evidence",
                "no observations from any registered source fall in this window."));
        }

        return ValidationReport.Create(
            runId,
            startedAt,
            request.Window,
            request.EventThreshold,
            request.Methodology,
            request.Benchmark,
            sources,
            replay.Considered,
            replay.Admitted.Count,
            replay.Refused.Count,
            ConfusionMatrix.From(labels),
            CalibrationCurve.From(calibrationSamples, withoutProbability),
            PerformanceCalculator.MeanRoundTripReturn(roundTrips),
            PerformanceCalculator.BuyAndHold(benchmarkPrices, request.Benchmark.CostPerTrade),
            ShadowComparisonResult.From(shadowDecisions, labelsByProposal),
            gaps,
            Limitations(replay, shadowDecisions.Count));
    }

    /// <summary>
    /// Prices the horizon. Entry is priced with what was knowable at the decision; exit with what is
    /// knowable now.
    /// </summary>
    /// <remarks>
    /// The asymmetry is deliberate and is the correct one. A position is entered at a price the
    /// decision could see, so the entry is fetched under the decision's own cutoff; the outcome is
    /// whatever later became true, so the exit is fetched under the present cutoff. Fetching the entry
    /// under the present cutoff would let a price that was published late stand in for one the decision
    /// never had, which is look-ahead bias arriving through the pricing rather than through the
    /// evidence.
    /// </remarks>
    private async Task<RealisedOutcome?> ResolveAsync(
        PredictionRecord prediction,
        ValidationRequest request,
        KnowledgeCutoff observationCutoff,
        CancellationToken cancellationToken)
    {
        var entry = await _history
            .GetPriceAsOfAsync(
                prediction.Subject,
                request.PriceAttribute,
                prediction.DecidedAtUtc,
                prediction.Cutoff,
                cancellationToken)
            .ConfigureAwait(false);

        if (entry is null || entry.Price <= 0m)
        {
            return null;
        }

        var exit = await _history
            .GetPriceAsOfAsync(
                prediction.Subject,
                request.PriceAttribute,
                prediction.ResolvesAtUtc,
                observationCutoff,
                cancellationToken)
            .ConfigureAwait(false);

        if (exit is null || exit.Price <= 0m || exit.AtUtc < prediction.ResolvesAtUtc)
        {
            return null;
        }

        return RealisedOutcome.Create(
            prediction.Subject,
            prediction.ResolvesAtUtc,
            exit.PublishedAtUtc > prediction.ResolvesAtUtc ? exit.PublishedAtUtc : prediction.ResolvesAtUtc,
            Percentage.FromRatio(PerformanceCalculator.RoundTripReturn(
                entry.Price,
                exit.Price,
                request.Benchmark.CostPerTrade)));
    }

    private static List<string> Limitations(BacktestResult replay, int shadowCount)
    {
        var limitations = new List<string>
        {
            "One window is not a track record. A result over a single period, however measured, says " +
            "nothing reliable about the next one.",

            "Returns are simple and equal-weighted across round trips rather than compounded or " +
            "position-sized, and the same convention is applied to both the system and the benchmark.",

            "Trading costs are modelled as a flat rate charged on both legs, identically to both sides. " +
            "Slippage, market impact, borrow costs and taxes are not modelled at all.",

            "Survivorship is not corrected for. If the subjects measured are the ones the repository " +
            "still holds, a subject that failed and was removed would not appear here.",
        };

        if (replay.Refused.Count > 0)
        {
            limitations.Add(
                $"{replay.Refused.Count} of {replay.Considered} predictions were refused by the " +
                "point-in-time guard. The rates describe what survived it, not what the system produced.");
        }

        if (shadowCount == 0)
        {
            limitations.Add(
                "There are no shadow measurements in this window, so nothing here bears on whether a " +
                "higher autonomy level would be justified. Autonomy remains L3 either way.");
        }

        return limitations;
    }
}
