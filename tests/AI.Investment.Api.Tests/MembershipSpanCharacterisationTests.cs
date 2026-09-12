using System.Globalization;
using AI.Investment.Domain.Coverage;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The A1b gate — the five membership-span formulations, held against one another.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This test encodes no policy and asserts no desired behaviour.</strong> The five private
/// functions below are verbatim transcriptions of the five span derivations that exist in this
/// repository today, each reproduced from its own site and named after it. Nothing here is a
/// simplification of them, and nothing here is the rule "as it ought to be".
/// </para>
/// <para>
/// <strong>Why it exists.</strong> <c>CoverageEvaluationSpanBoundaryTests</c> records, in its own
/// remarks, that the span derivation <em>"lives inside private member-construction lambdas in four
/// harnesses … and a fifth formulation in <c>AcquisitionDryRunTests.Span</c>"</em>, that none of
/// them is reachable without a database and the identity artefact, and that <strong>"deleting the
/// floor from all five sites would not fail any test here."</strong> That is the hole this file
/// closes. It reaches the expressions by transcribing them rather than by loading a database, which
/// is the only way to compare them at all.
/// </para>
/// <para>
/// <strong>The five sites.</strong> <c>AcquisitionAuthorizationTests</c> (construction at 610-631,
/// with its window floor applied later, at the call site, line 420), <c>PostAcquisitionVerification
/// Tests</c> (536-559), <c>PostBatchReviewTests</c> (1279-1301), <c>RecoveredSeriesMoveScreenTests</c>
/// (1109-1128), and <c>AcquisitionDryRunTests.Span</c> (403-418), whose inputs are built at 386-398.
/// </para>
/// <para>
/// <strong>What the comparison found, stated before it is asserted.</strong> The first four are the
/// same rule and clamp identically: a floor at <c>windowFrom</c> on both ends.
/// <c>AcquisitionAuthorizationTests</c> reaches that result by a different route — it stores
/// <c>spanFrom</c> unfloored and applies <c>Later(windowFrom, …)</c> where the value is used — but
/// the composed result is the same, and this file asserts the composed result because that is what
/// the evaluator receives. The fifth is shaped differently: it clamps <c>spanTo</c> with a
/// <em>ceiling</em> at <c>windowTo</c> rather than a floor at <c>windowFrom</c>.
/// </para>
/// <para>
/// <strong>That difference is unreachable, and the unreachability is itself asserted here.</strong>
/// A ceiling at <c>windowTo</c> can only change an answer for a member whose last cohort cut falls
/// after the window ends. The cut dates are a closed set of five, the latest of which is the
/// <c>finalCut</c>; a member whose last cut equals the <c>finalCut</c> takes the surviving branch in
/// every one of the five and never reaches either clamp. So whenever <c>finalCut &lt;= windowTo</c>
/// the five are one rule, and <see cref="The_sealed_manifest_satisfies_the_precondition"/> pins that
/// the sealed manifest satisfies it. The divergent case is pinned too, in
/// <see cref="Outside_the_precondition_the_fifth_formulation_diverges_and_this_records_it"/>, so
/// that it is recorded rather than discovered later.
/// </para>
/// </remarks>
public sealed class MembershipSpanCharacterisationTests
{
    /// <summary>The sealed window, as the four harnesses spell it.</summary>
    private static readonly DateOnly WindowFrom = new(2021, 9, 1);

    /// <summary>The sealed window, as the four harnesses spell it.</summary>
    private static readonly DateOnly WindowTo = new(2026, 8, 31);

    /// <summary>
    /// Every cohort cut date the two sealed universe declarations contain, ascending.
    /// </summary>
    /// <remarks>
    /// Read from <c>declarations/universe-sample400-2021-2026.json</c> and
    /// <c>declarations/universe-400-2021-2026.json</c>, which carry the same five. The first
    /// precedes the window start by one day, which is the case the A9 floor acts on and the reason
    /// three members (BPYU, KIN, USCR) sit on that boundary.
    /// </remarks>
    private static readonly string[] CutDates =
    [
        "2021-08-31",
        "2022-08-31",
        "2023-08-31",
        "2024-08-31",
        "2025-08-31",
    ];

