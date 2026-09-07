namespace AI.Investment.Api.Tests;

/// <summary>
/// Gate 6's sealed rule, made executable.
/// </summary>
/// <remarks>
/// <para>
/// The rule was pre-registered in <c>declarations/coverage-gate6.json</c> before any price existed
/// to fit it to, and this is that rule and nothing else: zero faults, a member's own membership
/// span as the expectation, interior gaps of four sessions or fewer legitimate, longer ones faults,
/// no observations for an acquirable symbol a fault, and <strong>a short series never a fault on
/// its own</strong>.
/// </para>
/// <para>
/// That last clause is the one that carries the weight. Every other coverage rule anyone would
/// reach for penalises short series, and the members with short series are precisely the companies
/// that were acquired, delisted or failed - the ones this universe was built from frames to
/// contain, and the ones the identity recovery spent 129 free calls putting back. A gate that
/// discarded them would undo all of it while reporting green.
/// </para>
/// <para>
/// <strong>Sessions are counted as weekdays.</strong> The platform holds no exchange calendar and
/// will not invent one, so a holiday is indistinguishable from a missing day. That is exactly why
/// the interior tolerance exists: four is longer than any US holiday run, so a gap inside it cannot
/// be a fault, and a gap beyond it cannot be a holiday.
/// </para>
/// </remarks>
internal static class CoverageEvaluation
{
    /// <summary>Consecutive expected sessions an interior gap may span and stay legitimate.</summary>
    public const int InteriorGapTolerance = 4;

    /// <summary>Sessions either side of a cohort boundary a series may legitimately miss.</summary>
    public const int CohortTolerance = 10;

    /// <summary>The gate passes on zero faults. Not a percentage.</summary>
    public const int FaultThreshold = 0;

    /// <summary>A member with a symbol and no series at all.</summary>
    public const string NoSeries = "no-series";

    /// <summary>A hole in the middle of a series that no holiday run explains.</summary>
    public const string InteriorGap = "interior-gap";

    /// <summary>A series refused as an unexplained discontinuity - a spin-off or special dividend.</summary>
    public const string UnexplainedDiscontinuity = "unexplained-discontinuity";

    /// <summary>
    /// Acquirable, empty, and never asked for. A stage that has not run, not a hole in the data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a fault, and just as deliberately not silence. Before the acquisition runs,
    /// every acquirable member holds nothing, and calling that a coverage fault says only that the
    /// stage is pending - which is true of the whole universe and tells an operator nothing about
    /// the data. What matters is the other case: a symbol that <em>was</em> requested, whose request
    /// succeeded, and which still holds no prices. That is a hole, and it was previously reported in
    /// the same breath as three hundred and fifty-two pending members, where nobody would find it.
    /// </para>
    /// <para>
    /// <strong>The safeguard is the point of the distinction, not an afterthought.</strong> Excusing
    /// "never asked for" is only honest while nobody claims to have asked. Once an acquisition stage
    /// declares itself complete, a member still carrying this status means the stage skipped it, and
    /// <see cref="Passes(int, int, bool)"/> fails the gate on exactly that. The verdict is counted
    /// and reported either way, so it can never be quietly excluded.
    /// </para>
    /// </remarks>
    public const string NotYetAcquired = "not-yet-acquired";

    /// <summary>
    /// Judges one member's price series against its own membership span.
    /// </summary>
    /// <param name="acquirable">Whether this member was expected to have a series at all.</param>
    /// <param name="sessions">The trading dates held, in any order; duplicates are ignored.</param>
    /// <param name="spanFrom">The later of the window start and the member's first cohort.</param>
    /// <param name="spanTo">The earlier of the window end and the member's last cohort.</param>
    /// <param name="refusedAsDiscontinuous">The series was refused by the split adjuster.</param>
    /// <param name="wasRequested">
    /// Whether a request for this member's symbol has succeeded against the price provider.
    /// Defaults to <see langword="true"/> so that every existing caller keeps the behaviour it
    /// had: an empty series is a fault unless something states positively that it was never asked
    /// for. The permissive reading has to be argued for at the call site, never assumed here.
    /// </param>
    public static Verdict Evaluate(
        bool acquirable,
        IEnumerable<DateOnly> sessions,
        DateOnly spanFrom,
        DateOnly spanTo,
        bool refusedAsDiscontinuous = false,
        bool wasRequested = true)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var held = sessions.Distinct().OrderBy(d => d).ToList();

        if (!acquirable)
        {
            // Not expected to have a series, so its absence says nothing. A member excluded from
            // acquisition is still a member; it is simply not what this gate measures.
            return new Verdict(false, [], held.Count, 0, "not acquirable; no series expected");
        }

        if (refusedAsDiscontinuous)
        {
            return new Verdict(
                true,
                [UnexplainedDiscontinuity],
                held.Count,
                0,
                "the series was refused as an unexplained discontinuity, which the sealed rule "
                + "records as a coverage event and counts as a fault rather than dropping quietly");
        }

