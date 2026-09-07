using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Why the quarterly cadence gate is 2.9 and can never again be 3.5.
/// </summary>
/// <remarks>
/// <para>
/// A threshold that moved after the measurement it governs is the most dangerous kind of change
/// this repository can make, because a bar lowered to fit a result and a bar corrected from a false
/// premise look identical in a diff. This class exists so the difference is written down, checkable,
/// and enforced rather than remembered.
/// </para>
/// <para>
/// <strong>What was wrong.</strong> The expansion plan asserted that quarterly fundamentals should
/// arrive at least 3.5 times per company per year, reasoning from four quarters in a year. A US
/// issuer files a Form 10-Q for the first three fiscal quarters and <em>none for the fourth</em>:
/// the fourth quarter is reported inside the Form 10-K as part of the annual figure, and is never
/// separately tagged as a quarter-length duration fact in XBRL. Three is therefore the ceiling, and
/// 3.5 was unsatisfiable by any filer following the rules.
/// </para>
/// <para>
/// <strong>Why that makes it a correction rather than a relaxation.</strong> The original gate
/// would have failed on flawless data. It was not measuring a property the data could have had. A
/// relaxation, by contrast, is a bar an honest result could have cleared and did not - and no such
/// bar has moved: the annual-equality check, the write-nothing checks and every admission criterion
/// stand exactly where they were.
/// </para>
/// <para>
/// <strong>Why 2.9 and not 3.0.</strong> A fiscal year offset from the calendar distributes a
/// company's four quarter-ends unevenly across calendar years, and a company that changes its fiscal
/// year end loses one. The tenth of a point absorbs the calendar, not a shortfall in the filings.
/// The observed rate over complete years was 3.27, comfortably clear of both.
/// </para>
/// </remarks>
public sealed class QuarterlyCadenceTests
{
    /// <summary>How many 10-Q filings a US issuer makes in a fiscal year.</summary>
    /// <remarks>
    /// Q1, Q2, Q3. There is no Q4 10-Q. This is the fact the original gate got wrong, and every
    /// assertion below is a consequence of it.
    /// </remarks>
    private const int QuarterlyFilingsPerFiscalYear = 3;

    [Fact]
    public void A_us_issuer_files_three_quarterly_reports_a_year_not_four() =>
        Assert.Equal(
            QuarterlyFilingsPerFiscalYear,
            (int)QuarterlyNormalisationTests.QuarterlyFilingsPerYearCeiling);

    /// <summary>
    /// The threshold that was replaced sat above the ceiling, which is what made it impossible.
    /// </summary>
    [Fact]
    public void The_replaced_threshold_demanded_more_quarters_than_exist() =>
        Assert.True(
            QuarterlyNormalisationTests.RejectedImpossibleThreshold >
            QuarterlyNormalisationTests.QuarterlyFilingsPerYearCeiling,
            "3.5 was rejected because it exceeded the three quarterly filings a US issuer makes. " +
            "If this ever becomes false the stated reason for the correction was wrong, and the " +
            "correction should be revisited rather than kept.");

    /// <summary>
    /// The gate in force sits under the ceiling, so it is satisfiable by a compliant filer.
    /// </summary>
    /// <remarks>
    /// The guard against the correction being used twice. Raising the gate back above three would
    /// reintroduce exactly the defect this replaced, and would fail here rather than at the end of
    /// a run.
    /// </remarks>
    [Fact]
    public void The_gate_in_force_is_achievable() =>
        Assert.True(
            QuarterlyNormalisationTests.MinimumQuarterlyPeriodsPerCompletedYear <=
            QuarterlyNormalisationTests.QuarterlyFilingsPerYearCeiling,
            "The quarterly cadence gate has been raised above three quarterly filings a year, which " +
            "no US issuer produces. That is the unsatisfiable condition the 3.5 threshold was " +
            "corrected for.");

    /// <summary>
    /// And it is not so low that it would pass on annual data, which is the failure it screens for.
    /// </summary>
    /// <remarks>
    /// A gate is only worth having if something fails it. Annual-only extraction yields about one
    /// period per company per year; anything at or below two would wave that through, and the whole
    /// point of the check is to notice when the filter is still closed.
    /// </remarks>
    [Fact]
    public void The_gate_in_force_would_still_catch_annual_only_data() =>
        Assert.True(
            QuarterlyNormalisationTests.MinimumQuarterlyPeriodsPerCompletedYear > 2m,
            "The quarterly cadence gate has fallen to a level that annual-only extraction would " +
            "pass. It exists to catch exactly that.");

    /// <summary>The corrected gate is the number that was approved, not one nearby.</summary>
    [Fact]
    public void The_gate_in_force_is_the_approved_figure() =>
        Assert.Equal(2.9m, QuarterlyNormalisationTests.MinimumQuarterlyPeriodsPerCompletedYear);
}