    /// <summary>
    /// The window these cases are measured against is the sealed one, not a local invention.
    /// </summary>
    [Fact]
    public void The_window_is_the_sealed_window()
    {
        var window = AcquisitionPlanning.Window();

        Assert.Equal(window.From, WindowFrom);
        Assert.Equal(window.To, WindowTo);
    }

    /// <summary>
    /// The precondition under which all five formulations are one rule, asserted against the
    /// sealed manifest rather than assumed.
    /// </summary>
    /// <remarks>
    /// The fifth formulation's ceiling at <c>windowTo</c> is the only clamp the other four do not
    /// have. It cannot fire while the latest cohort cut does not pass the window end, because the
    /// member holding that cut takes the surviving branch instead. This asserts the sealed data
    /// satisfies that, so the equivalence proved below is a statement about this repository and not
    /// about arithmetic in general.
    /// </remarks>
    [Fact]
    public void The_sealed_manifest_satisfies_the_precondition()
    {
        var finalCut = DateOnly.Parse(CutDates[^1], CultureInfo.InvariantCulture);

        Assert.True(finalCut <= WindowTo);

        foreach (var cut in CutDates)
        {
            Assert.True(DateOnly.Parse(cut, CultureInfo.InvariantCulture) <= WindowTo);
        }
    }

    /// <summary>
    /// The gate: the five formulations agree on every membership shape the sealed data can produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exhaustive rather than sampled. A member's cohort list is the ascending subset of the five
    /// cut dates at which it was a member, so all 32 subsets are enumerated, against each of the
    /// five cut dates in turn as the <c>finalCut</c> — 160 cases, which is the whole input space
    /// this rule has while the cut set is what it is.
    /// </para>
    /// <para>
    /// Any disagreement here is a finding about Gate 6's inputs, not a detail: it would mean the
    /// sealed 24 / 130 depends on which harness computed the span.
    /// </para>
    /// </remarks>
    [Fact]
    public void All_five_formulations_agree_on_every_reachable_membership_shape()
    {
        var cases = 0;

        foreach (var finalCut in CutDates)
        {
            for (var mask = 0; mask < 1 << 5; mask++)
            {
                var list = Subset(mask);

                var one = AsAcquisitionAuthorizationTests(list, finalCut, WindowFrom, WindowTo);
                var two = AsPostAcquisitionVerificationTests(list, finalCut, WindowFrom, WindowTo);
                var three = AsPostBatchReviewTests(list, finalCut, WindowFrom, WindowTo);
                var four = AsRecoveredSeriesMoveScreenTests(list, finalCut, WindowFrom, WindowTo);
                var five = AsAcquisitionDryRunTests(list, finalCut, WindowFrom, WindowTo);
                var extracted = AsMembershipSpan(list, finalCut, WindowFrom, WindowTo);

                var because = $"cohorts [{string.Join(' ', list)}], finalCut {finalCut}";

                Assert.True(one == two, $"site 1 vs 2: {one} != {two} for {because}");
                Assert.True(one == three, $"site 1 vs 3: {one} != {three} for {because}");
                Assert.True(one == four, $"site 1 vs 4: {one} != {four} for {because}");
                Assert.True(one == five, $"site 1 vs 5: {one} != {five} for {because}");

                // The extraction itself, held to the five it was taken from. This is what makes
                // re-pointing the sites a behaviour-preserving change rather than a claim.
                Assert.True(one == extracted, $"extracted vs sites: {extracted} != {one} for {because}");

                cases++;
            }
        }

        Assert.Equal(160, cases);
    }

