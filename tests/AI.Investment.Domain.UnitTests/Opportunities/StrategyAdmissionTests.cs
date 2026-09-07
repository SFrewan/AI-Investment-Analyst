using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The gate that decides whether a strategy's numbers mean anything.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is a way a strategy can look measured without being measured, and each one was
/// chosen because it is invisible in the arithmetic. A Brier score of 0.08 is indistinguishable from
/// a good one whether it came from thirty resolved predictions or three thousand, whether the event
/// was declared before or after the outcomes were visible, and whether the parameters were fitted to
/// the very data being scored. The number is the same; only the record around it differs.
/// </para>
/// <para>
/// The criteria are shrunk to test sizes. The shipped bar needs a hundred resolved predictions and
/// building a hundred of them per assertion would bury the assertion.
/// </para>
/// </remarks>
public sealed class StrategyAdmissionTests
{
    private static readonly DateTime Declared = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstPrediction = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Measured = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

    private const string Evidence = "evidence-base-a";
    private const string OtherEvidence = "evidence-base-b";

    private static readonly OpportunityType Type = OpportunityType.Create("equity-price-recovery");

    /// <summary>Twenty resolved predictions, a Brier ceiling of 0.20, three variants, ninety days.</summary>
    private static readonly AdmissionCriteria Small = AdmissionCriteria.Create(
        minimumResolvedPredictions: 20,
        maximumBrierScore: 0.20m,
        maximumTrialsPerSearchFamily: 3,
        maximumEvidenceAge: TimeSpan.FromDays(90));

    // ---- the case that passes -------------------------------------------------------------------

    /// <summary>
    /// A strategy declared before its predictions, tuned elsewhere, well sampled and well calibrated.
    /// </summary>
    [Fact]
    public void A_strategy_with_a_complete_record_is_admitted()
    {
        var admission = StrategyAdmission.Evaluate(Declaration(), Scored(), Small, Now);

        Assert.True(admission.IsAdmitted);
        Assert.Empty(admission.Refusals);
        Assert.Equal(Type, admission.Type);
        Assert.Equal(Declaration().Fingerprint, admission.EventFingerprint);
    }

    // ---- the refusals ---------------------------------------------------------------------------

    /// <summary>
    /// <strong>The default.</strong> A strategy nobody has measured is refused, not admitted quietly.
    /// </summary>
    [Fact]
    public void A_strategy_that_has_never_been_measured_is_refused()
    {
        var admission = StrategyAdmission.Evaluate(Declaration(), measurement: null, Small, Now);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(AdmissionRefusal.NeverMeasured, Assert.Single(admission.Refusals));
    }

