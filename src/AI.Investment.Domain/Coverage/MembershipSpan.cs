namespace AI.Investment.Domain.Coverage;

/// <summary>
/// The span a member is judged over: its own membership, clipped to the window.
/// </summary>
/// <remarks>
/// <para>
/// This decides the two dates <see cref="CoverageEvaluation.Evaluate"/> measures against, and it is
/// therefore half of Gate 6's answer. A member judged over the whole window instead of over its own
/// membership is reported as missing every session it could not have had, which is the failure mode
/// the sealed rule exists to avoid: <em>"a member's own membership span as the expectation"</em>.
/// </para>
/// <para>
/// <strong>This is an extraction, not a new rule.</strong> The derivation existed five times, inside
/// private member-construction lambdas in four test harnesses and as a fifth, differently shaped
/// formulation in a fifth. All five were transcribed and held against one another across every
/// membership shape the sealed cut set can produce before a line of this file was written; they
/// agreed, and <c>MembershipSpanCharacterisationTests</c> is that proof and continues to hold this
/// type to all five. Nothing here was chosen — it is what those five already did.
/// </para>
/// <para>
/// <strong>Why the clamp is a floor and not a range.</strong> Both ends are raised to
/// <paramref name="windowFrom"/> and neither is lowered to <paramref name="windowTo"/>. That looks
/// asymmetric, and it is: a cut before the window start is real — the earliest cohort cut in the
/// sealed universe is the day before the window opens, and three members hold it as their only one —
/// whereas a cut after the window end cannot occur while the final cut is the latest cut there is,
/// because the member holding it takes the surviving branch instead. The fifth formulation carried a
/// ceiling for the case that cannot arise; the characterisation records that difference rather than
/// hiding it, and this type follows the four that feed the sealed result.
/// </para>
/// <para>
/// <strong>Pure and total.</strong> No clock, no culture, no I/O, no state. The same inputs give the
/// same span on any machine at any time, which is what lets a coverage verdict be re-derived years
/// later and argued about.
/// </para>
/// </remarks>
public static class MembershipSpan
{
    /// <summary>
    /// Derives the span to judge one member over.
    /// </summary>
    /// <param name="cohorts">
    /// The cohort cut dates at which this member was in the universe, <strong>ascending</strong>.
    /// Only the first and last are read, so the order is load-bearing and is the caller's
    /// responsibility — every current caller sorts before calling. Empty means the member was never
    /// cut into a cohort, which is not the same as never having been a member.
    /// </param>
    /// <param name="finalCut">
    /// The latest cut date in the universe. A member whose last cut is this one had not left when
    /// the cutting stopped, so its span runs to the window end rather than to its last cut.
    /// </param>
    /// <param name="windowFrom">The first day of the sealed window.</param>
    /// <param name="windowTo">The last day of the sealed window.</param>
    /// <returns>
    /// The inclusive span. <c>To</c> may precede <c>From</c> only if the window itself is inverted;
    /// for a member whose cuts all precede the window, both collapse onto
    /// <paramref name="windowFrom"/>.
    /// </returns>
    public static (DateOnly From, DateOnly To) Derive(
        IReadOnlyList<DateOnly> cohorts,
        DateOnly finalCut,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(cohorts);

        // Still in the universe when the cutting stopped: its series is expected to run on.
        var survives = cohorts.Count > 0 && cohorts[^1] == finalCut;

        var spanFrom = cohorts.Count == 0 ? windowFrom : cohorts[0];

        var spanTo = cohorts.Count == 0 || survives ? windowTo : cohorts[^1];

        // The floor, on both ends. A cut before the window opens is evidence, not an error.
        return (
            spanFrom < windowFrom ? windowFrom : spanFrom,
            spanTo < windowFrom ? windowFrom : spanTo);
    }
}