    /// <summary>
    /// The three members whose only cut precedes the window start, pinned by value.
    /// </summary>
    /// <remarks>
    /// This is the boundary <c>CoverageEvaluationSpanBoundaryTests</c> describes and cannot reach:
    /// BPYU, KIN and USCR hold the single cut <c>2021-08-31</c>. The span the five produce for that
    /// shape is a crossed pair before clamping, and the clamp is what resolves it. Pinned as a
    /// literal so that a change to the clamp fails here with the value visible.
    /// </remarks>
    [Fact]
    public void The_single_cut_before_the_window_start_is_clamped_to_the_window_start()
    {
        List<string> list = ["2021-08-31"];

        var span = AsPostBatchReviewTests(list, "2025-08-31", WindowFrom, WindowTo);

        Assert.Equal(WindowFrom, span.From);
        Assert.Equal(WindowFrom, span.To);

        Assert.Equal(span, AsAcquisitionAuthorizationTests(list, "2025-08-31", WindowFrom, WindowTo));
        Assert.Equal(span, AsPostAcquisitionVerificationTests(list, "2025-08-31", WindowFrom, WindowTo));
        Assert.Equal(span, AsRecoveredSeriesMoveScreenTests(list, "2025-08-31", WindowFrom, WindowTo));
        Assert.Equal(span, AsAcquisitionDryRunTests(list, "2025-08-31", WindowFrom, WindowTo));
    }

    /// <summary>
    /// A member with no cohorts at all takes the whole window, in all five.
    /// </summary>
    [Fact]
    public void A_member_with_no_cohorts_spans_the_whole_window()
    {
        List<string> list = [];

        var span = AsPostBatchReviewTests(list, "2025-08-31", WindowFrom, WindowTo);

        Assert.Equal(WindowFrom, span.From);
        Assert.Equal(WindowTo, span.To);

        Assert.Equal(span, AsAcquisitionAuthorizationTests(list, "2025-08-31", WindowFrom, WindowTo));
        Assert.Equal(span, AsAcquisitionDryRunTests(list, "2025-08-31", WindowFrom, WindowTo));
    }

    /// <summary>
    /// A member still present at the final cut runs to the window end, in all five.
    /// </summary>
    [Fact]
    public void A_surviving_member_runs_to_the_window_end()
    {
        List<string> list = ["2021-08-31", "2022-08-31", "2023-08-31", "2024-08-31", "2025-08-31"];

        var span = AsPostBatchReviewTests(list, "2025-08-31", WindowFrom, WindowTo);

        Assert.Equal(WindowFrom, span.From);
        Assert.Equal(WindowTo, span.To);

        Assert.Equal(span, AsAcquisitionDryRunTests(list, "2025-08-31", WindowFrom, WindowTo));
    }

    /// <summary>
    /// A member that left before the final cut ends at its own last cut, in all five.
    /// </summary>
    [Fact]
    public void A_departed_member_ends_at_its_own_last_cut()
    {
        List<string> list = ["2021-08-31", "2022-08-31", "2023-08-31"];

        var span = AsPostBatchReviewTests(list, "2025-08-31", WindowFrom, WindowTo);

        Assert.Equal(WindowFrom, span.From);
        Assert.Equal(new DateOnly(2023, 8, 31), span.To);

        Assert.Equal(span, AsAcquisitionDryRunTests(list, "2025-08-31", WindowFrom, WindowTo));
    }

    /// <summary>
    /// Outside the precondition the fifth formulation genuinely differs — recorded, not corrected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If a cohort cut could fall after the window end, the fifth formulation's ceiling would clip a
    /// departed member's span to the window end while the other four would leave it at the cut. This
    /// pins that difference with both values rather than asserting either is right, because it is
    /// exactly the kind of thing that must not be discovered by accident later.
    /// </para>
    /// <para>
    /// <strong>The sealed data cannot produce this input</strong> — see
    /// <see cref="The_sealed_manifest_satisfies_the_precondition"/>. It is constructed here with a
    /// deliberately out-of-range cut, and reaching it in production would mean the cut set or the
    /// window had changed, which is a decision for the owner and not for this file.
    /// </para>
    /// </remarks>
    [Fact]
    public void Outside_the_precondition_the_fifth_formulation_diverges_and_this_records_it()
    {
        List<string> list = ["2022-08-31", "2027-01-29"];
        const string FinalCut = "2028-08-31";

        var four = AsPostBatchReviewTests(list, FinalCut, WindowFrom, WindowTo);
        var five = AsAcquisitionDryRunTests(list, FinalCut, WindowFrom, WindowTo);

        // The first four leave a departed member's end at its own cut, past the window end.
        Assert.Equal(new DateOnly(2027, 1, 29), four.To);

        // The fifth clips it to the window end.
        Assert.Equal(WindowTo, five.To);

        Assert.NotEqual(four, five);

        // Both agree on the start regardless.
        Assert.Equal(four.From, five.From);

        // Which side the extracted rule takes, stated rather than left to be inferred: the four
        // that feed the sealed 24 / 130 result.
        Assert.Equal(four, AsMembershipSpan(list, FinalCut, WindowFrom, WindowTo));
    }

