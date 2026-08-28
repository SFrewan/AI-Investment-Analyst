using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Validation;

/// <summary>
/// The rule the whole phase rests on: what was knowable, and when.
/// </summary>
/// <remarks>
/// Look-ahead bias does not announce itself. It arrives as unusually good results, and by the time
/// anybody is suspicious the history has been stored without the distinction that would settle it.
/// These tests are therefore about the negative cases almost exclusively: what the guard refuses, and
/// what it refuses to decide.
/// </remarks>
public sealed class PointInTimeGuardTests
{
    private static readonly DateTime Decision = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly KnowledgeCutoff Cutoff = KnowledgeCutoff.At(Decision);

    private static readonly IngestionSubject Subject = IngestionSubject.Create("Security", "AAPL");

    private static Provenance Fact(DateTime asOf, DateTime published, DateTime? retrieved = null) =>
        Provenance.Create("sec-edgar", asOf, published, retrieved ?? published);

    [Fact]
    public void Evidence_published_before_the_decision_is_admissible()
    {
        var verdict = PointInTimeGuard.Judge(
            Fact(Decision.AddDays(-40), Decision.AddDays(-10)),
            ClaimKind.Fact,
            Cutoff);

        Assert.True(verdict.IsAdmissible);
        Assert.Equal(AdmissibilityRefusal.None, verdict.Refusal);
    }

