using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Securities;

/// <summary>
/// One immutable statement that a security's listing on ONE venue took a status on a date.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the temporal core of the reference model.</strong> A listing is never edited; a
/// new event is appended. That is the whole reason the model exists: <c>Company.ChangeListing</c>
/// overwrites the current ticker and exchange, so the value it replaced is gone and no question
/// about a past date can be answered from it. Here nothing is replaced, so every past date still
/// has an answer.
/// </para>
/// <para>
/// <strong>Venue-qualified, always.</strong> <see cref="VenueId"/> is required and
/// <see cref="ListingStatus.Removed"/> means removed from <em>that</em> venue. A statement about one
/// venue is never evidence about all venues, which is why nothing here can express a market-wide
/// cessation and why no such type exists to express it.
/// </para>
/// <para>
/// <strong>Three instants, and none of them inferred.</strong> <see cref="EffectiveDate"/> is when
/// the transition takes effect and must be stated by the source - it is never derived from a filing
/// date, a last observed price, or the absence of later data. <see cref="PublishedAtUtc"/> is when
/// the statement became knowable, and it is the instant a point-in-time read filters on;
/// <see cref="RecordedAtUtc"/> is when this row was written here. Selecting on
/// <see cref="PublishedAtUtc"/> rather than on the effective date is what stops a backdated
/// correction from leaking into a reconstruction of a date before anyone could have known it.
/// </para>
/// </remarks>
public sealed class ListingEvent
{
    public const int MaxReasonCodeLength = 64;

    private ListingEvent(
        Guid id,
        SecurityId securityId,
        VenueId venueId,
        ListingStatus status,
        DateOnly effectiveDate,
        string reasonCode,
        SourceId sourceId,
        ContentHash? evidenceContentHash,
        DateTime publishedAtUtc,
        DateTime recordedAtUtc)
    {
        Id = id;
        SecurityId = securityId;
        VenueId = venueId;
        Status = status;
        EffectiveDate = effectiveDate;
        ReasonCode = reasonCode;
        SourceId = sourceId;
        EvidenceContentHash = evidenceContentHash;
        PublishedAtUtc = publishedAtUtc;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private ListingEvent()
    {
        VenueId = null!;
        ReasonCode = string.Empty;
        SourceId = null!;
    }

    public Guid Id { get; private set; }

    /// <summary>The security this statement is about.</summary>
    public SecurityId SecurityId { get; private set; }

    /// <summary>The venue this statement is about, and the only venue it is about.</summary>
    public VenueId VenueId { get; private set; }

    /// <summary>The status the listing took on this venue.</summary>
    public ListingStatus Status { get; private set; }

    /// <summary>When the transition takes effect, as the source stated it. Never inferred.</summary>
    public DateOnly EffectiveDate { get; private set; }

    /// <summary>Why, in the source's own terms. A transition without a reason is not evidence.</summary>
    public string ReasonCode { get; private set; }

    /// <summary>Who said so.</summary>
    public SourceId SourceId { get; private set; }

    /// <summary>
    /// The archived bytes this statement was read from, when there are any.
    /// </summary>
    /// <remarks>
    /// Null is allowed and means the statement has no archived document behind it - a venue
    /// reference load, say. It is not a placeholder to be filled in later: a hash that did not come
    /// from real bytes would read as evidence while being an assertion.
    /// </remarks>
    public ContentHash? EvidenceContentHash { get; private set; }

    /// <summary>When the statement became knowable. Point-in-time reads filter on this.</summary>
    public DateTime PublishedAtUtc { get; private set; }

    /// <summary>When this row was written here.</summary>
    public DateTime RecordedAtUtc { get; private set; }

    /// <summary>
    /// Records a listing transition. Every argument is required except the evidence hash.
    /// </summary>
    public static ListingEvent Record(
        Guid id,
        SecurityId securityId,
        VenueId venueId,
        ListingStatus status,
        DateOnly effectiveDate,
        string reasonCode,
        SourceId sourceId,
        DateTime publishedAtUtc,
        DateTime recordedAtUtc,
        ContentHash? evidenceContentHash = null)
    {
        ArgumentNullException.ThrowIfNull(venueId);
        ArgumentNullException.ThrowIfNull(sourceId);

        DateRange.EnsureUtc(publishedAtUtc, nameof(publishedAtUtc));
        DateRange.EnsureUtc(recordedAtUtc, nameof(recordedAtUtc));

        if (id == Guid.Empty)
        {
            throw new DomainValidationException(nameof(id), "A listing event id is required.");
        }

        if (securityId.Value == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(securityId),
                "A listing event must name the security it is about.");
        }

        if (status == ListingStatus.Unknown)
        {
            throw new DomainValidationException(
                nameof(status),
                "A listing event must state a status. Unknown is the absence of evidence, not a "
                + "transition that can be recorded.");
        }

        var trimmedReason = ValidateReasonCode(reasonCode);

        if (recordedAtUtc < publishedAtUtc)
        {
            throw new DomainRuleViolationException(
                "ListingEvent.RecordedAfterPublished",
                $"A listing event cannot be recorded ({recordedAtUtc:O}) before it became knowable "
                + $"({publishedAtUtc:O}).");
        }

        return new ListingEvent(
            id,
            securityId,
            venueId,
            status,
            effectiveDate,
            trimmedReason,
            sourceId,
            evidenceContentHash,
            publishedAtUtc,
            recordedAtUtc);
    }

    private static string ValidateReasonCode(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new DomainValidationException(
                nameof(reasonCode),
                "A listing event must carry the reason the source gave for it.");
        }

        var trimmed = reasonCode.Trim();

        if (trimmed.Length > MaxReasonCodeLength)
        {
            throw new DomainValidationException(
                nameof(reasonCode),
                $"A reason code may not exceed {MaxReasonCodeLength} characters.");
        }

        return trimmed;
    }
}
