using AI.Investment.Domain.Coverage;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// A9 — characterisation of the span boundary the harness <c>spanTo</c> floor acts on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These tests encode no policy and assert no desired behaviour.</strong> Every expected
/// value was read off the current implementation. They exist so that the A9 decision — whether the
/// harness floor <c>spanTo &lt; windowFrom ? windowFrom : spanTo</c> stays — is taken against a
/// pinned baseline rather than against nothing.
/// </para>
/// <para>
/// <strong>What these tests DO cover.</strong> The evaluator's behaviour at the three relationships
/// between <c>spanTo</c> and the window start, and the exact difference the floor makes to a
/// verdict: it changes <c>Expected</c> and nothing else.
/// </para>
/// <para>
/// <strong>What these tests DO NOT cover, and cannot.</strong> The floor expression itself. It lives
/// inside private member-construction lambdas in four harnesses
/// (<c>AcquisitionAuthorizationTests</c>, <c>PostAcquisitionVerificationTests</c>,
/// <c>PostBatchReviewTests</c>, <c>RecoveredSeriesMoveScreenTests</c>) and a fifth formulation in
/// <c>AcquisitionDryRunTests.Span</c>, none of which is reachable without a database and the
/// identity artefact. Reaching it would mean extracting a helper, which is a production change and
/// is not what a characterisation test is for. So these tests pin the CONSEQUENCE of the floor, by
/// passing the clamped and unclamped values directly. <strong>Deleting the floor from all five
/// sites would not fail any test here.</strong> That limitation is deliberate and is reported.
/// </para>
/// <para>
/// The window comes from <see cref="AcquisitionPlanning.Window"/> rather than from a local constant,
/// so that these cases stay tied to the same sealed window the harnesses read. That seam is already
/// used this way by <c>PriceAcquisitionPlanningTests</c>.
/// </para>
/// </remarks>
public sealed class CoverageEvaluationSpanBoundaryTests
{
    /// <summary>
    /// The window these cases are measured against, and the fact that makes case (b) a "1".
    /// </summary>
    /// <remarks>
    /// If the sealed window ever moves, this test fails first and says why, instead of the session
    /// counts below failing for a reason nobody can see.
    /// </remarks>
    [Fact]
    public void The_sealed_window_is_the_one_these_cases_are_measured_against()
    {
        var window = AcquisitionPlanning.Window();

        Assert.Equal(new DateOnly(2021, 9, 1), window.From);
        Assert.Equal(new DateOnly(2026, 8, 31), window.To);

        // Load-bearing for case (b): a one-day span expects one session only because the window
        // opens on a weekday.
        Assert.Equal(DayOfWeek.Wednesday, window.From.DayOfWeek);
    }

    /// <summary>
    /// (a) <c>spanTo &lt; windowFrom</c> — the only case the floor acts on.
    /// </summary>
    /// <remarks>
    /// This is what the sealed rule produces on its own for a member whose last cohort cut precedes
    /// the window start: <c>From</c> is raised to the window start by the rule's own "later of"
    /// clause, <c>To</c> stays at the cut, and the two cross.
    /// </remarks>
    [Fact]
    public void A_spanTo_before_the_window_start_expects_no_sessions()
    {
        var window = AcquisitionPlanning.Window();
        var spanTo = window.From.AddDays(-1);

        Assert.Equal(0, CoverageEvaluation.ExpectedSessions(window.From, spanTo));

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, [], window.From, spanTo);

        Assert.True(verdict.IsFault);
        Assert.Contains(CoverageEvaluation.NoSeries, verdict.Faults);
        Assert.Equal(0, verdict.Sessions);
        Assert.Equal(0, verdict.Expected);
        Assert.False(verdict.NotYetAcquired);
    }

    /// <summary>
    /// (b) <c>spanTo == windowFrom</c> — what the floor produces.
    /// </summary>
    [Fact]
    public void A_spanTo_at_the_window_start_expects_one_session()
    {
        var window = AcquisitionPlanning.Window();

        Assert.Equal(1, CoverageEvaluation.ExpectedSessions(window.From, window.From));

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, [], window.From, window.From);

        Assert.True(verdict.IsFault);
        Assert.Contains(CoverageEvaluation.NoSeries, verdict.Faults);
        Assert.Equal(0, verdict.Sessions);
        Assert.Equal(1, verdict.Expected);
        Assert.False(verdict.NotYetAcquired);
    }

    /// <summary>
    /// (c) <c>spanTo &gt; windowFrom</c> — the floor does not fire, and most members are here.
    /// </summary>
    [Fact]
    public void A_spanTo_after_the_window_start_is_untouched_by_the_floor()
    {
        var window = AcquisitionPlanning.Window();
        var spanTo = new DateOnly(2023, 8, 31);

        Assert.Equal(522, CoverageEvaluation.ExpectedSessions(window.From, spanTo));

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, [], window.From, spanTo);

        Assert.True(verdict.IsFault);
        Assert.Contains(CoverageEvaluation.NoSeries, verdict.Faults);
        Assert.Equal(522, verdict.Expected);
    }

    /// <summary>
    /// The A9 question in one test: what the floor changes, and what it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two spans here are the same member evaluated with the floor applied and without it. This
    /// is the boundary occupied by the three members whose sole cohort cut is the day before the
    /// window opens — <c>BPYU.US</c>, <c>KIN.US</c>, <c>USCR.US</c>.
    /// </para>
    /// <para>
    /// Their identities are not loaded here: the member-to-span derivation needs a database and the
    /// identity artefact, so what is pinned is the span pair those three produce, not the lookup
    /// that produces it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_floor_changes_the_expected_count_and_leaves_the_verdict_alone()
    {
        var window = AcquisitionPlanning.Window();

        var unfloored = CoverageEvaluation.Evaluate(
            acquirable: true, [], window.From, window.From.AddDays(-1));

        var floored = CoverageEvaluation.Evaluate(
            acquirable: true, [], window.From, window.From);

        // Compared part by part rather than as records: Faults is a list, and record equality would
        // compare two separately allocated lists by reference and always disagree. The same reason
        // CoverageEvaluationTests gives for doing it this way.
        Assert.Equal(unfloored.IsFault, floored.IsFault);
        Assert.Equal(unfloored.Faults, floored.Faults);
        Assert.Equal(unfloored.Sessions, floored.Sessions);
        Assert.Equal(unfloored.Reason, floored.Reason);
        Assert.Equal(unfloored.NotYetAcquired, floored.NotYetAcquired);

        // The one thing that differs, and the whole of what the floor does today.
        Assert.Equal(0, unfloored.Expected);
        Assert.Equal(1, floored.Expected);
    }
}
