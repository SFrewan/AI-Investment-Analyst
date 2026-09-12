using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Boundary and branch-order characterisation for <see cref="CoverageEvaluation"/>, additive to
/// <see cref="CoverageEvaluationTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These tests encode no policy.</strong> Every expected value was read off the current
/// implementation, and several of them pin behaviour a review has flagged as worth a decision. Where
/// that is so it is said in the test, and the test still asserts the behaviour as it is. A
/// characterisation test that quietly asserts the preferred answer is worse than none: it makes a
/// future change look safe by agreeing with it in advance.
/// </para>
/// <para>
/// <strong>Why a second file rather than edits to the first.</strong> <c>CoverageEvaluationTests</c>
/// already covers the rule's substance - short series, the four-versus-five gap tolerance, weekends,
/// the pending status and its safeguard, the zero-fault threshold. Nothing here repeats it. What is
/// added is the arithmetic at the edges and the order the branches run in: degenerate and reversed
/// spans, the two branches that report <c>Expected = 0</c> regardless of span, multiple gaps in one
/// series, and the exact reason strings. Those are the parts a refactor can move without any
/// existing test noticing.
/// </para>
/// <para>
/// <strong>Dates.</strong> September 2021, because 2021-09-01 is a Wednesday and the acquisition
/// window opens on it. 09-04 is a Saturday, 09-06 a Monday, 09-10 a Friday. Gaps are counted in
/// weekdays because <c>Weekdays</c> is the platform's only notion of a session: it holds no exchange
/// calendar and will not invent one.
/// </para>
/// </remarks>
public sealed class CoverageEvaluationBoundaryTests
{
    private static DateOnly Sep(int day) => new(2021, 9, day);

    private static DateOnly[] NoSessions() => [];

    // ---------- the fault-kind names ----------

    [Fact]
    public void The_fault_kind_names_are_unchanged()
    {
        // The numeric constants are pinned in CoverageEvaluationTests. These are the strings the
        // reports and the reconciliation artefacts group by.
        Assert.Equal("no-series", CoverageEvaluation.NoSeries);
        Assert.Equal("interior-gap", CoverageEvaluation.InteriorGap);
        Assert.Equal("unexplained-discontinuity", CoverageEvaluation.UnexplainedDiscontinuity);
        Assert.Equal("not-yet-acquired", CoverageEvaluation.NotYetAcquired);
    }

    // ---------- branch order, and the two branches that ignore the span ----------

    [Fact]
    public void A_non_acquirable_member_still_reports_the_sessions_it_holds()
    {
        var held = new[] { Sep(6), Sep(7), Sep(8) };

        var verdict = CoverageEvaluation.Evaluate(acquirable: false, held, Sep(1), Sep(30));

        // Sessions is held.Count even here, so a non-acquirable member holding data stays visible
        // rather than being reported as empty.
        Assert.Equal(3, verdict.Sessions);
        Assert.False(verdict.IsFault);
    }

    [Fact]
    public void A_non_acquirable_member_reports_zero_expected_sessions_whatever_its_span()
    {
        // Expected is hard-coded to 0 in this branch; the span is never consulted. The same span
        // yields 22 through ExpectedSessions, so the 0 is a property of the branch, not the dates.
        var verdict = CoverageEvaluation.Evaluate(acquirable: false, NoSessions(), Sep(1), Sep(30));

        Assert.Equal(0, verdict.Expected);
        Assert.Equal(22, CoverageEvaluation.ExpectedSessions(Sep(1), Sep(30)));
    }

    [Fact]
    public void Not_acquirable_is_tested_before_the_discontinuity_refusal()
    {
        // Branch order, pinned deliberately. A refused series belonging to a non-acquirable member
        // is NOT a fault, because the first branch returns before the refusal is looked at.
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: false, NoSessions(), Sep(1), Sep(30), refusedAsDiscontinuous: true);