    [Fact]
    public void Evidence_published_after_the_decision_is_refused()
    {
        var verdict = PointInTimeGuard.Judge(
            Fact(Decision.AddDays(-40), Decision.AddDays(1)),
            ClaimKind.Fact,
            Cutoff);

        Assert.False(verdict.IsAdmissible);
        Assert.Equal(AdmissibilityRefusal.PublishedAfterCutoff, verdict.Refusal);
        Assert.Contains("Nobody knew it yet", verdict.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Published exactly at the decision was knowable. The boundary is inclusive.</summary>
    [Fact]
    public void Evidence_published_at_the_instant_of_the_decision_is_admissible()
    {
        Assert.True(PointInTimeGuard
            .Judge(Fact(Decision.AddDays(-40), Decision), ClaimKind.Fact, Cutoff)
            .IsAdmissible);

        Assert.False(PointInTimeGuard
            .Judge(Fact(Decision.AddDays(-40), Decision.AddTicks(1)), ClaimKind.Fact, Cutoff)
            .IsAdmissible);
    }

    /// <summary>
    /// The claim this whole design turns on: admissibility does not depend on when we fetched it.
    /// </summary>
    /// <remarks>
    /// If it did, replaying a period after backfilling a source would produce a different answer for
    /// reasons that have nothing to do with the world. The test sweeps retrieval time across two
    /// years while holding publication fixed, and insists the verdict never moves.
    /// </remarks>
    [Fact]
    public void Retrieval_time_alone_never_changes_a_verdict()
    {
        var published = Decision.AddDays(-10);
        var asOf = Decision.AddDays(-40);

        var baseline = PointInTimeGuard.Judge(Fact(asOf, published, published), ClaimKind.Fact, Cutoff);

        for (var days = 0; days <= 730; days += 37)
        {
            var later = PointInTimeGuard.Judge(
                Fact(asOf, published, published.AddDays(days)),
                ClaimKind.Fact,
                Cutoff);

            Assert.Equal(baseline.IsAdmissible, later.IsAdmissible);
            Assert.Equal(baseline.Refusal, later.Refusal);
        }
    }

    /// <summary>
    /// A value fetched before it was published places itself nowhere. The guard refuses to decide.
    /// </summary>
    [Fact]
    public void A_value_retrieved_before_it_was_published_cannot_be_judged()
    {
        var verdict = PointInTimeGuard.Judge(
            Fact(Decision.AddDays(-40), Decision.AddDays(-10), Decision.AddDays(-20)),
            ClaimKind.Fact,
            Cutoff);

        Assert.True(verdict.IsUndeterminable);
        Assert.False(verdict.IsAdmissible);
        Assert.Equal(AdmissibilityRefusal.ImpossibleOrdering, verdict.Refusal);
    }

    /// <summary>
    /// A "fact" about a period that had not finished is a forecast, and is refused as one.
    /// </summary>
    [Fact]
    public void A_fact_describing_a_period_after_the_decision_is_refused()
    {
        var verdict = PointInTimeGuard.Judge(
            Fact(Decision.AddDays(30), Decision.AddDays(-1)),
            ClaimKind.Fact,
            Cutoff);

        Assert.False(verdict.IsAdmissible);
        Assert.Equal(AdmissibilityRefusal.DescribesPeriodAfterCutoff, verdict.Refusal);
    }

    /// <summary>
    /// A prediction legitimately describes the future, so the period rule applies to facts only.
    /// </summary>
    [Fact]
    public void A_prediction_about_the_future_published_in_time_is_admissible()
    {
        var verdict = PointInTimeGuard.Judge(
            Fact(Decision.AddDays(30), Decision.AddDays(-1)),
            ClaimKind.Prediction,
            Cutoff);

        Assert.True(verdict.IsAdmissible);
    }

    [Fact]
    public void Missing_provenance_is_undeterminable_rather_than_admissible()
    {
        var verdict = PointInTimeGuard.Judge((Provenance?)null, ClaimKind.Fact, Cutoff);

        Assert.True(verdict.IsUndeterminable);
        Assert.False(verdict.IsAdmissible);
        Assert.Contains("may not guess", verdict.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_observation_is_judged_by_the_provenance_it_carries()
    {
        var observation = Observation.RecordFact(
            Subject,
            "security.close",
            ObservationValue.Number(191.25m),
            Fact(Decision.AddDays(-1), Decision.AddDays(-1)));

        Assert.True(PointInTimeGuard.Judge(observation, Cutoff).IsAdmissible);

        var late = Observation.RecordFact(
            Subject,
            "security.close",
            ObservationValue.Number(191.25m),
            Fact(Decision.AddDays(-1), Decision.AddDays(5)));

        Assert.Equal(AdmissibilityRefusal.PublishedAfterCutoff, PointInTimeGuard.Judge(late, Cutoff).Refusal);
        Assert.False(PointInTimeGuard.Judge((Observation?)null, Cutoff).IsAdmissible);
    }

    /// <summary>
    /// A calculated number launders its inputs. The guard judges what went in, not when the
    /// arithmetic ran.
    /// </summary>
    [Fact]
    public void A_calculation_is_admissible_only_if_every_input_behind_it_was()
    {
        var stale = Metric(
            inputPublishedAtUtc: Decision.AddDays(-5),
            calculatedWithCutoff: Decision.AddDays(-1));

        Assert.True(PointInTimeGuard.Judge(stale, Cutoff).IsAdmissible);

        var leaky = Metric(
            inputPublishedAtUtc: Decision.AddDays(1),
            calculatedWithCutoff: Decision.AddDays(2));

        var verdict = PointInTimeGuard.Judge(leaky, Cutoff);

        Assert.False(verdict.IsAdmissible);
        Assert.Equal(AdmissibilityRefusal.DerivedFromInadmissibleEvidence, verdict.Refusal);
        Assert.Contains("The arithmetic is not the leak", verdict.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value computed with a wider view of the world is not the value the decision had.
    /// </summary>
    [Fact]
    public void A_calculation_made_with_a_later_cutoff_is_refused()
    {
        var verdict = PointInTimeGuard.Judge(
            Metric(inputPublishedAtUtc: Decision.AddDays(-5), calculatedWithCutoff: Decision.AddDays(2)),
            Cutoff);

        Assert.False(verdict.IsAdmissible);
        Assert.Equal(AdmissibilityRefusal.CalculatedWithALaterCutoff, verdict.Refusal);
    }

    [Fact]
    public void A_calculation_with_no_recorded_inputs_cannot_be_judged()
    {
        Assert.True(PointInTimeGuard.Judge((MetricResult?)null, Cutoff).IsUndeterminable);
    }

    [Fact]
    public void The_guard_refuses_to_be_asked_without_a_cutoff()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PointInTimeGuard.Judge(Fact(Decision, Decision), ClaimKind.Fact, null!));
    }

    private static MetricResult Metric(DateTime inputPublishedAtUtc, DateTime calculatedWithCutoff)
    {
        var cutoff = KnowledgeCutoff.At(calculatedWithCutoff);

        return MetricResult.Create(
            CalculationContext.Create(Subject, cutoff, calculatedWithCutoff),
            MetricId.Create("valuation.pe"),
            MetricValue.Ratio(12.5m),
            "price / earnings",
            SourceId.Create("internal-analytics"),
            CalculationVersion.Create(1, 0),
            asOfUtc: Decision.AddDays(-40),
            [
                CalculationInput.Create(
                    "price",
                    Claims.Fact(12.5m, Fact(Decision.AddDays(-40), inputPublishedAtUtc)),
                    UnitOfMeasure.Ratio),
            ]);
    }
}
