using AI.Investment.Application.UnitTests.Operations;
using AI.Investment.Application.Validation;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Validation;

/// <summary>
/// The whole measurement, end to end, over a history a test can see all of.
/// </summary>
/// <remarks>
/// <para>
/// The interesting cases are the ones where the honest answer is unflattering or absent: an empty
/// repository, a price that was restated after the decision, a price that was not public yet, a
/// strategy that loses to the index. A validation harness that only has a happy path is a harness
/// that will report a happy path.
/// </para>
/// </remarks>
public sealed class ValidationServiceTests
{
    private static readonly DateTime Now = ValidationFixtures.WindowEnd.AddDays(60);

    private readonly FakeValidationHistory _history = new();
    private readonly FakePredictionCatalogue _catalogue = new();
    private readonly FakeShadowStore _shadow = new();
    private readonly FakeClock _clock = new(Now);

    private ValidationService Service() =>
        new(_history, _catalogue, _shadow, _clock);

    private static ValidationRequest Request(decimal cost = 0m, DateTime? benchmarkDeclaredAt = null) =>
        new(
            ValidationFixtures.Window(),
            Percentage.Zero,
            ValidationFixtures.Method,
            ValidationFixtures.Benchmark(cost, benchmarkDeclaredAt),
            ValidationFixtures.PriceAttribute);

    // ---- the empty repository, which is the honest starting position ------------------------

    /// <summary>
    /// Nothing measured is reported as nothing measured, not as a zero hit rate.
    /// </summary>
    [Fact]
    public async Task An_empty_repository_produces_a_report_that_says_nothing_was_measured()
    {
        var report = await Service().RunAsync(Request());

        Assert.True(report.IsEmpty);
        Assert.Equal(0, report.PredictionsConsidered);
        Assert.Equal(ValidationVerdict.NotEstablished, report.Verdict);
        Assert.Contains("untested hypothesis", report.Conclusion, StringComparison.Ordinal);

        Assert.False(report.Matrix.HitRate.IsMeasured);
        Assert.False(report.SystemReturn.IsMeasured);
        Assert.False(report.BenchmarkReturn.IsMeasured);
        Assert.False(report.ExcessReturn.IsMeasured);

        Assert.Contains(report.DataGaps, gap => gap.Metric == "benchmark");
        Assert.Contains(report.DataGaps, gap => gap.Metric == "shadow versus actual");
        Assert.Contains(report.DataGaps, gap => gap.Metric == "evidence");
    }

    /// <summary>A benchmark fixed after the run began makes the run fail, not improve.</summary>
    [Fact]
    public async Task A_benchmark_declared_after_the_run_began_stops_the_run()
    {
        var error = await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            Service().RunAsync(Request(benchmarkDeclaredAt: Now.AddDays(1))));