    /// <summary>
    /// The extracted rule against the boundary values the four pinned cases above assert.
    /// </summary>
    [Fact]
    public void The_extracted_rule_reproduces_the_pinned_boundary_values()
    {
        const string FinalCut = "2025-08-31";

        Assert.Equal(
            (WindowFrom, WindowFrom),
            AsMembershipSpan(["2021-08-31"], FinalCut, WindowFrom, WindowTo));

        Assert.Equal(
            (WindowFrom, WindowTo),
            AsMembershipSpan([], FinalCut, WindowFrom, WindowTo));

        Assert.Equal(
            (WindowFrom, WindowTo),
            AsMembershipSpan(
                ["2021-08-31", "2022-08-31", "2023-08-31", "2024-08-31", "2025-08-31"],
                FinalCut, WindowFrom, WindowTo));

        Assert.Equal(
            (WindowFrom, new DateOnly(2023, 8, 31)),
            AsMembershipSpan(["2021-08-31", "2022-08-31", "2023-08-31"], FinalCut, WindowFrom, WindowTo));
    }

    /// <summary>
    /// The promoted rule, driven from the same string inputs the five formulations take.
    /// </summary>
    /// <remarks>
    /// The harnesses compare the last cut to the final cut as ordinal strings; the Domain type
    /// compares them as dates. For the ISO-8601 cut labels this repository uses the two orderings
    /// are the same, and the exhaustive comparison above is what establishes that rather than
    /// assuming it.
    /// </remarks>
    private static (DateOnly From, DateOnly To) AsMembershipSpan(
        List<string> list, string finalCut, DateOnly windowFrom, DateOnly windowTo) =>
        MembershipSpan.Derive(
            [.. list.Select(c => DateOnly.Parse(c, CultureInfo.InvariantCulture))],
            DateOnly.Parse(finalCut, CultureInfo.InvariantCulture),
            windowFrom,
            windowTo);

    /// <summary>The ascending cohort subset a bitmask selects.</summary>
    private static List<string> Subset(int mask)
    {
        var list = new List<string>();

        for (var i = 0; i < CutDates.Length; i++)
        {
            if ((mask & (1 << i)) != 0) { list.Add(CutDates[i]); }
        }

        return list;
    }

    // ---------------------------------------------------------------------------------------
    //  The five formulations, transcribed from their sites. Each is the code that is there now.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Site 1 — <c>AcquisitionAuthorizationTests</c>, construction at 610-631 composed with the
    /// window floor it applies at the call site, line 420.
    /// </summary>
    /// <remarks>
    /// This is the one site that stores <c>spanFrom</c> unfloored and floors it where it is used.
    /// The composition is what the evaluator receives, so the composition is what is compared.
    /// </remarks>
    private static (DateOnly From, DateOnly To) AsAcquisitionAuthorizationTests(
        List<string> list, string finalCut, DateOnly windowFrom, DateOnly windowTo)
    {
        var survives = list.Count > 0 &&
            string.Equals(list[^1], finalCut, StringComparison.Ordinal);

        var spanFrom = list.Count == 0
            ? windowFrom
            : DateOnly.Parse(list[0], CultureInfo.InvariantCulture);

        var spanTo = list.Count == 0 || survives
            ? windowTo
            : DateOnly.Parse(list[^1], CultureInfo.InvariantCulture);

        // Stored: SpanFrom unfloored, SpanTo floored.
        var storedFrom = spanFrom;
        var storedTo = spanTo < windowFrom ? windowFrom : spanTo;

        // Applied at the call site: Later(windowFrom, member.SpanFrom), member.SpanTo.
        return (Later(windowFrom, storedFrom), storedTo);
    }

