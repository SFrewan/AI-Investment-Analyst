using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Securities;

/// <summary>
/// The relationship between one security and one venue, and the history of what it did.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no current-status column here, deliberately.</strong> A listing is identified by
/// the pair it joins and is otherwise nothing but its <see cref="ListingEvent"/> series. Current
/// state is a projection of that history - <see cref="StatusAsAt"/> with today's date - never a
/// stored field, because a stored field is the thing that would have to be overwritten, and
/// overwriting is exactly what loses the past.
/// </para>
/// <para>
/// <strong>Per venue, and never generalised.</strong> One security may hold several listings at once
/// with different statuses. Nothing on this type reports across venues, because no source can
/// support such a statement: a market-wide claim needs a venue set and a completeness assertion over
/// it, and neither exists.
/// </para>
/// </remarks>
public sealed class Listing
{
    private readonly List<ListingEvent> _events = [];

    private Listing(SecurityId securityId, VenueId venueId, DateTime openedAtUtc)
    {
        SecurityId = securityId;
        VenueId = venueId;
        OpenedAtUtc = openedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Listing() => VenueId = null!;

    /// <summary>One half of the identity. Never changes.</summary>
    public SecurityId SecurityId { get; private set; }

    /// <summary>The other half. Never changes.</summary>
    public VenueId VenueId { get; private set; }

    /// <summary>When this pairing was first recorded here.</summary>
    public DateTime OpenedAtUtc { get; private set; }

    /// <summary>The transitions observed for this pairing, in insertion order.</summary>
    public IReadOnlyList<ListingEvent> Events => _events;

    public static Listing Open(SecurityId securityId, VenueId venueId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(venueId);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (securityId.Value == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(securityId),
                "A listing must name the security it is for.");
        }

        return new Listing(securityId, venueId, nowUtc);
    }

    /// <summary>
    /// Appends a transition. Append-only: there is no operation that edits or removes one.
    /// </summary>
    /// <remarks>
    /// The event must be about this exact pairing. An event filed against the wrong listing would
    /// attribute a venue's statement to a venue that never made it, which is the precise error the
    /// venue-qualification rule exists to prevent.
    /// </remarks>
    public void Append(ListingEvent listingEvent)
    {
        ArgumentNullException.ThrowIfNull(listingEvent);

        if (listingEvent.SecurityId != SecurityId || listingEvent.VenueId != VenueId)
        {
            throw new DomainRuleViolationException(
                "Listing.EventBelongsToThisPairing",
                $"A listing event for ({listingEvent.SecurityId}, {listingEvent.VenueId}) cannot be "
                + $"appended to the listing for ({SecurityId}, {VenueId}).");
        }

        _events.Add(listingEvent);
    }

    /// <summary>
    /// The status on this venue as at <paramref name="asOf"/>, using only what was knowable by
    /// <paramref name="knownAsOfUtc"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two instants, and both are load-bearing.</strong> <paramref name="asOf"/> selects
    /// which transitions have taken effect; <paramref name="knownAsOfUtc"/> selects which statements
    /// had been published. Collapsing them into one would answer "what do we now believe was true
    /// then", which is a different and much more flattering question than "what could have been
    /// known then" - and it is the question a backtest must never be allowed to ask.
    /// </para>
    /// <para>
    /// Ordering is by effective date, then by publication, then by the recording instant. The second
    /// key settles two statements about the same effective date by preferring the later one to be
    /// published, which is the correction; the third only makes the result deterministic when a
    /// source published two statements at the same instant.
    /// </para>
    /// <para>
    /// <see cref="ListingStatus.Unknown"/> when no qualifying event exists. That is the honest
    /// answer for a date before the first statement, and it is not <see cref="ListingStatus.Listed"/>
    /// and not <see cref="ListingStatus.Removed"/>.
    /// </para>
    /// </remarks>
    public ListingStatus StatusAsAt(DateOnly asOf, DateTime knownAsOfUtc)
    {
        DateRange.EnsureUtc(knownAsOfUtc, nameof(knownAsOfUtc));

        var applicable = _events
            .Where(e => e.EffectiveDate <= asOf && e.PublishedAtUtc <= knownAsOfUtc)
            .OrderBy(e => e.EffectiveDate)
            .ThenBy(e => e.PublishedAtUtc)
            .ThenBy(e => e.RecordedAtUtc)
            .LastOrDefault();

        return applicable?.Status ?? ListingStatus.Unknown;
    }
}
