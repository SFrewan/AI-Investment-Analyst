using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// One opportunity per drawdown, and a probability of the event the validation run scores.
/// </summary>
/// <remarks>
/// <para>
/// Both properties were measured as broken before they were fixed, over the platform's own stored
/// year: the screen produced 1,108 candidates from 77 independent drawdowns, and stated a
/// probability of one event while the validation run scored another, which turned a Brier score of
/// 0.11 into 0.55. Neither failure was visible from the code, and neither would have been visible
/// from a validation report either - it would simply have said the model was badly calibrated.
/// </para>
/// <para>
/// The parameters here are small and local so the series can be read by eye. They are deliberately
/// not the shipped ones: these tests are about what the rule counts and how often it speaks, not
/// about the judgement in the shipped numbers.
/// </para>
/// </remarks>
public sealed class PriceRecoveryEpisodeTests
{
    private static readonly DateTime Start = new(2026, 1, 5, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>Small enough to hand-build a series for, same shape as the shipped rule.</summary>
    private static readonly PriceRecoveryParameters Parameters = new(
        MinimumSessions: 5,
        DrawdownRatio: 0.10m,
        HorizonSessions: 2,
        MinimumTrials: 1,
        EventThresholdRatio: 0m);

    private const int Window = 10;

    /// <summary>
    /// A prefix that ends inside the second drawdown, so the whole series produces a candidate.
    /// </summary>
    /// <remarks>
    /// It carries six trials at the shipped comparison, which is enough for the two independent
    /// walks to disagree if they were counting different things. The full fixture ends recovered,
    /// so it produces no candidate at all - correct, and useless for comparing counts.
    /// </remarks>
    private const int AlignmentPrefix = 23;

    /// <summary>
    /// Two separate drawdowns, each lasting several sessions, with a full recovery between them.
    /// </summary>
    private static readonly decimal[] TwoEpisodes =
    [
        100m, 101m, 102m, 103m, 104m, 105m, 104m, 103m, 102m, 101m,
        // first drawdown: well under ten per cent below the peak, and it persists
        88m, 87m, 86m, 88m, 90m,
        // recovered
        104m, 106m, 108m, 110m, 112m,
        // second drawdown
        95m, 94m, 96m,
        // recovered again
        113m, 115m,
    ];

    // ---- episode deduplication ---------------------------------------------

    /// <summary>
    /// <strong>The property.</strong> One candidate per run of sessions the screen would fire on.
    /// </summary>
    /// <remarks>
    /// Stated as a property rather than as expected indices, because the thing being asserted is
    /// exactly "episodes, not sessions" - the count of maximal runs. Hand-written indices would
    /// assert the same thing less clearly and would need rewriting whenever the fixture moved.
    /// </remarks>
    [Fact]
    public void One_candidate_is_raised_per_run_of_qualifying_sessions()
    {
        var perSession = Walk(Evaluate);
        var perEpisode = Walk(series => PriceRecoveryRule.EvaluateEpisode(series, Parameters, Window));

        var runs = Runs(perSession);

        Assert.True(runs > 1, "The fixture must contain more than one drawdown for this to mean anything.");
        Assert.True(perSession.Count(fired => fired) > runs, "The fixture must contain a drawdown that persists.");

        Assert.Equal(runs, perEpisode.Count(fired => fired));
    }

    /// <summary>The episode-aware screen never speaks where the plain one is silent.</summary>
    [Fact]
    public void No_candidate_is_raised_that_the_screen_would_not_have_raised()
    {
        var perSession = Walk(Evaluate);
        var perEpisode = Walk(series => PriceRecoveryRule.EvaluateEpisode(series, Parameters, Window));

        for (var i = 0; i < perSession.Count; i++)
        {
            Assert.True(!perEpisode[i] || perSession[i]);
        }
    }

    /// <summary>A session inside an open drawdown says so, rather than saying nothing.</summary>
    /// <remarks>
    /// The reason matters as much as the suppression. An operator looking at a quiet cycle needs to
    /// know the difference between "no drawdown" and "this one is already being tracked".
    /// </remarks>
    [Fact]
    public void A_session_inside_an_open_drawdown_is_refused_by_name()
    {
        var reasons = new List<PriceRecoveryRefusal>();

        for (var upTo = Window; upTo <= TwoEpisodes.Length; upTo++)
        {
            reasons.Add(PriceRecoveryRule.EvaluateEpisode(Prefix(upTo), Parameters, Window).Refusal);
        }

        Assert.Contains(PriceRecoveryRefusal.EpisodeAlreadyOpen, reasons);

        Assert.Contains(
            "previous session",
            PriceRecoveryRule.Explain(PriceRecoveryRefusal.EpisodeAlreadyOpen),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The first window a series can produce is not swallowed for want of a session before it.
    /// </summary>
    [Fact]
    public void A_series_with_no_session_before_the_window_is_treated_as_a_new_episode()
    {
        // Exactly the history the rule reads: the session before it cannot be evaluated at all, so
        // there is nothing to compare against. A longer prefix would take the comparison path
        // instead and this test would stop asserting what it names.
        var exactly = Prefix(Parameters.MinimumSessions);

        var plain = PriceRecoveryRule.Evaluate(exactly, Parameters);
        var episode = PriceRecoveryRule.EvaluateEpisode(exactly, Parameters, Window);

        Assert.Equal(plain.Refusal, episode.Refusal);
    }

    [Fact]
    public void A_window_shorter_than_the_history_the_rule_needs_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            PriceRecoveryRule.EvaluateEpisode(Prefix(Window), Parameters, Parameters.MinimumSessions - 1));

    /// <summary>
    /// <strong>The regression.</strong> A history shorter than the window still suppresses duplicates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The platform asks for the last N sessions and is handed however many exist, so a series
    /// shorter than the window is the ordinary case for a newly tracked instrument - not a special
    /// one. The first version of this method treated it as the first-window case and called every
    /// session of such a history a new episode. Over the stored year that turned 77 drawdowns into
    /// 208, and it was invisible: each individual verdict was correct, and the count was only wrong
    /// against a second, independent count of the same thing.
    /// </para>
    /// <para>
    /// So the window here is deliberately longer than the whole fixture. Every prefix is short, the
    /// suppression must still hold, and the expected number is the same run count the property test
    /// above uses.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_history_shorter_than_the_window_still_raises_one_candidate_per_episode()
    {
        const int LongerThanTheFixture = 40;

        var perSession = Walk(Evaluate);
        var perEpisode = Walk(series =>
            PriceRecoveryRule.EvaluateEpisode(series, Parameters, LongerThanTheFixture));

        Assert.True(
            TwoEpisodes.Length < LongerThanTheFixture,
            "The window must exceed the fixture for this to exercise the short-history path.");

        Assert.Equal(Runs(perSession), perEpisode.Count(fired => fired));
    }

    // ---- the probability measures the event validation scores ---------------

    /// <summary>
    /// <strong>The alignment.</strong> Every success the rule counts is a true positive to the labeller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule counts a base rate; the validation run labels outcomes. If those two describe
    /// different events, the platform states a probability of one thing and scores it against
    /// another - and the resulting Brier score and calibration curve measure the gap rather than the
    /// model. That is not hypothetical: it is what the stored year actually did.
    /// </para>
    /// <para>
    /// So this walks the same series the rule walked, builds the prediction and the realised outcome
    /// the validation run would have built for each trial, asks <see cref="OutcomeLabeller"/> to
    /// judge them, and asserts the counts agree exactly. Re-deriving the trials here rather than
    /// asking the rule for them is the point: two independent walks that agree is evidence, one walk
    /// checked against itself is not.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_success_the_rule_counts_is_a_true_positive_to_the_validation_labeller()
    {
        var series = Prefix(AlignmentPrefix);

        var verdict = PriceRecoveryRule.Evaluate(series, Parameters);

        Assert.True(verdict.HasCandidate, "The fixture must produce a candidate for this to compare anything.");

        var candidate = verdict.Candidate!;
        var labelled = LabelledTruePositives(series, out var labelledTrials);

        Assert.Equal(candidate.Trials, labelledTrials);
        Assert.Equal(candidate.Successes, labelled);
    }

    /// <summary>The threshold is honoured, and honoured the way the labeller honours it.</summary>
    /// <remarks>
    /// Both sides compare with <c>&gt;=</c>. A trial that returns exactly the threshold counts as the
    /// event having happened, on both sides, which is the kind of boundary that otherwise disagrees
    /// silently for years.
    /// </remarks>
    [Theory]
    [InlineData(0.00)]
    [InlineData(0.02)]
    [InlineData(-0.05)]
    public void The_rule_and_the_labeller_agree_at_every_threshold(double threshold)
    {
        var ratio = (decimal)threshold;
        var parameters = Parameters with { EventThresholdRatio = ratio };
        var series = Prefix(AlignmentPrefix);

        var verdict = PriceRecoveryRule.Evaluate(series, parameters);

        Assert.True(verdict.HasCandidate);

        var labelled = LabelledTruePositives(series, out _, ratio);

        Assert.Equal(verdict.Candidate!.Successes, labelled);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Re-walks the trials and asks the validation labeller what each one was.
    /// </summary>
    private static int LabelledTruePositives(
        List<ClosingPrice> series,
        out int trials,
        decimal threshold = 0m)
    {
        var subject = IngestionSubject.Create("Security", "TEST.US");
        var eventThreshold = Percentage.FromRatio(threshold);
        var runningPeak = series[0].Close;

        trials = 0;
        var truePositives = 0;

        for (var i = 0; i < series.Count - Parameters.HorizonSessions; i++)
        {
            if (series[i].Close > runningPeak)
            {
                runningPeak = series[i].Close;
            }

            if (i < Parameters.MinimumSessions - 1)
            {
                continue;
            }

            if ((runningPeak - series[i].Close) / runningPeak < Parameters.DrawdownRatio)
            {
                continue;
            }

            trials++;

            var decidedAt = series[i].SessionCloseUtc;
            var resolvesAt = series[i + Parameters.HorizonSessions].SessionCloseUtc;
            var realised = (series[i + Parameters.HorizonSessions].Close - series[i].Close) / series[i].Close;

            var prediction = PredictionRecord.Create(
                Guid.NewGuid(),
                subject,
                decidedAt,
                resolvesAt,
                PredictionDirection.Positive,
                PriceRecoveryRule.Version,
                decidedAt,
                "price-recovery trial",
                Percentage.FromRatio(0.5m));

            var outcome = RealisedOutcome.Create(
                subject,
                resolvesAt,
                resolvesAt,
                Percentage.FromRatio(realised));

            // Judged from after the horizon, as the validation run judges: it never labels a
            // prediction that has not resolved.
            if (OutcomeLabeller.Label(prediction, outcome, eventThreshold, resolvesAt.AddDays(1))
                == OutcomeLabel.TruePositive)
            {
                truePositives++;
            }
        }

        return truePositives;
    }

    /// <summary>
    /// Runs the screen at every decision point, point-in-time: only prices up to that session.
    /// </summary>
    private static List<bool> Walk(Func<IReadOnlyList<ClosingPrice>, PriceRecoveryVerdict> screen)
    {
        var fired = new List<bool>();

        for (var upTo = Window; upTo <= TwoEpisodes.Length; upTo++)
        {
            fired.Add(screen(Prefix(upTo)).HasCandidate);
        }

        return fired;
    }

    private static PriceRecoveryVerdict Evaluate(IReadOnlyList<ClosingPrice> series) =>
        PriceRecoveryRule.Evaluate(series, Parameters);

    /// <summary>The number of maximal runs of consecutive firings - the number of episodes.</summary>
    private static int Runs(List<bool> fired)
    {
        var runs = 0;

        for (var i = 0; i < fired.Count; i++)
        {
            if (fired[i] && (i == 0 || !fired[i - 1]))
            {
                runs++;
            }
        }

        return runs;
    }

    private static List<ClosingPrice> Prefix(int count) =>
        TwoEpisodes
            .Take(count)
            .Select((close, index) => new ClosingPrice(Start.AddDays(index), close))
            .ToList();
}