    /// <summary>Site 2 — <c>PostAcquisitionVerificationTests</c>, 536-559.</summary>
    private static (DateOnly From, DateOnly To) AsPostAcquisitionVerificationTests(
        List<string> list, string finalCut, DateOnly windowFrom, DateOnly windowTo)
    {
        var survives = list.Count > 0 &&
            string.Equals(list[^1], finalCut, StringComparison.Ordinal);

        var spanFrom = list.Count == 0
            ? windowFrom
            : DateOnly.Parse(list[0], CultureInfo.InvariantCulture);

        var spanTo = list.Count == 0 || survives
            ? windowTo
            : DateOnly.Parse(list[^1], CultureInfo.InvariantCulture);

        return (
            spanFrom < windowFrom ? windowFrom : spanFrom,
            spanTo < windowFrom ? windowFrom : spanTo);
    }

    /// <summary>Site 3 — <c>PostBatchReviewTests</c>, 1279-1301.</summary>
    private static (DateOnly From, DateOnly To) AsPostBatchReviewTests(
        List<string> list, string finalCut, DateOnly windowFrom, DateOnly windowTo)
    {
        var survives = list.Count > 0 &&
            string.Equals(list[^1], finalCut, StringComparison.Ordinal);

        var spanFrom = list.Count == 0
            ? windowFrom
            : DateOnly.Parse(list[0], CultureInfo.InvariantCulture);

        var spanTo = list.Count == 0 || survives
            ? windowTo
            : DateOnly.Parse(list[^1], CultureInfo.InvariantCulture);

        return (
            spanFrom < windowFrom ? windowFrom : spanFrom,
            spanTo < windowFrom ? windowFrom : spanTo);
    }

    /// <summary>Site 4 — <c>RecoveredSeriesMoveScreenTests</c>, 1109-1128.</summary>
    private static (DateOnly From, DateOnly To) AsRecoveredSeriesMoveScreenTests(
        List<string> list, string finalCut, DateOnly windowFrom, DateOnly windowTo)
    {
        var survives = list.Count > 0 &&
            string.Equals(list[^1], finalCut, StringComparison.Ordinal);

        var spanFrom = list.Count == 0
            ? windowFrom
            : DateOnly.Parse(list[0], CultureInfo.InvariantCulture);

        var spanTo = list.Count == 0 || survives
            ? windowTo
            : DateOnly.Parse(list[^1], CultureInfo.InvariantCulture);

        return (
            spanFrom < windowFrom ? windowFrom : spanFrom,
            spanTo < windowFrom ? windowFrom : spanTo);
    }

    /// <summary>
    /// Site 5 — <c>AcquisitionDryRunTests.Span</c>, 403-418, over the member fields its own
    /// <c>MembersAsync</c> builds at 386-398.
    /// </summary>
    private static (DateOnly From, DateOnly To) AsAcquisitionDryRunTests(
        List<string> list, string finalCut, DateOnly windowFrom, DateOnly windowTo)
    {
        // Built at 386-398: FirstCohort, LastCohort, SurvivesToWindowEnd.
        var firstCohort = list.Count == 0 ? null : list[0];
        var lastCohort = list.Count == 0 ? null : list[^1];
        var survivesToWindowEnd = list.Count > 0 &&
            string.Equals(list[^1], finalCut, StringComparison.Ordinal);

        // Span(member), 403-418.
        var from = firstCohort is null
            ? windowFrom
            : Later(windowFrom, DateOnly.Parse(firstCohort, CultureInfo.InvariantCulture));

        var to = lastCohort is null || survivesToWindowEnd
            ? windowTo
            : Earlier(windowTo, DateOnly.Parse(lastCohort, CultureInfo.InvariantCulture));

        return (from, to < from ? from : to);
    }

    private static DateOnly Later(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Earlier(DateOnly a, DateOnly b) => a < b ? a : b;
}
