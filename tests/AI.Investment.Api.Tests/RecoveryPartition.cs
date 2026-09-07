namespace AI.Investment.Api.Tests;

/// <summary>
/// The four buckets every unrecovered member falls into, and the arithmetic over them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The partition is the deliverable, not the recovery rate.</strong> A recovery that
/// "succeeds" by quietly turning the companies it could not identify into unmatched rows would
/// hide exactly what the sealed universe exists to preserve. So the rule here is total and
/// disjoint: every member lands in exactly one bucket, the buckets sum to the membership, and a
/// transport failure is never allowed to become an absence of evidence.
/// </para>
/// <para>
/// The distinction that took twenty probe filings to establish is between <see cref="NoListedSecurity"/>
/// and <see cref="Unresolved"/>. A company whose cover page says it has no securities registered
/// under Section 12(b) had no ticker to recover; a company whose filing simply does not say is a
/// different fact. Collapsing the two would make the method look worse than it is in one direction
/// and better in the other, depending which way the collapse went.
/// </para>
/// </remarks>
internal static class RecoveryPartition
{
    /// <summary>The issuer's own filing named a ticker.</summary>
    public const string Recovered = "recovered";

    /// <summary>The cover page states no Section 12(b) security. Nothing to recover.</summary>
    public const string NoListedSecurity = "no-listed-security";

    /// <summary>No ticker, and the filing does not settle whether there was one.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>The request never reached SEC. Not evidence about the filing.</summary>
    public const string TransportFailed = "transport-failed";

    /// <summary>Every bucket, in report order.</summary>
    public static readonly string[] All =
        [Recovered, NoListedSecurity, Unresolved, TransportFailed];

    /// <summary>Whether a recovered identity may be added to the acquisition-ready set.</summary>
    /// <remarks>
    /// Only the first bucket. A member with no listed security is not acquirable - there is
    /// nothing to price - and the other two are not identified at all. None of them is dropped.
    /// </remarks>
    public static bool IsAcquirable(string outcome) =>
        string.Equals(outcome, Recovered, StringComparison.Ordinal);

    /// <summary>Whether the outcome is a statement about the filing rather than the network.</summary>
    public static bool IsEvidence(string outcome) =>
        !string.Equals(outcome, TransportFailed, StringComparison.Ordinal);

    /// <summary>
    /// Checks the partition is total and disjoint against the membership it claims to cover.
    /// </summary>
    /// <remarks>
    /// Cheap, and the one invariant that must never bend: if these do not sum, a member has been
    /// lost or counted twice, and either is worse than any recovery rate is good.
    /// </remarks>
    public static bool Covers(
        int members,
        int recovered,
        int noListedSecurity,
        int unresolved,
        int transportFailed) =>
        members == recovered + noListedSecurity + unresolved + transportFailed;

    /// <summary>
    /// The survivorship share of a panel, as gate 2 measures it.
    /// </summary>
    public static decimal PanelDropoutShare(int panelMembers, int panelDropouts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(panelDropouts);

        return panelMembers <= 0 ? 0m : (decimal)panelDropouts / panelMembers;
    }

    /// <summary>Whether a panel clears gate 2's unchanged five per cent floor.</summary>
    public static bool ClearsGate2(int panelMembers, int panelDropouts) =>
        PanelDropoutShare(panelMembers, panelDropouts) >= AcquisitionPlanning.SurvivorshipFloor;
}
