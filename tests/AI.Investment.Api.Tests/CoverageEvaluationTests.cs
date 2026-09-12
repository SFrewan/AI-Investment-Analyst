using AI.Investment.Domain.Coverage;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Gate 6's sealed rule as arithmetic, including the clause the universe depends on.
/// </summary>
/// <remarks>
/// The rule was pre-registered before any price existed. These facts hold the executable version
/// to the declaration, and in particular to the clause that a short series is never a fault - the
/// one that stops the gate quietly undoing the survivorship work.
/// </remarks>
public sealed class CoverageEvaluationTests
{
    private static readonly DateOnly WindowFrom = new(2021, 9, 1);
    private static readonly DateOnly WindowTo = new(2026, 8, 31);

    private static List<DateOnly> Weekdays(DateOnly from, DateOnly to, params DateOnly[] omit)
    {
        var skip = omit.ToHashSet();
        var days = new List<DateOnly>();

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !skip.Contains(d))
            {
                days.Add(d);
            }
        }

        return days;
    }

    /// <summary>
    /// The clause the whole universe rests on: a company that died has a short series, and that
    /// is the evidence rather than a hole in it.
    /// </summary>
    [Fact]
    public void A_series_that_ends_when_the_company_did_is_complete_not_faulty()
    {
        // Acquired in March 2023; its membership span ends there and so does its series.
        var died = new DateOnly(2023, 3, 31);
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true,
            Weekdays(WindowFrom, died),
            WindowFrom,
            died);

        Assert.False(verdict.IsFault);
        Assert.Empty(verdict.Faults);

        // It is emphatically shorter than the window, and that is not held against it.
        Assert.True(verdict.Sessions < CoverageEvaluation.ExpectedSessions(WindowFrom, WindowTo));
        Assert.True(CoverageEvaluation.EndsLegitimately(died, died));
    }

    [Fact]
    public void A_member_that_listed_after_the_window_opened_is_not_faulted_for_it()
    {
        var listed = new DateOnly(2023, 6, 1);
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true,
            Weekdays(listed, WindowTo),
            listed,
            WindowTo);

        Assert.False(verdict.IsFault);
        Assert.True(CoverageEvaluation.StartsLegitimately(listed, listed));

        // But a series that starts long after its span began is a different matter.
        Assert.False(CoverageEvaluation.StartsLegitimately(listed, WindowFrom));
    }

    /// <summary>
    /// Four sessions is a holiday run; five is a hole.
    /// </summary>
    [Fact]
    public void An_interior_gap_is_legitimate_to_four_sessions_and_a_fault_at_five()
    {
        var from = new DateOnly(2023, 1, 2);
        var to = new DateOnly(2023, 3, 31);

        // Remove four consecutive weekdays: a long holiday run, and no fault.
        var fourMissing = new[]
        {
            new DateOnly(2023, 2, 6), new DateOnly(2023, 2, 7),
            new DateOnly(2023, 2, 8), new DateOnly(2023, 2, 9),
        };

        var tolerated = CoverageEvaluation.Evaluate(
            acquirable: true, Weekdays(from, to, fourMissing), from, to);

        Assert.False(tolerated.IsFault);
        Assert.Contains("within the 4", tolerated.Reason, StringComparison.Ordinal);

        // A fifth consecutive weekday, and no holiday run explains it.
        var fiveMissing = fourMissing.Append(new DateOnly(2023, 2, 10)).ToArray();

        var faulted = CoverageEvaluation.Evaluate(
            acquirable: true, Weekdays(from, to, fiveMissing), from, to);

        Assert.True(faulted.IsFault);
        Assert.Contains(CoverageEvaluation.InteriorGap, faulted.Faults);
    }

    [Fact]
    public void Weekends_are_not_gaps()
    {
        var from = new DateOnly(2023, 1, 2);
        var to = new DateOnly(2023, 1, 31);

        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, Weekdays(from, to), from, to);

        Assert.False(verdict.IsFault);
        Assert.Equal("complete across its membership span", verdict.Reason);
    }

    [Fact]
    public void An_acquirable_member_with_no_series_at_all_is_a_fault()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, [], WindowFrom, WindowTo);

        Assert.True(verdict.IsFault);
        Assert.Contains(CoverageEvaluation.NoSeries, verdict.Faults);
        Assert.Equal(0, verdict.Sessions);

        // And it states what it expected, so the size of the hole is legible.
        Assert.InRange(verdict.Expected, 1240, 1320);
    }

    /// <summary>
    /// A member excluded from acquisition is not measured by this gate, and is not a fault.
    /// </summary>
    [Fact]
    public void A_member_that_was_never_acquirable_is_not_faulted_for_having_no_series()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: false, [], WindowFrom, WindowTo);

        Assert.False(verdict.IsFault);
        Assert.Empty(verdict.Faults);
        Assert.Contains("no series expected", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 50% refusal is a recorded coverage event and a fault, never a silent drop.
    /// </summary>
    [Fact]
    public void A_series_refused_as_discontinuous_is_a_recorded_fault()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true,
            Weekdays(WindowFrom, WindowTo),
            WindowFrom,
            WindowTo,
            refusedAsDiscontinuous: true);

        Assert.True(verdict.IsFault);
        Assert.Contains(CoverageEvaluation.UnexplainedDiscontinuity, verdict.Faults);
        Assert.Contains("rather than dropping quietly", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_passes_only_on_zero_faults()
    {
        Assert.Equal(0, CoverageEvaluation.FaultThreshold);
        Assert.True(CoverageEvaluation.Passes(0));
        Assert.False(CoverageEvaluation.Passes(1));
        Assert.False(CoverageEvaluation.Passes(335));
    }

    // ---- the pending status, and the safeguard that keeps it honest ------------------------------

    /// <summary>
    /// The whole reason for the distinction: before acquisition, empty means pending, not broken.
    /// </summary>
    [Fact]
    public void An_acquirable_member_never_requested_is_pending_rather_than_faulty()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, [], WindowFrom, WindowTo, wasRequested: false);

        Assert.False(verdict.IsFault);
        Assert.True(verdict.NotYetAcquired);
        Assert.Empty(verdict.Faults);
        Assert.DoesNotContain(CoverageEvaluation.NoSeries, verdict.Faults);

        // And it still states what a complete series would have been, so the pending work is sized.
        Assert.InRange(verdict.Expected, 1240, 1320);
        Assert.Contains("has not run for it", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case the distinction exists to stop hiding: asked for, answered, and still empty.
    /// </summary>
    [Fact]
    public void An_acquirable_member_that_was_requested_and_is_empty_is_still_a_fault()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true, [], WindowFrom, WindowTo, wasRequested: true);

        Assert.True(verdict.IsFault);
        Assert.False(verdict.NotYetAcquired);
        Assert.Contains(CoverageEvaluation.NoSeries, verdict.Faults);
        Assert.Contains("a request for it succeeded", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The default is the strict reading, so no existing caller acquired an excuse by accident.
    /// </summary>
    [Fact]
    public void Omitting_the_argument_keeps_the_old_strict_behaviour()
    {
        var byDefault = CoverageEvaluation.Evaluate(acquirable: true, [], WindowFrom, WindowTo);
        var stated = CoverageEvaluation.Evaluate(
            acquirable: true, [], WindowFrom, WindowTo, wasRequested: true);

        Assert.True(byDefault.IsFault);
        Assert.False(byDefault.NotYetAcquired);

        // Compared part by part rather than as records: Faults is a list, and record equality
        // would compare two separately allocated empty lists by reference and always disagree.
        Assert.Equal(stated.IsFault, byDefault.IsFault);
        Assert.Equal(stated.NotYetAcquired, byDefault.NotYetAcquired);
        Assert.Equal(stated.Faults, byDefault.Faults);
        Assert.Equal(stated.Sessions, byDefault.Sessions);
        Assert.Equal(stated.Expected, byDefault.Expected);
        Assert.Equal(stated.Reason, byDefault.Reason);
    }

    /// <summary>
    /// A member excluded from acquisition is not pending either - it is simply not measured.
    /// </summary>
    [Fact]
    public void A_member_that_was_never_acquirable_is_not_pending_either()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: false, [], WindowFrom, WindowTo, wasRequested: false);

        Assert.False(verdict.IsFault);
        Assert.False(verdict.NotYetAcquired);
    }

    /// <summary>
    /// A refusal is judged before the pending branch: something was fetched to refuse.
    /// </summary>
    [Fact]
    public void A_refused_series_is_a_fault_whatever_the_request_flag_says()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true,
            Weekdays(WindowFrom, WindowTo),
            WindowFrom,
            WindowTo,
            refusedAsDiscontinuous: true,
            wasRequested: false);

        Assert.True(verdict.IsFault);
        Assert.False(verdict.NotYetAcquired);
        Assert.Contains(CoverageEvaluation.UnexplainedDiscontinuity, verdict.Faults);
    }

    /// <summary>
    /// A held series is never pending, however the flag is set.
    /// </summary>
    [Fact]
    public void A_member_that_holds_prices_is_never_pending()
    {
        var verdict = CoverageEvaluation.Evaluate(
            acquirable: true,
            Weekdays(new DateOnly(2023, 1, 2), new DateOnly(2023, 1, 31)),
            new DateOnly(2023, 1, 2),
            new DateOnly(2023, 1, 31),
            wasRequested: false);

        Assert.False(verdict.IsFault);
        Assert.False(verdict.NotYetAcquired);
    }

    /// <summary>
    /// The safeguard. Excusing "never asked for" is honest only while nobody claims to have asked.
    /// </summary>
    [Fact]
    public void Acquisition_complete_with_anything_still_pending_fails_the_gate()
    {
        // Before the stage claims to be finished, pending members do not fail the gate.
        Assert.True(CoverageEvaluation.Passes(0, 352, acquisitionComplete: false));

        // The moment it does, a member nobody asked for was skipped, not pending.
        Assert.False(CoverageEvaluation.Passes(0, 1, acquisitionComplete: true));
        Assert.False(CoverageEvaluation.Passes(0, 352, acquisitionComplete: true));

        // And a completed acquisition with nothing left pending and no faults is the passing case.
        Assert.True(CoverageEvaluation.Passes(0, 0, acquisitionComplete: true));
    }

    /// <summary>
    /// The threshold did not move, and a fault is still a fault whatever the pending count says.
    /// </summary>
    [Fact]
    public void The_zero_fault_threshold_is_untouched_by_the_new_status()
    {
        Assert.Equal(0, CoverageEvaluation.FaultThreshold);
        Assert.Equal(4, CoverageEvaluation.InteriorGapTolerance);
        Assert.Equal(10, CoverageEvaluation.CohortTolerance);

        Assert.False(CoverageEvaluation.Passes(1, 0, acquisitionComplete: false));
        Assert.False(CoverageEvaluation.Passes(1, 0, acquisitionComplete: true));

        // The one-argument overload is unchanged and still means exactly what it meant.
        Assert.True(CoverageEvaluation.Passes(0));
        Assert.False(CoverageEvaluation.Passes(1));
    }

    [Fact]
    public void A_negative_pending_count_is_refused_rather_than_averaged_away()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoverageEvaluation.Passes(0, -1, acquisitionComplete: true));
    }

    [Fact]
    public void Duplicate_sessions_do_not_invent_coverage_or_gaps()
    {
        var from = new DateOnly(2023, 1, 2);
        var to = new DateOnly(2023, 1, 31);
        var doubled = Weekdays(from, to).Concat(Weekdays(from, to)).ToList();

        var verdict = CoverageEvaluation.Evaluate(acquirable: true, doubled, from, to);

        Assert.False(verdict.IsFault);
        Assert.Equal(Weekdays(from, to).Count, verdict.Sessions);
    }
}
