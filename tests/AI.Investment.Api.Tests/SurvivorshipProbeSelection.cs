using System.Globalization;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Which ten members the widened probe reads, and what a result of k out of n is worth.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Frozen before anything is read.</strong> The rule is arithmetic over facts that were
/// already on disk before this stage existed - a member's death year and its identity status - and
/// it contains no threshold, no score and nothing that could be tuned after seeing a filing. That
/// matters more here than usual: a probe whose sample is chosen after glancing at the answers
/// measures the chooser, not the method.
/// </para>
/// <para>
/// <strong>Round-robin across strata, not the first ten.</strong> The 110 unpriceable dropouts
/// differ in two ways that plausibly affect whether a filing tagged its cover page: the year the
/// company stopped filing (the cover-page XBRL rule phased in by filer size through 2021) and the
/// identity category it fell into. Taking the first ten of any ordering would ask one question ten
/// times. Rotating through the strata asks it across the range, which is the only way ten filings
/// can say anything about a hundred and ten.
/// </para>
/// </remarks>
internal static class SurvivorshipProbeSelection
{
    /// <summary>The authorised ceiling, and the sample size.</summary>
    public const int Size = 10;

    /// <summary>
    /// The ten to read, chosen by rotating through (death year, identity status) strata.
    /// </summary>
    /// <remarks>
    /// Strata are ordered by year then status, both ordinal; members inside a stratum by CIK,
    /// ordinal. One is taken from each stratum in turn, and the rotation repeats until ten are
    /// chosen or the candidates run out. Every comparison is ordinal so the answer does not depend
    /// on the machine's locale, and the whole function is deterministic: the same candidate list
    /// yields the same ten, today and in a year.
    /// </remarks>
    public static List<Candidate> Choose(IEnumerable<Candidate> candidates, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        var strata = candidates
            .GroupBy(c => c.Stratum, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(c => c.Cik, StringComparer.Ordinal).ToList())
            .ToList();

        var chosen = new List<Candidate>(size);

        for (var round = 0; chosen.Count < size; round++)
        {
            var takenThisRound = 0;

            foreach (var stratum in strata)
            {
                if (chosen.Count >= size)
                {
                    break;
                }

                if (round >= stratum.Count)
                {
                    continue;
                }

                chosen.Add(stratum[round]);
                takenThisRound++;
            }

            // Every stratum is exhausted: there are simply fewer candidates than asked for.
            if (takenThisRound == 0)
            {
                break;
            }
        }

        return chosen;
    }

    /// <summary>
    /// The recovery rate, over readable filings only.
    /// </summary>
    /// <remarks>
    /// <strong>Transport failures are excluded from the denominator, deliberately.</strong> A
    /// request that never reached SEC says nothing about whether the filing tagged its cover page,
    /// and counting it either way would move the rate for a reason that has nothing to do with the
    /// question. It is reported separately instead.
    /// </remarks>
    public static decimal Rate(int recovered, int provenAbsent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recovered);
        ArgumentOutOfRangeException.ThrowIfNegative(provenAbsent);

        var readable = recovered + provenAbsent;

        return readable == 0 ? 0m : (decimal)recovered / readable;
    }

    /// <summary>
    /// A 95% Wilson interval on that rate, so the smallness of the sample is visible.
    /// </summary>
    /// <remarks>
    /// Wilson rather than the textbook normal interval, because at these sample sizes the normal
    /// one produces bounds outside [0,1] and is simply wrong. The interval is the honest part of
    /// the answer: ten filings cannot pin a rate, and a report that gave a bare percentage would
    /// invite a decision the evidence does not support.
    /// </remarks>
    public static (double Low, double High) Interval(int recovered, int readable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recovered);

        if (readable <= 0)
        {
            return (0d, 1d);
        }

        const double z = 1.959963984540054;

        var n = (double)readable;
        var p = recovered / n;
        var denominator = 1d + (z * z / n);
        var centre = (p + (z * z / (2d * n))) / denominator;
        var half = z / denominator * Math.Sqrt((p * (1d - p) / n) + (z * z / (4d * n * n)));

        return (Math.Max(0d, centre - half), Math.Min(1d, centre + half));
    }

    /// <summary>
    /// Whether a result justifies committing to the full recovery, stated before it is seen.
    /// </summary>
    /// <remarks>
    /// Pre-registered so the bar cannot move once the number is known. A full recovery is worth
    /// authorising only if the lower bound of the interval clears one half - that is, only if the
    /// evidence supports "more often than not" rather than merely permitting it. Anything less
    /// leaves the enlarged panel's survivorship properties unmeasured, which the standing rule
    /// treats as worse than an honest gap.
    /// </remarks>
    public static bool JustifiesFullRecovery(int recovered, int readable) =>
        readable >= 8 && Interval(recovered, readable).Low > 0.5d;

    /// <summary>One member of the 110, with the two facts the strata are built from.</summary>
    /// <param name="Cik">The member.</param>
    /// <param name="Name">Its SEC name, for the report.</param>
    /// <param name="LastCohort">The last cohort cut it appeared in - when it stopped filing.</param>
    /// <param name="Status">Its identity category: provisional, ambiguous or unmatched.</param>
    /// <param name="Accession">The annual filing to read, taken from the archive.</param>
    /// <param name="Form">That filing's form type.</param>
    /// <param name="FilingDate">When it was filed.</param>
    /// <param name="Document">The filing's primary document.</param>
    internal sealed record Candidate(
        string Cik,
        string Name,
        string LastCohort,
        string Status,
        string Accession,
        string Form,
        string FilingDate,
        string Document)
    {
        /// <summary>Death year and identity category, which is what the rotation spreads across.</summary>
        public string Stratum => string.Create(
            CultureInfo.InvariantCulture,
            $"{(LastCohort.Length >= 4 ? LastCohort[..4] : "0000")}|{Status}");
    }
}
