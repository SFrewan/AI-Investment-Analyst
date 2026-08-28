using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Validation;

/// <summary>
/// The window a run measures, the predictions in it, and how they are labelled.
/// </summary>
public sealed class ValidationPredictionTests
{
    private static readonly DateTime Decision = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Subject = IngestionSubject.Create("Security", "AAPL");

    private static readonly CalculationVersion Method = CalculationVersion.Create(1, 0);

    private static PredictionRecord Prediction(
        PredictionDirection direction = PredictionDirection.Positive,
        DateTime? evidenceAt = null,
        decimal? probability = null) =>
        PredictionRecord.Create(
            Guid.NewGuid(),
            Subject,
            Decision,
            Decision.AddDays(30),
            direction,
            Method,
            evidenceAt ?? Decision.AddDays(-1),
            "opportunity/test",
            probability is null ? null : Percentage.FromRatio(probability.Value));

    // ---- the look-ahead guard, at the point a prediction is constructed ---------------------

    /// <summary>
    /// The constructor is the last line of defence, and it refuses rather than warns.
    /// </summary>
    [Fact]
    public void A_prediction_built_on_evidence_younger_than_itself_is_refused()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            Prediction(evidenceAt: Decision.AddSeconds(1)));

        Assert.Equal(PredictionRecord.LookaheadRule, error.Rule);
        Assert.Contains("measure hindsight", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_published_at_the_instant_of_the_decision_is_allowed()
    {
        var prediction = Prediction(evidenceAt: Decision);

        Assert.Equal(Decision, prediction.EvidenceAvailableAtUtc);
        Assert.Equal(Decision, prediction.Cutoff.AsOfUtc);
    }

    [Fact]
    public void A_prediction_that_resolves_before_it_was_made_is_refused()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            PredictionRecord.Create(
                Guid.NewGuid(),
                Subject,
                Decision,
                Decision,
                PredictionDirection.Positive,
                Method,
                Decision.AddDays(-1),
                "opportunity/test"));

        Assert.Equal(PredictionRecord.UnresolvableRule, error.Rule);
    }

    [Fact]
    public void A_prediction_must_be_identifiable_and_traceable()
    {
        Assert.Throws<DomainValidationException>(() =>
            PredictionRecord.Create(
                Guid.Empty,
                Subject,
                Decision,
                Decision.AddDays(1),
                PredictionDirection.Positive,
                Method,
                Decision,
                "opportunity/test"));

        Assert.Throws<ArgumentException>(() =>
            PredictionRecord.Create(
                Guid.NewGuid(),
                Subject,
                Decision,
                Decision.AddDays(1),
                PredictionDirection.Positive,
                Method,
                Decision,
                "   "));
    }

    [Theory]
    [InlineData(PredictionDirection.Positive, true)]
    [InlineData(PredictionDirection.Negative, true)]
    [InlineData(PredictionDirection.Abstain, false)]
    [InlineData(PredictionDirection.Unknown, false)]
    public void Only_a_positive_or_negative_prediction_is_a_call(PredictionDirection direction, bool isCall) =>
        Assert.Equal(isCall, Prediction(direction).IsCall);

    // ---- outcomes ---------------------------------------------------------------------------

    [Fact]
    public void An_outcome_may_not_be_measured_before_its_horizon()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            RealisedOutcome.Create(Subject, Decision.AddDays(30), Decision.AddDays(20), Percentage.FromRatio(0.1m)));

        Assert.Equal(RealisedOutcome.PrematureRule, error.Rule);
    }

    [Theory]
    [InlineData(PredictionDirection.Positive, 0.05, OutcomeLabel.TruePositive)]
    [InlineData(PredictionDirection.Positive, -0.05, OutcomeLabel.FalsePositive)]
    [InlineData(PredictionDirection.Negative, -0.05, OutcomeLabel.TrueNegative)]
    [InlineData(PredictionDirection.Negative, 0.05, OutcomeLabel.FalseNegative)]
    public void The_four_cells_are_the_four_combinations(
        PredictionDirection direction,
        decimal realised,
        OutcomeLabel expected)
    {
        var prediction = Prediction(direction);

        var outcome = RealisedOutcome.Create(
            Subject,
            prediction.ResolvesAtUtc,
            prediction.ResolvesAtUtc,
            Percentage.FromRatio(realised));

        var label = OutcomeLabeller.Label(
            prediction,
            outcome,
            Percentage.Zero,
            prediction.ResolvesAtUtc.AddDays(1));

        Assert.Equal(expected, label);
    }

    /// <summary>The threshold is inclusive, and the boundary is asserted rather than assumed.</summary>
    [Fact]
    public void A_realised_move_exactly_at_the_threshold_counts_as_the_event_happening()
    {
        var prediction = Prediction();
        var threshold = Percentage.FromRatio(0.02m);

        Assert.Equal(
            OutcomeLabel.TruePositive,
            Label(prediction, 0.02m, threshold));

        Assert.Equal(
            OutcomeLabel.FalsePositive,
            Label(prediction, 0.0199m, threshold));
    }

    [Fact]
    public void A_prediction_whose_horizon_has_not_elapsed_is_unresolved_rather_than_wrong()
    {
        var prediction = Prediction();

        Assert.Equal(
            OutcomeLabel.Unresolved,
            OutcomeLabeller.Label(prediction, null, Percentage.Zero, prediction.ResolvesAtUtc.AddTicks(-1)));
    }

    [Fact]
    public void A_prediction_with_no_outcome_data_is_unavailable_rather_than_wrong()
    {
        var prediction = Prediction();

        Assert.Equal(
            OutcomeLabel.Unavailable,
            OutcomeLabeller.Label(prediction, null, Percentage.Zero, prediction.ResolvesAtUtc.AddDays(1)));
    }

    /// <summary>
    /// Judging a thirty-day prediction on a ten-day outcome answers a different question, so the
    /// labeller refuses rather than accepting whatever outcome was to hand.
    /// </summary>
    [Fact]
    public void An_outcome_measured_over_a_shorter_horizon_does_not_count()
    {
        var prediction = Prediction();

        var shortHorizon = RealisedOutcome.Create(
            Subject,
            prediction.DecidedAtUtc.AddDays(10),
            prediction.DecidedAtUtc.AddDays(10),
            Percentage.FromRatio(0.5m));

        Assert.Equal(
            OutcomeLabel.Unavailable,
            OutcomeLabeller.Label(prediction, shortHorizon, Percentage.Zero, prediction.ResolvesAtUtc.AddDays(1)));
    }

    [Fact]
    public void An_abstention_is_labelled_as_one_rather_than_scored()
    {
        Assert.Equal(
            OutcomeLabel.Abstained,
            OutcomeLabeller.Label(
                Prediction(PredictionDirection.Abstain),
                null,
                Percentage.Zero,
                Decision.AddDays(60)));

        Assert.Equal(
            OutcomeLabel.Unknown,
            OutcomeLabeller.Label(
                Prediction(PredictionDirection.Unknown),
                null,
                Percentage.Zero,
                Decision.AddDays(60)));
    }

    // ---- the window -------------------------------------------------------------------------

    [Fact]
    public void A_window_walks_its_decision_times_deterministically()
    {
        var window = EvaluationWindow.Create(Decision, Decision.AddDays(10), TimeSpan.FromDays(3), TimeSpan.FromDays(2));

        var first = window.DecisionTimes();
        var second = window.DecisionTimes();

        Assert.Equal(6, first.Count);
        Assert.Equal(first.Select(c => c.AsOfUtc), second.Select(c => c.AsOfUtc));
        Assert.Equal(Decision, first[0].AsOfUtc);
        Assert.Equal(Decision.AddDays(10), first[^1].AsOfUtc);
    }

    [Fact]
    public void A_window_knows_which_decisions_have_had_time_to_resolve()
    {
        var window = EvaluationWindow.Create(Decision, Decision.AddDays(10), TimeSpan.FromDays(3), TimeSpan.FromDays(1));

        Assert.True(window.Resolves(Decision.AddDays(7)));
        Assert.False(window.Resolves(Decision.AddDays(8)));
        Assert.True(window.Contains(Decision.AddDays(10)));
        Assert.False(window.Contains(Decision.AddDays(11)));
    }

    [Fact]
    public void A_window_that_could_never_resolve_a_prediction_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            EvaluationWindow.Create(Decision, Decision.AddDays(10), TimeSpan.FromDays(10), TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() =>
            EvaluationWindow.Create(Decision, Decision.AddHours(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));

        Assert.Throws<DomainValidationException>(() =>
            EvaluationWindow.Create(Decision, Decision.AddDays(10), TimeSpan.Zero, TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() =>
            EvaluationWindow.Create(Decision, Decision.AddDays(10), TimeSpan.FromDays(1), TimeSpan.Zero));
    }

    private static OutcomeLabel Label(PredictionRecord prediction, decimal realised, Percentage threshold) =>
        OutcomeLabeller.Label(
            prediction,
            RealisedOutcome.Create(
                Subject,
                prediction.ResolvesAtUtc,
                prediction.ResolvesAtUtc,
                Percentage.FromRatio(realised)),
            threshold,
            prediction.ResolvesAtUtc.AddDays(1));
}
