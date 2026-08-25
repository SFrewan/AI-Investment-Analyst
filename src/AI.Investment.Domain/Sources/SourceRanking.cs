namespace AI.Investment.Domain.Sources;

/// <summary>
/// Orders sources deterministically when more than one reports the same thing.
/// </summary>
/// <remarks>
/// <para>
/// Pure, total and deterministic - the same properties the policy engine has, for the same
/// reason: "which source do we believe?" must be answerable identically every time, and
/// reconstructable months later when someone asks why the system preferred one figure over
/// another.
/// </para>
/// <para><strong>What this deliberately does NOT do.</strong> It does not resolve conflicts. It
/// orders sources by standing; deciding what to do when two of them state different numbers is a
/// separate problem needing real conflicting data to study, and a clever resolver written before
/// that data exists would be guessing. What it provides is the foundation such a resolver needs:
/// a stable, explainable ordering.</para>
/// <para>The ordering, in priority order:</para>
/// <list type="number">
/// <item><strong>Authority.</strong> The originating record outranks a report of it, which
/// outranks an unassessed one. This dominates everything else - a fresher secondary source does
/// not beat the filing it is summarising.</item>
/// <item><strong>Self-sufficiency.</strong> Among equals, a source that may confirm alone
/// outranks one that needs corroboration.</item>
/// <item><strong>Measured reliability.</strong> What the source has actually done. Below
/// authority because it is often <see cref="ReliabilityGrade.Unrated"/> and must not let an
/// unmeasured primary source lose to a well-scored aggregator.</item>
/// <item><strong>Region specificity.</strong> A source scoped to the market in question outranks
/// a global one for that market.</item>
/// <item><strong>Identifier.</strong> A final tie-break so the order is total and stable rather
/// than dependent on enumeration order.</item>
/// </list>
/// <para>
/// Recency is deliberately absent. It is a property of an observation, not of a source, and it
/// belongs to whatever compares two claims - not here.
/// </para>
/// </remarks>
public sealed class SourceRanking : IComparer<DataSource>
{
    /// <summary>Shared instance. The comparer is stateless.</summary>
    public static SourceRanking Instance { get; } = new();

    /// <summary>
    /// Compares standing. Returns a positive number when <paramref name="x"/> outranks
    /// <paramref name="y"/>, so a descending sort puts the most authoritative source first.
    /// </summary>
    public int Compare(DataSource? x, DataSource? y)
    {
        // A missing source ranks below any real one. Total ordering includes null.
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byAuthority = x.Authority.CompareTo(y.Authority);

        if (byAuthority != 0)
        {
            return byAuthority;
        }

        var bySelfSufficiency = x.Verification.CanConfirmAlone.CompareTo(y.Verification.CanConfirmAlone);

        if (bySelfSufficiency != 0)
        {
            return bySelfSufficiency;
        }

        var byReliability = x.Reliability.CompareTo(y.Reliability);

        if (byReliability != 0)
        {
            return byReliability;
        }

        // A source scoped to one market knows it better than a global one. Non-global outranks
        // global, and two non-global sources are equally specific.
        var bySpecificity = (!x.Region.IsGlobal).CompareTo(!y.Region.IsGlobal);

        if (bySpecificity != 0)
        {
            return bySpecificity;
        }

        // Descending order by rank means the identifier tie-break must be inverted to read
        // ascending alphabetically once the sort is reversed.
        return -string.CompareOrdinal(x.Id.Value, y.Id.Value);
    }

    /// <summary>
    /// Returns the supplied sources in descending order of standing - most authoritative first.
    /// </summary>
    public static IReadOnlyList<DataSource> MostAuthoritativeFirst(IEnumerable<DataSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var ordered = sources.Where(s => s is not null).ToList();
        ordered.Sort((left, right) => Instance.Compare(right, left));

        return ordered;
    }

    /// <summary>
    /// The single most authoritative source, or null when none were supplied.
    /// </summary>
    /// <remarks>
    /// Indexes the ordered list rather than calling <c>FirstOrDefault()</c> on it. The list is
    /// indexable, so the LINQ call would walk an enumerator to reach an element already
    /// addressable directly (CA1826). Deriving this from <see cref="MostAuthoritativeFirst"/>
    /// rather than scanning for a maximum keeps one definition of the ordering, tie-breaks
    /// included.
    /// </remarks>
    public static DataSource? MostAuthoritative(IEnumerable<DataSource> sources)
    {
        var ordered = MostAuthoritativeFirst(sources);

        return ordered.Count > 0 ? ordered[0] : null;
    }
}
