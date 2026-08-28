using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Validation;
using AI.Investment.Domain.Validation;
using Xunit;

namespace AI.Investment.Application.UnitTests.Validation;

/// <summary>
/// The replay: what it admits, what it refuses, and that it does the same thing twice.
/// </summary>
public sealed class BacktestEngineTests
{
    private static readonly DateTime Decision = ValidationFixtures.WindowStart.AddDays(30);

    [Fact]
    public void A_prediction_whose_evidence_predates_it_is_admitted()
    {
        var result = BacktestEngine.Replay(
            ValidationFixtures.Window(),
            [ValidationFixtures.Candidate(Decision, Decision.AddDays(-1))]);

        Assert.Single(result.Admitted);
        Assert.Empty(result.Refused);
        Assert.Equal(1, result.Considered);
        Assert.False(result.HasUndeterminableHistory);
    }

    /// <summary>The refusal that matters most, and it is counted rather than thrown.</summary>
    [Fact]
    public void A_prediction_whose_evidence_postdates_it_is_refused_and_counted()
    {
        var result = BacktestEngine.Replay(
            ValidationFixtures.Window(),
            [ValidationFixtures.Candidate(Decision, Decision.AddSeconds(1))]);

        Assert.Empty(result.Admitted);
        var refusal = Assert.Single(result.Refused);

        Assert.Equal(AdmissibilityRefusal.DerivedFromInadmissibleEvidence, refusal.Refusal);
        Assert.False(refusal.WasUndeterminable);
        Assert.Contains("measure hindsight", refusal.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fail closed. A candidate whose history cannot be established is excluded, not assumed sound.
    /// </summary>
    [Fact]
    public void A_prediction_with_no_admissibility_evidence_is_refused_as_undeterminable()
    {
        var result = BacktestEngine.Replay(
            ValidationFixtures.Window(),
            [ValidationFixtures.Candidate(Decision, evidenceAvailableAtUtc: null)]);

        Assert.Empty(result.Admitted);
        Assert.True(result.HasUndeterminableHistory);

        var refusal = Assert.Single(result.Refused);

        Assert.Equal(AdmissibilityRefusal.ProvenanceMissing, refusal.Refusal);
        Assert.True(refusal.WasUndeterminable);
        Assert.Contains("may not assume", refusal.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prediction_outside_the_window_is_refused()
    {
        var outside = ValidationFixtures.WindowStart.AddDays(-10);

        var result = BacktestEngine.Replay(
            ValidationFixtures.Window(),
            [ValidationFixtures.Candidate(outside, outside.AddDays(-1))]);

        Assert.Empty(result.Admitted);
        Assert.Single(result.Refused);
    }

    [Fact]
    public void A_prediction_that_resolves_before_it_was_made_is_refused()
    {
        var result = BacktestEngine.Replay(
            ValidationFixtures.Window(),
            [ValidationFixtures.Candidate(Decision, Decision.AddDays(-1), horizon: TimeSpan.Zero)]);

        Assert.Empty(result.Admitted);
        Assert.Equal(AdmissibilityRefusal.DescribesPeriodAfterCutoff, result.Refused[0].Refusal);
    }

    /// <summary>
    /// A stated probability outside [0,1] is a defect in the record rather than a bold forecast, and
    /// admitting it would put an impossible number into the calibration curve.
    /// </summary>
    [Fact]
    public void A_prediction_whose_stated_probability_is_not_a_probability_is_refused()
    {
        var result = BacktestEngine.Replay(
            ValidationFixtures.Window(),
            [ValidationFixtures.Candidate(Decision, Decision.AddDays(-1), probability: 1.4m)]);

        Assert.Empty(result.Admitted);
        Assert.True(result.Refused[0].WasUndeterminable);
    }

    /// <summary>
    /// Deterministic replay: same input, same admissions, same order. Two runs over the same history
    /// that differ mean the history changed, and that is only a usable signal if the engine does not
    /// wobble on its own.
    /// </summary>
    [Fact]
    public void The_same_history_replays_identically_however_it_was_ordered()
    {
        var ids = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToList();

        var candidates = ids
            .Select((id, index) => ValidationFixtures.Candidate(
                Decision.AddDays(index % 5),
                Decision.AddDays(index % 5).AddDays(-1),
                predictionId: id))
            .ToList();

        var window = ValidationFixtures.Window();

        var first = BacktestEngine.Replay(window, candidates);
        var shuffled = BacktestEngine.Replay(window, candidates.AsEnumerable().Reverse().ToList());

        Assert.Equal(25, first.Admitted.Count);
        Assert.Equal(
            first.Admitted.Select(p => p.PredictionId),
            shuffled.Admitted.Select(p => p.PredictionId));

        Assert.Equal(
            first.Admitted.Select(p => p.DecidedAtUtc),
            shuffled.Admitted.Select(p => p.DecidedAtUtc));
    }

    /// <summary>
    /// Refusals do not shrink the sample silently: admitted plus refused always equals considered.
    /// </summary>
    [Fact]
    public void Every_candidate_is_either_admitted_or_refused()
    {
        var candidates = new List<PredictionCandidate>
        {
            ValidationFixtures.Candidate(Decision, Decision.AddDays(-1)),
            ValidationFixtures.Candidate(Decision, Decision.AddDays(1)),
            ValidationFixtures.Candidate(Decision, null),
            ValidationFixtures.Candidate(ValidationFixtures.WindowStart.AddDays(-5), null),
        };

        var result = BacktestEngine.Replay(ValidationFixtures.Window(), candidates);

        Assert.Equal(candidates.Count, result.Considered);
        Assert.Equal(candidates.Count, result.Admitted.Count + result.Refused.Count);
    }
}
