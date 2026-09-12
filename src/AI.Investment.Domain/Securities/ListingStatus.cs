namespace AI.Investment.Domain.Securities;

/// <summary>
/// What a venue says about a security's listing ON THAT VENUE, and never about the market.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no unqualified "delisted" here, and that omission is the point.</strong> A
/// statement about one venue is evidence about that venue; it is never evidence about all venues.
/// The repository proved this empirically rather than assuming it: SHPW filed a 25-NSE on
/// 2024-09-06 and then traded for 339 further days, to 2025-08-11. A status that could be read as
/// market-wide would have turned that filing into a cessation.
/// </para>
/// <para>
/// <see cref="Removed"/> therefore means removed <em>from this venue</em>. Deciding that a security
/// has stopped trading anywhere needs a venue set and a completeness assertion over it, neither of
/// which exists, so the concept is absent rather than approximated.
/// </para>
/// </remarks>
public enum ListingStatus
{
    /// <summary>
    /// No listing statement has been observed for this venue at the requested time.
    /// </summary>
    /// <remarks>
    /// The default, deliberately, and it means "nothing is known" rather than "not listed". A point
    /// in time before the first event is a gap in the evidence, and a gap must not read as an
    /// answer.
    /// </remarks>
    Unknown = 0,

    /// <summary>Admitted to trading on this venue.</summary>
    Listed = 1,

    /// <summary>Trading halted on this venue, with the listing intact.</summary>
    Suspended = 2,

    /// <summary>Removed from this venue. Says nothing about any other venue.</summary>
    Removed = 3,
}