        Assert.False(verdict.IsFault);
        Assert.Empty(verdict.Faults);
    }

    [Fact]
    public void A_refused_series_reports_zero_expected_sessions_whatever_its_span()
    {
        var held = new[] { Sep(6) };

        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, held, Sep(1), Sep(30), refusedAsDiscontinuous: true);

        // As in the not-acquirable branch, Expected is hard-coded to 0 and the span is not read.
        Assert.Equal(0, verdict.Expected);
        Assert.Equal(1, verdict.Sessions);
    }

    [Fact]
    public void A_refused_series_does_not_also_accrue_interior_gap_faults()
    {
        // The refusal returns before the gap loop runs, so a member with both problems appears once
        // carrying one fault rather than twice. This is the non-double-counting property, and it is
        // why a refused member contributes exactly one fault event.
        var held = new[] { Sep(6), Sep(24) };

        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, held, Sep(1), Sep(30), refusedAsDiscontinuous: true);

        Assert.Equal(CoverageEvaluation.UnexplainedDiscontinuity, Assert.Single(verdict.Faults));
    }

    [Fact]
    public void A_refusal_is_judged_before_emptiness_so_an_empty_refused_series_is_not_no_series()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, NoSessions(), Sep(1), Sep(30), refusedAsDiscontinuous: true);

        Assert.Equal(CoverageEvaluation.UnexplainedDiscontinuity, Assert.Single(verdict.Faults));
        Assert.Equal(0, verdict.Sessions);
    }

    // ---------- more than one gap in one series ----------

    [Fact]
    public void Each_interior_gap_contributes_its_own_fault_event()
    {
        // Two gaps, 5 weekdays then 7. The member is ONE fault member carrying TWO fault events,
        // which is how eight interior-gap members produce 114 events rather than 8.
        var held = new[] { Sep(6), Sep(14), Sep(24) };

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, held, Sep(1), Sep(30));

        Assert.True(verdict.IsFault);
        Assert.Collection(
            verdict.Faults,
            first => Assert.Equal(CoverageEvaluation.InteriorGap, first),
            second => Assert.Equal(CoverageEvaluation.InteriorGap, second));
    }

    [Fact]
    public void The_reason_names_the_worst_gap_not_the_first()
    {
        var held = new[] { Sep(6), Sep(14), Sep(24) };

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, held, Sep(1), Sep(30));

        Assert.Equal(
            "an interior gap of 7 session(s) exceeds the 4 a holiday run can explain",
            verdict.Reason);
    }

    [Fact]
    public void Sessions_may_arrive_in_any_order()
    {
        var held = new[] { Sep(14), Sep(6), Sep(24) };

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, held, Sep(1), Sep(30));

        // Identical to the ordered case: the input is sorted before the gaps are walked.
        Assert.True(verdict.IsFault);
        Assert.Equal(3, verdict.Sessions);
        Assert.Equal(
            "an interior gap of 7 session(s) exceeds the 4 a holiday run can explain",
            verdict.Reason);
    }

    // ---------- what the span is and is not used for ----------

    [Fact]
    public void Only_interior_gaps_are_measured_not_the_run_up_to_the_span_edges()
    {
        // A contiguous three-day series in the middle of a month-long span. What happens before the
        // first observation or after the last is the member's listing or its death, and Evaluate
        // does not judge it - the cohort tolerances do, elsewhere.
        var held = new[] { Sep(13), Sep(14), Sep(15) };

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, held, Sep(1), Sep(30));

        Assert.False(verdict.IsFault);
        Assert.Equal(3, verdict.Sessions);
        Assert.Equal(22, verdict.Expected);
        Assert.Equal("complete across its membership span", verdict.Reason);
    }

    [Fact]
    public void A_held_series_outside_its_declared_span_is_still_measured()
    {
        // The span and the held sessions are never cross-checked: the gap walk uses only the held
        // dates. A series lying entirely outside its span reports the span's expected count beside
        // its own session count, and no fault arises from the mismatch.
        var held = new[] { Sep(20), Sep(21) };

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, held, Sep(1), Sep(3));

        Assert.False(verdict.IsFault);
        Assert.Equal(2, verdict.Sessions);
        Assert.Equal(3, verdict.Expected);
    }

    // ---------- ExpectedSessions, at its boundaries ----------

    [Fact]
    public void ExpectedSessions_counts_weekdays_inclusively()
    {
        Assert.Equal(5, CoverageEvaluation.ExpectedSessions(Sep(6), Sep(10)));   // Mon-Fri
        Assert.Equal(5, CoverageEvaluation.ExpectedSessions(Sep(6), Sep(12)));   // Mon-Sun
        Assert.Equal(22, CoverageEvaluation.ExpectedSessions(Sep(1), Sep(30)));
    }

    [Fact]
    public void A_single_day_span_on_a_weekday_expects_one_session()
    {
        // 2021-09-01 is a Wednesday, and it is the acquisition window's first day.
        Assert.Equal(1, CoverageEvaluation.ExpectedSessions(Sep(1), Sep(1)));
    }

    [Fact]
    public void A_single_day_span_on_a_weekend_expects_no_sessions()
    {
        // 2021-09-04 is a Saturday. spanFrom == spanTo does not imply Expected == 1.
        Assert.Equal(0, CoverageEvaluation.ExpectedSessions(Sep(4), Sep(4)));
    }

    [Fact]
    public void A_reversed_span_expects_zero_sessions_rather_than_throwing()
    {
        Assert.Equal(0, CoverageEvaluation.ExpectedSessions(Sep(30), Sep(1)));
    }

    [Fact]
    public void Evaluate_accepts_a_reversed_span_directly_and_reports_zero_expected()
    {
        // Reachable through Evaluate, which takes the two dates as given and does not order them.
        // It is NOT reachable through either Gate 6 harness, because both clamp spanTo up to the
        // window start before calling. That clamp lives in the callers; it is not this method's
        // behaviour and is neither asserted nor altered here.
        var verdict = CoverageEvaluation.Evaluate(acquirable: true, NoSessions(), Sep(30), Sep(1));

        Assert.True(verdict.IsFault);
        Assert.Equal(CoverageEvaluation.NoSeries, Assert.Single(verdict.Faults));
        Assert.Equal(0, verdict.Sessions);
        Assert.Equal(0, verdict.Expected);
    }

    // ---------- the two span helpers, at their tolerance boundary ----------

    [Fact]
    public void EndsLegitimately_allows_a_trailing_run_up_to_the_cohort_tolerance()
    {
        // Exactly 10 weekdays after the last session is tolerated; 11 is not.
        Assert.True(CoverageEvaluation.EndsLegitimately(Sep(10), Sep(24)));
        Assert.False(CoverageEvaluation.EndsLegitimately(Sep(10), Sep(27)));
    }

    [Fact]
    public void StartsLegitimately_allows_a_leading_run_up_to_the_cohort_tolerance()
    {
        // Exactly 10 weekdays before the first session is tolerated; 11 is not.
        Assert.True(CoverageEvaluation.StartsLegitimately(Sep(15), Sep(1)));
        Assert.False(CoverageEvaluation.StartsLegitimately(Sep(16), Sep(1)));
    }

    // ---------- the single-argument Passes overload ----------

    [Fact]
    public void Passes_does_not_guard_against_a_negative_fault_count()
    {
        // Characterised, not endorsed. The one-argument overload has no guard, so a negative count
        // passes. The three-argument overload does guard its pending count, and that difference is
        // asserted in CoverageEvaluationTests. No caller can produce a negative fault count today.
        Assert.True(CoverageEvaluation.Passes(-1));
    }

    // ---------- the exact reason strings ----------

    [Fact]
    public void Every_branch_returns_its_reason_verbatim()
    {
        // The existing tests match substrings. These are the whole strings, so that a reworded
        // reason fails here rather than passing everywhere.
        var notAcquirable = CoverageEvaluation.Evaluate(false, NoSessions(), Sep(1), Sep(30));
        Assert.Equal("not acquirable; no series expected", notAcquirable.Reason);

        var refused = CoverageEvaluation.Evaluate(
            true, NoSessions(), Sep(1), Sep(30), refusedAsDiscontinuous: true);
        Assert.Equal(
            "the series was refused as an unexplained discontinuity, which the sealed rule "
            + "records as a coverage event and counts as a fault rather than dropping quietly",
            refused.Reason);

        var empty = CoverageEvaluation.Evaluate(true, NoSessions(), Sep(1), Sep(30));
        Assert.Equal(
            "acquirable, a request for it succeeded, and no closing price is held for any "
            + "session in its membership span",
            empty.Reason);

        var pending = CoverageEvaluation.Evaluate(
            true, NoSessions(), Sep(1), Sep(30), wasRequested: false);
        Assert.Equal(
            "acquirable and empty, but no request for this symbol has succeeded, so the "
            + "acquisition has not run for it rather than run and come back empty",
            pending.Reason);

        var tolerated = new[] { Sep(6), Sep(13) };
        Assert.Equal(
            "the longest interior gap is 4 session(s), within the 4 a holiday run can explain",
            CoverageEvaluation.Evaluate(true, tolerated, Sep(6), Sep(13)).Reason);

        var faulted = new[] { Sep(6), Sep(14) };
        Assert.Equal(
            "an interior gap of 5 session(s) exceeds the 4 a holiday run can explain",
            CoverageEvaluation.Evaluate(true, faulted, Sep(6), Sep(14)).Reason);
    }

    // ---------- argument validation ----------

    [Fact]
    public void A_null_session_sequence_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            CoverageEvaluation.Evaluate(true, null!, Sep(1), Sep(30));
        });
    }
}