        if (held.Count == 0)
        {
            // Ordered so the permissive branch cannot be reached by accident: a refusal is judged
            // above, and only an untouched symbol - never asked for, never answered - lands here.
            return wasRequested
                ? new Verdict(
                    true,
                    [NoSeries],
                    0,
                    ExpectedSessions(spanFrom, spanTo),
                    "acquirable, a request for it succeeded, and no closing price is held for any "
                    + "session in its membership span")
                : new Verdict(
                    false,
                    [],
                    0,
                    ExpectedSessions(spanFrom, spanTo),
                    "acquirable and empty, but no request for this symbol has succeeded, so the "
                    + "acquisition has not run for it rather than run and come back empty",
                    NotYetAcquired: true);
        }

        var expected = ExpectedSessions(spanFrom, spanTo);
        var faults = new List<string>();
        var worst = 0;

        // Interior gaps only: bounded by an observation on both sides. What happens before the
        // first observation or after the last is the member's listing or its death, and the
        // tolerances below judge those, not this.
        for (var i = 1; i < held.Count; i++)
        {
            var missing = Weekdays(held[i - 1].AddDays(1), held[i].AddDays(-1));

            if (missing > worst)
            {
                worst = missing;
            }

            if (missing > InteriorGapTolerance)
            {
                faults.Add(InteriorGap);
            }
        }

        var reason = faults.Count == 0
            ? worst == 0
                ? "complete across its membership span"
                : $"the longest interior gap is {worst} session(s), within the {InteriorGapTolerance} a holiday run can explain"
            : $"an interior gap of {worst} session(s) exceeds the {InteriorGapTolerance} a holiday run can explain";

        return new Verdict(faults.Count > 0, faults, held.Count, expected, reason);
    }

    /// <summary>Whether a series ending early is explained by the member leaving the universe.</summary>
    /// <remarks>
    /// A company acquired in 2023 stops having prices in 2023. That is the evidence, not a hole in
    /// it. The tolerance is generous because a delisting and its last trade rarely fall on the
    /// same day.
    /// </remarks>
    public static bool EndsLegitimately(DateOnly lastSession, DateOnly spanTo) =>
        Weekdays(lastSession.AddDays(1), spanTo) <= CohortTolerance;

    /// <summary>Whether a series starting late is explained by the member joining later.</summary>
    public static bool StartsLegitimately(DateOnly firstSession, DateOnly spanFrom) =>
        Weekdays(spanFrom, firstSession.AddDays(-1)) <= CohortTolerance;

    /// <summary>The gate's verdict over every member. Zero faults, or it fails.</summary>
    /// <remarks>
    /// Unchanged, and kept as its own overload rather than given a default, so that no existing
    /// caller silently acquires an opinion about whether the acquisition has finished.
    /// </remarks>
    public static bool Passes(int totalFaults) => totalFaults <= FaultThreshold;

    /// <summary>
    /// The same zero-fault verdict, plus the safeguard that makes the pending status honest.
    /// </summary>
    /// <param name="totalFaults">Faults across every member. Zero, or the gate fails.</param>
    /// <param name="notYetAcquired">Members acquirable, empty, and never successfully requested.</param>
    /// <param name="acquisitionComplete">Whether an acquisition stage has declared itself finished.</param>
    /// <remarks>
    /// The threshold has not moved: zero faults is still the only passing fault count. What this
    /// adds is that once acquisition claims to be complete, a member nobody ever asked for is no
    /// longer pending - it was skipped, and a gate that passed over it would be certifying coverage
    /// it never looked for.
    /// </remarks>
    public static bool Passes(int totalFaults, int notYetAcquired, bool acquisitionComplete)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(notYetAcquired);

        return Passes(totalFaults) && !(acquisitionComplete && notYetAcquired > 0);
    }

    /// <summary>Weekdays in an inclusive range. The platform's only notion of a session.</summary>
    public static int ExpectedSessions(DateOnly from, DateOnly to) => Weekdays(from, to);

    private static int Weekdays(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return 0;
        }

        var count = 0;

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                count++;
            }
        }

        return count;
    }

    /// <param name="IsFault">Whether this member contributes a fault to the gate.</param>
    /// <param name="Faults">Which faults, by name. Empty when none.</param>
    /// <param name="Sessions">Distinct trading dates held.</param>
    /// <param name="Expected">Weekdays in the member's own membership span.</param>
    /// <param name="Reason">Why, in the words an operator reads.</param>
    /// <param name="NotYetAcquired">
    /// Acquirable, empty, and never successfully requested. Never a fault on its own, and never
    /// invisible either: the count has to be reported, and it fails the gate the moment an
    /// acquisition stage claims to have finished.
    /// </param>
    internal sealed record Verdict(
        bool IsFault,
        IReadOnlyList<string> Faults,
        int Sessions,
        int Expected,
        string Reason,
        bool NotYetAcquired = false);
}