        Assert.Equal(BenchmarkDefinition.DeclaredAfterTheRunRule, error.Rule);
    }

    // ---- the point-in-time rule, inside the service ------------------------------------------

    /// <summary>
    /// Bitemporal replay: a price corrected later must not reach a decision that came before the
    /// correction.
    /// </summary>
    [Fact]
    public async Task A_price_restated_after_the_decision_is_not_the_price_the_decision_had()
    {
        var subject = IngestionSubject.Create("Security", "RESTATED");
        var decided = ValidationFixtures.WindowStart.AddDays(30);
        var resolves = decided.AddDays(30);

        // What was published at the time.
        _history.Add(subject, ValidationFixtures.PriceAttribute, decided, 100m, decided);

        // The correction, published forty days later. Same instant, different value.
        _history.Add(subject, ValidationFixtures.PriceAttribute, decided, 200m, decided.AddDays(40));

        _history.Add(subject, ValidationFixtures.PriceAttribute, resolves, 110m, resolves);

        _catalogue.Add(ValidationFixtures.Candidate(decided, decided.AddDays(-1), subject: subject));

        var report = await Service().RunAsync(Request());

        // Entered at 100 and exited at 110 is a ten per cent gain, and a true positive. Entered at
        // the restated 200 it would have been a loss, and the cell would read false positive.
        Assert.Equal(1, report.Matrix.TruePositives);
        Assert.Equal(0, report.Matrix.FalsePositives);
    }

    /// <summary>
    /// A price that had not been published at the decision is not available to it, so the prediction
    /// cannot be priced and is reported unavailable rather than priced from the future.
    /// </summary>
    [Fact]
    public async Task A_price_not_yet_public_at_the_decision_cannot_be_used_to_enter()
    {
        var subject = IngestionSubject.Create("Security", "LATE");
        var decided = ValidationFixtures.WindowStart.AddDays(30);
        var resolves = decided.AddDays(30);

        _history.Add(subject, ValidationFixtures.PriceAttribute, decided, 100m, decided.AddDays(10));
        _history.Add(subject, ValidationFixtures.PriceAttribute, resolves, 110m, resolves);

        _catalogue.Add(ValidationFixtures.Candidate(decided, decided.AddDays(-1), subject: subject));

        var report = await Service().RunAsync(Request());

        Assert.Equal(1, report.PredictionsAdmitted);
        Assert.Equal(0, report.Matrix.Scored);
        Assert.Equal(1, report.Matrix.Unavailable);
        Assert.False(report.SystemReturn.IsMeasured);
    }

    /// <summary>
    /// A prediction with no admissibility evidence is refused, counted, and reported as a gap.
    /// </summary>
    [Fact]
    public async Task Predictions_whose_history_cannot_be_established_are_refused_and_reported()
    {
        var decided = ValidationFixtures.WindowStart.AddDays(30);

        _catalogue.Add(ValidationFixtures.Candidate(decided, evidenceAvailableAtUtc: null));

        var report = await Service().RunAsync(Request());

        Assert.Equal(1, report.PredictionsConsidered);
        Assert.Equal(0, report.PredictionsAdmitted);
        Assert.Equal(1, report.PredictionsRefused);
        Assert.Contains(report.DataGaps, gap => gap.Metric == "point-in-time admissibility");
        Assert.Contains(report.Limitations, limit => limit.Contains("refused by the", StringComparison.Ordinal));
    }

    // ---- a full measurement, whose honest answer is that the system lost ---------------------

    /// <summary>
    /// Sixty predictions with known outcomes, an index that rose ten per cent, and a system that did
    /// not beat it. Every number below is arithmetic over a history stated in the arrangement.
    /// </summary>
    [Fact]
    public async Task A_full_run_measures_every_rate_and_reports_losing_to_the_benchmark()
    {
        Arrange();

        var report = await Service().RunAsync(Request());

        Assert.Equal(60, report.PredictionsConsidered);
        Assert.Equal(60, report.PredictionsAdmitted);
        Assert.Equal(0, report.PredictionsRefused);

        // 40 calls to act: 30 right, 10 wrong. 20 calls not to: 15 right, 5 wrong.
        Assert.Equal(30, report.Matrix.TruePositives);
        Assert.Equal(10, report.Matrix.FalsePositives);
        Assert.Equal(15, report.Matrix.TrueNegatives);
        Assert.Equal(5, report.Matrix.FalseNegatives);

        Assert.Equal(0.75m, report.Matrix.HitRate.Value);
        Assert.Equal(0.25m, report.Matrix.FalsePositiveRate.Value);
        Assert.Equal(5m / 35m, report.Matrix.FalseNegativeRate.Value);
        Assert.Equal(30m / 35m, report.Matrix.Recall.Value);

        // Positions are taken on the calls to act only. Thirty gains of ten per cent and ten losses
        // of ten per cent, equal-weighted, is five per cent.
        Assert.Equal(0.05m, report.SystemReturn.Value);
        Assert.Equal(40, report.SystemReturn.SampleSize);

        // The index went from 400 to 440.
        Assert.Equal(0.10m, report.BenchmarkReturn.Value);

        Assert.Equal(-0.05m, report.ExcessReturn.Value);
        Assert.Equal(ValidationVerdict.WorseThanBenchmark, report.Verdict);
        Assert.Contains("did not pay for itself", report.Conclusion, StringComparison.Ordinal);
    }

    /// <summary>
    /// The calibration curve over the same run: both bands are perfectly calibrated even though the
    /// strategy lost money, which is exactly why the two are reported separately.
    /// </summary>
    [Fact]
    public async Task Calibration_is_measured_independently_of_whether_the_system_made_money()
    {
        Arrange();

        var report = await Service().RunAsync(Request());

        var confident = report.Calibration.Bins.Single(bin => bin.LowerRatio == 0.7m);
        var doubtful = report.Calibration.Bins.Single(bin => bin.LowerRatio == 0.2m);

        Assert.Equal(40, confident.Count);
        Assert.Equal(0.75m, confident.MeanStated.Value);
        Assert.Equal(0.75m, confident.ObservedFrequency.Value);
        Assert.Equal(0m, confident.Gap.Value);

        Assert.Equal(20, doubtful.Count);
        Assert.Equal(0.25m, doubtful.ObservedFrequency.Value);

        Assert.Equal(0.1875m, report.Calibration.BrierScore.Value);
        Assert.Equal(60, report.Calibration.ResolvedCount);
    }

    /// <summary>
    /// Two runs over the same history produce the same numbers. Only the run identity and the moment
    /// of generation differ, which is what makes a later difference mean the history changed.
    /// </summary>
    [Fact]
    public async Task The_same_history_measures_identically_twice()
    {
        Arrange();

        var service = Service();
        var first = await service.RunAsync(Request());
        var second = await service.RunAsync(Request());

        Assert.NotEqual(first.RunId, second.RunId);

        Assert.Equal(first.Matrix.ToString(), second.Matrix.ToString());
        Assert.Equal(first.SystemReturn.Value, second.SystemReturn.Value);
        Assert.Equal(first.BenchmarkReturn.Value, second.BenchmarkReturn.Value);
        Assert.Equal(first.ExcessReturn.Value, second.ExcessReturn.Value);
        Assert.Equal(first.Calibration.BrierScore.Value, second.Calibration.BrierScore.Value);
        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.Benchmark.Fingerprint, second.Benchmark.Fingerprint);
    }

    /// <summary>Too few predictions withholds the rates instead of reporting a precise-looking one.</summary>
    [Fact]
    public async Task A_handful_of_predictions_withholds_the_rates()
    {
        Arrange(positives: 3, negatives: 0);

        var report = await Service().RunAsync(Request());

        Assert.Equal(3, report.Matrix.Scored);
        Assert.Equal(MetricAvailability.Insufficient, report.Matrix.HitRate.Availability);
        Assert.Null(report.Matrix.HitRate.Value);
        Assert.Equal(MetricAvailability.Insufficient, report.SystemReturn.Availability);
        Assert.Equal(ValidationVerdict.NotEstablished, report.Verdict);
    }

    // ---- shadow versus actual ------------------------------------------------------------------

    /// <summary>
    /// Shadow measurements are matched to the predictions they were about, and the extra actions a
    /// higher level would have taken are judged only where their outcomes are known.
    /// </summary>
    [Fact]
    public async Task Shadow_decisions_are_matched_to_the_outcomes_of_their_own_proposals()
    {
        Arrange();

        var proposalIds = _seededProposalIds.Take(40).ToList();

        foreach (var proposalId in proposalIds)
        {
            _shadow.Seed(ShadowDecision.Record(
                Guid.NewGuid(),
                proposalId,
                Capability.SimulatedExecution,
                "execution.simulated-order",
                RiskTier.Medium,
                Money.Create(1_000m, Currency.Usd),
                AutonomyMode.PrepareForApproval,
                PolicyOutcome.RequireApproval,
                AutonomyMode.AutoExecuteBounded,
                PolicyOutcome.Execute,
                "measurement",
                ValidationFixtures.WindowStart.AddDays(10)));
        }

        var report = await Service().RunAsync(Request());

        Assert.Equal(40, report.Shadow.Total);
        Assert.Equal(40, report.Shadow.DivergenceCount);
        Assert.Equal(40, report.Shadow.ShadowWouldHaveExecutedAndActualDidNot);

        // Those forty proposals are the forty calls to act, of which thirty turned out right.
        Assert.True(report.Shadow.DivergenceHitRate.IsMeasured);
        Assert.Equal(0.75m, report.Shadow.DivergenceHitRate.Value);
    }

    [Fact]
    public async Task No_shadow_measurements_is_reported_as_a_gap_rather_than_as_agreement()
    {
        Arrange();

        var report = await Service().RunAsync(Request());

        Assert.Equal(0, report.Shadow.Total);
        Assert.False(report.Shadow.AgreementRate.IsMeasured);
        Assert.Contains(report.DataGaps, gap => gap.Metric == "shadow versus actual");
        Assert.Contains(report.Limitations, limit => limit.Contains("Autonomy remains L3", StringComparison.Ordinal));
    }

    // ---- the arrangement ------------------------------------------------------------------------

    private readonly List<Guid> _seededProposalIds = [];

    /// <summary>
    /// Builds a history whose every outcome is decided here rather than by the code under test.
    /// </summary>
    /// <remarks>
    /// One subject per prediction, with exactly two prices: what it cost at the decision and what it
    /// was worth at the horizon. Sharing a subject would couple the predictions to each other through
    /// the price series and make the expected cells an exercise in bookkeeping rather than a statement
    /// of what should happen.
    /// </remarks>
    private void Arrange(int positives = 40, int negatives = 20)
    {
        _history.Add(
            ValidationFixtures.Index,
            ValidationFixtures.PriceAttribute,
            ValidationFixtures.WindowStart,
            400m,
            ValidationFixtures.WindowStart);

        _history.Add(
            ValidationFixtures.Index,
            ValidationFixtures.PriceAttribute,
            ValidationFixtures.WindowEnd,
            440m,
            ValidationFixtures.WindowEnd);

        for (var index = 0; index < positives; index++)
        {
            // Every fourth call to act goes the wrong way.
            Seed(index, PredictionDirection.Positive, 0.75m, rises: index % 4 != 0);
        }

        for (var index = 0; index < negatives; index++)
        {
            // Every fourth call not to act was a missed opportunity.
            Seed(positives + index, PredictionDirection.Negative, 0.25m, rises: index % 4 == 0);
        }
    }

    private void Seed(int index, PredictionDirection direction, decimal probability, bool rises)
    {
        var subject = IngestionSubject.Create("Security", $"TEST{index}");
        var decided = ValidationFixtures.WindowStart.AddDays(index);
        var resolves = decided.AddDays(30);
        var proposalId = Guid.NewGuid();

        _seededProposalIds.Add(proposalId);

        _history.Add(subject, ValidationFixtures.PriceAttribute, decided, 100m, decided);
        _history.Add(subject, ValidationFixtures.PriceAttribute, resolves, rises ? 110m : 90m, resolves);

        _catalogue.Add(ValidationFixtures.Candidate(
            decided,
            decided.AddDays(-1),
            direction,
            probability: probability,
            proposalId: proposalId,
            subject: subject));
    }
}
