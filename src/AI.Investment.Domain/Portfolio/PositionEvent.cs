using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Portfolio;

/// <summary>Which way a fill moved a holding.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero and is refused at construction, for the same reason
/// <c>OrderSide.Unknown</c> is: defaulting to <c>Acquired</c> would let an event assembled with a
/// missing field increase a holding rather than reduce it.
/// </remarks>
public enum PositionChange
{
    /// <summary>Never valid on a recorded event.</summary>
    Unknown = 0,

    /// <summary>Units were bought. Increases the holding and its cost.</summary>
    Acquired = 1,

    /// <summary>Units were sold. Reduces the holding and realises a result.</summary>
    Disposed = 2,
}

/// <summary>
/// One fill, recorded as it affected a holding. Append-only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a record of something that happened, not a balance.</strong> There is no stored
/// quantity, no stored average cost and no stored profit anywhere in this model - a
/// <see cref="Position"/> is replayed from these rows on demand, exactly as
/// <c>CapitalLedger.Balance</c> is projected from ledger entries. A stored balance can be wrong
/// while every event behind it is right, and nothing in the data would say so.
/// </para>
/// <para>
/// <strong><see cref="VenueReference"/> is the identity, and it is the venue's, not ours.</strong>
/// A fill already carries the venue's own reference, and reconciliation against the venue is the
/// reason it exists. Minting a second identity here would create two answers to "was this fill
/// applied?" - and the one the database enforces uniqueness on would be the one nobody reconciles
/// against. The uniqueness constraint on that column is what makes applying a fill twice
/// impossible rather than merely unlikely.
/// </para>
/// <para>
/// <strong>Cost excludes fees</strong>, because the ledger posts fees to their own account rather
/// than into <c>Positions</c>. Folding them into the basis here would make this model disagree with
/// the ledger about what a holding cost, and a blended figure hides what the venue charged - which
/// is the part of a result that is knowable in advance and most often forgotten. The fee is carried
/// on the event so it can be read, and it is excluded from cost and from realised profit.
/// </para>
/// <para>
/// Long-only. A disposal larger than the holding is refused rather than turned into a short
/// position: nothing in this platform's execution path can open one, and inventing the semantics
/// here would put leverage in a model whose only inputs are simulated long fills.
/// </para>
/// </remarks>
public sealed class PositionEvent
{
    public const int MaxInstrumentLength = 60;

    public const int MaxVenueReferenceLength = 120;

    private PositionEvent(
        Guid positionEventId,
        string instrument,
        PositionChange change,
        decimal quantity,
        Money price,
        Money fees,
        string venueReference,
        OpportunityId opportunityId,
        DateTime occurredAtUtc)
    {
        PositionEventId = positionEventId;
        Instrument = instrument;
        Change = change;
        Quantity = quantity;
        Price = price;
        Fees = fees;
        VenueReference = venueReference;
        OpportunityId = opportunityId;
        OccurredAtUtc = occurredAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    /// <remarks>
    /// EF materialises this type through this constructor and then sets each property, because
    /// <see cref="Price"/> and <see cref="Fees"/> are owned types and an owned reference is a
    /// navigation, which EF cannot bind through a constructor. The same pattern every aggregate in
    /// this model already uses. <see cref="Record"/> remains the only way in.
    /// </remarks>
    private PositionEvent()
    {
        Instrument = null!;
        Price = null!;
        Fees = null!;
        VenueReference = null!;
    }

    public Guid PositionEventId { get; private set; }

    /// <summary>
    /// The instrument, spelled exactly as the order and the limit engine spell it.
    /// </summary>
    /// <remarks>
    /// The limit engine reads per-instrument exposure by <c>proposal.Target.Identifier</c>, so an
    /// event stored under a differently normalised symbol would be invisible to a concentration
    /// check while still being real money. The value is stored as given.
    /// </remarks>
    public string Instrument { get; private set; }

    public PositionChange Change { get; private set; }

    /// <summary>Units, always positive. The direction is <see cref="Change"/>.</summary>
    public decimal Quantity { get; private set; }

    public Money Price { get; private set; }

    /// <summary>What the venue charged. Excluded from cost and from realised profit.</summary>
    public Money Fees { get; private set; }

    /// <summary>The venue's own identifier for the fill. Unique across this table.</summary>
    public string VenueReference { get; private set; }

    public OpportunityId OpportunityId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Consideration before fees.</summary>
    public Money Notional => Price.MultiplyBy(Quantity);

    public static PositionEvent Record(
        string instrument,
        PositionChange change,
        decimal quantity,
        Money price,
        Money fees,
        string venueReference,
        OpportunityId opportunityId,
        DateTime occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(fees);

        var symbol = Text(instrument, nameof(instrument), MaxInstrumentLength);
        var reference = Text(venueReference, nameof(venueReference), MaxVenueReferenceLength);

        if (change == PositionChange.Unknown || !Enum.IsDefined(change))
        {
            throw new DomainValidationException(
                nameof(change),
                "A position event must say whether the fill acquired or disposed of units.");
        }

        if (quantity <= 0m)
        {
            throw new DomainValidationException(
                nameof(quantity),
                "A position event records a positive number of units; the direction is the change, " +
                $"not the sign. Received {quantity.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!price.IsPositive)
        {
            throw new DomainValidationException(nameof(price), "A fill price must be positive.");
        }

        if (fees.IsNegative)
        {
            throw new DomainValidationException(nameof(fees), "Fees may not be negative.");
        }

        if (fees.Currency != price.Currency)
        {
            throw new DomainValidationException(
                nameof(fees),
                $"Fees are in {fees.Currency} but the price is in {price.Currency}.");
        }

        if (occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(
                nameof(occurredAtUtc),
                "A position event's timestamp must be UTC. Replay order is decided by it.");
        }

        return new PositionEvent(
            Guid.NewGuid(),
            symbol,
            change,
            quantity,
            price,
            fees,
            reference,
            opportunityId,
            occurredAtUtc);
    }

    private static string Text(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(name, $"A position event requires {name}.");
        }

        var trimmed = value.Trim();

        return trimmed.Length > maxLength
            ? throw new DomainValidationException(
                name,
                $"{name} may be at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.")
            : trimmed;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Change} {Quantity} {Instrument} @ {Price} [{VenueReference}]");
}