    /// <summary>
    /// Look-ahead one level up: each prediction is well formed and the event was chosen afterwards.
    /// </summary>
    /// <remarks>
    /// <see cref="PredictionRecord"/> already refuses a prediction resting on evidence that was not
    /// yet public, and every prediction in this set would pass that check. What it cannot see is that
    /// the event definition itself was written once the outcomes were visible, which is the cheapest
    /// possible way to produce a strategy that has never been wrong.
    /// </remarks>
    [Fact]
    public void An_event_declared_after_the_predictions_it_scores_is_refused()
    {
        var late = StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0m),
            horizonSessions: 21,
            Evidence,
            FirstPrediction.AddDays(1),
            OtherEvidence);

        var admission = StrategyAdmission.Evaluate(late, Scored(), Small, Now);

        Assert.Contains(AdmissionRefusal.EventDeclaredAfterTheEvidence, admission.Refusals);
    }

    /// <summary>The same rule as a domain invariant, so the type refuses it too.</summary>
    [Fact]
    public void The_declaration_itself_refuses_an_evidence_set_that_predates_it() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            Declaration().EnsureDeclaredBefore(Declared.AddDays(-1)));

    /// <summary>
    /// <strong>In-sample scoring.</strong> Fitted to the data it is scored on, and refused for it.
    /// </summary>
    /// <remarks>
    /// No arithmetic can detect this. The Brier score of a strategy tuned on the data it is scored
    /// on is a description of that data, and it is generally excellent. So the tuning basis is
    /// declared and the two strings are compared, which is the only check that can be made.
    /// </remarks>
    [Fact]
    public void A_strategy_tuned_on_the_evidence_it_is_scored_on_is_refused()
    {
        var inSample = StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0m),
            horizonSessions: 21,
            Evidence,
            Declared,
            tuningBasisFingerprint: Evidence);

        var admission = StrategyAdmission.Evaluate(inSample, Scored(), Small, Now);

        Assert.True(inSample.WasTunedOnTheEvidence);
        Assert.Contains(AdmissionRefusal.TunedOnTheEvidenceItIsScoredOn, admission.Refusals);
    }

    /// <summary>An excellent score over too small a sample is still refused.</summary>
    [Fact]
    public void A_strategy_with_too_few_resolved_predictions_is_refused()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(resolved: 19, brier: 0.01m),
            Small,
            Now);

        Assert.Contains(AdmissionRefusal.NotEnoughResolvedPredictions, admission.Refusals);
    }

    /// <summary>Predictions that were made but have not resolved do not count towards the sample.</summary>
    [Fact]
    public void An_unmeasurable_score_is_not_a_passing_score()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(brier: null),
            Small,
            Now);

        Assert.Contains(AdmissionRefusal.CalibrationNotEstablished, admission.Refusals);
    }

    [Theory]
    [InlineData(0.2001)]
    [InlineData(0.2500)]
    [InlineData(0.4088)]
    public void A_score_worse_than_the_ceiling_is_refused(double brier)
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(brier: (decimal)brier),
            Small,
            Now);

        Assert.Contains(AdmissionRefusal.PoorlyCalibrated, admission.Refusals);
    }

    /// <summary>The boundary is inclusive: a score exactly at the ceiling clears it.</summary>
    [Fact]
    public void A_score_exactly_at_the_ceiling_is_admitted()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(brier: 0.20m),
            Small,
            Now);

        Assert.True(admission.IsAdmitted);
    }

    /// <summary>
    /// <strong>Multiple comparisons.</strong> The sixth variant tried on one dataset is refused.
    /// </summary>
    /// <remarks>
    /// Keeping the best of many variants measured against one year of one dataset is a search, and
    /// the winner of a search is expected to look good by chance. A single Brier score cannot see how
    /// many attempts preceded it, so the count is carried and refused past a budget.
    /// </remarks>
    [Fact]
    public void A_strategy_past_the_trial_budget_for_one_evidence_base_is_refused()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(trials: 4),
            Small,
            Now);

        Assert.Contains(AdmissionRefusal.TooManyTrialsAgainstOneEvidenceBase, admission.Refusals);
    }

    /// <summary>The budget is inclusive, so the last variant inside it is still admitted.</summary>
    [Fact]
    public void The_last_variant_inside_the_trial_budget_is_admitted()
    {
        var admission = StrategyAdmission.Evaluate(Declaration(), Scored(trials: 3), Small, Now);

        Assert.True(admission.IsAdmitted);
    }

    /// <summary>
    /// The probability the strategy states and the event the labeller scored must be one event.
    /// </summary>
    /// <remarks>
    /// This is the failure that produced a Brier score of 0.5538 where the strategy's own event
    /// scored 0.11. It was a wiring accident then; here it is a refusal, so a strategy cannot be
    /// admitted on a score of a different question.
    /// </remarks>
    [Fact]
    public void A_strategy_scored_against_a_different_event_is_refused()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(labelled: 0.02m),
            Small,
            Now);

        Assert.Contains(AdmissionRefusal.EventMismatch, admission.Refusals);
    }

    [Fact]
    public void A_measurement_of_another_strategy_admits_nothing()
    {
        var other = StrategyMeasurement.Create(
            OpportunityType.Create("equity-fundamental-value"),
            resolvedPredictions: 500,
            Measurement.Measured(0.01m, 500, "excellent"),
            Percentage.FromRatio(0m),
            FirstPrediction,
            Measured,
            Evidence);

        var admission = StrategyAdmission.Evaluate(Declaration(), other, Small, Now);

        Assert.False(admission.IsAdmitted);
        Assert.Equal(
            AdmissionRefusal.MeasurementIsOfAnotherStrategy,
            Assert.Single(admission.Refusals));
    }

    [Fact]
    public void A_measurement_older_than_the_permitted_age_is_refused()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(),
            Small,
            Measured.AddDays(91));

        Assert.Contains(AdmissionRefusal.EvidenceStale, admission.Refusals);
    }

    /// <summary>Every refusal carries a reason, or the audit trail records a verdict and no argument.</summary>
    [Fact]
    public void Every_refusal_is_accompanied_by_a_reason()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(),
            Scored(resolved: 1, brier: 0.9m, trials: 9),
            Small,
            Measured.AddDays(400));

        Assert.True(admission.Refusals.Count > 1);
        Assert.Equal(admission.Refusals.Count, admission.Reasons.Count);
        Assert.All(admission.Reasons, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    // ---- the declaration itself -----------------------------------------------------------------

    /// <summary>A changed claim is a different claim, and the fingerprint says so.</summary>
    [Fact]
    public void An_edited_declaration_has_a_different_fingerprint()
    {
        var original = Declaration();

        var edited = StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0.05m),
            horizonSessions: 21,
            Evidence,
            Declared,
            OtherEvidence);

        Assert.NotEqual(original.Fingerprint, edited.Fingerprint);
        Assert.Equal(original.Fingerprint, Declaration().Fingerprint);
    }

    [Theory]
    [InlineData(PredictionDirection.Abstain)]
    [InlineData(PredictionDirection.Unknown)]
    public void A_declaration_that_makes_no_claim_is_refused(PredictionDirection direction) =>
        Assert.Throws<DomainValidationException>(() =>
            StrategyEvent.Declare(
                Type,
                direction,
                Percentage.FromRatio(0m),
                horizonSessions: 21,
                Evidence,
                Declared));

    [Fact]
    public void A_declaration_with_no_horizon_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            StrategyEvent.Declare(
                Type,
                PredictionDirection.Positive,
                Percentage.FromRatio(0m),
                horizonSessions: 0,
                Evidence,
                Declared));

    [Fact]
    public void Criteria_that_would_admit_anything_are_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            AdmissionCriteria.Create(0, 0.20m, 3, TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionCriteria.Create(20, 1.5m, 3, TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionCriteria.Create(20, 0.20m, 0, TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionCriteria.Create(20, 0.20m, 3, TimeSpan.Zero));
    }

    /// <summary>A run dated before the predictions it scores is a defect, not a result.</summary>
    [Fact]
    public void A_measurement_taken_before_the_predictions_it_scores_is_refused() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            StrategyMeasurement.Create(
                Type,
                resolvedPredictions: 100,
                Measurement.Measured(0.1m, 100, "fine"),
                Percentage.FromRatio(0m),
                FirstPrediction,
                FirstPrediction.AddDays(-1),
                Evidence));

    /// <summary>The shipped bar is itself valid, and is the promotion bar's sample and ceiling.</summary>
    [Fact]
    public void The_shipped_bar_is_valid_and_is_not_a_coin_flip()
    {
        Assert.Equal(100, AdmissionCriteria.Standard.MinimumResolvedPredictions);
        Assert.Equal(0.20m, AdmissionCriteria.Standard.MaximumBrierScore);
        Assert.True(AdmissionCriteria.Standard.MaximumBrierScore < 0.25m);
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static StrategyEvent Declaration() =>
        StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0m),
            horizonSessions: 21,
            Evidence,
            Declared,
            tuningBasisFingerprint: OtherEvidence);

    private static StrategyMeasurement Scored(
        int resolved = 100,
        decimal? brier = 0.10m,
        decimal labelled = 0m,
        int trials = 1) =>
        StrategyMeasurement.Create(
            Type,
            resolved,
            brier is null
                ? Measurement.Insufficient(3, 20)
                : Measurement.Measured(brier.Value, Math.Max(resolved, 1), "measured"),
            Percentage.FromRatio(labelled),
            FirstPrediction,
            Measured,
            Evidence,
            trials);
}
