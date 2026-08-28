using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Execution;

/// <summary>What a venue is being asked to do.</summary>
/// <remarks>
/// <para>
/// Deliberately narrow: an instrument, a side, a quantity and a price. It carries the opportunity
/// and the approval it came from, so that no order can exist without a traceable reason and a
/// traceable permission - not because the venue needs them, but because the record does.
/// </para>
/// <para>
/// The price is required, including for what a real venue would call a market order. A simulated
/// fill has to happen at some price, and letting the venue choose one would make the simulation's
/// results depend on a number the caller never stated and cannot reproduce.
/// </para>
/// </remarks>
public sealed record VenueOrder
{
    public const int MaxInstrumentLength = 60;

    private VenueOrder(
        string instrument,
        OrderSide side,
        decimal quantity,
        Money price,
        OpportunityId opportunityId,
        Guid approvalTokenId,
        string idempotencyKey)
    {
        Instrument = instrument;
        Side = side;
        Quantity = quantity;
        Price = price;
        OpportunityId = opportunityId;
        ApprovalTokenId = approvalTokenId;
        IdempotencyKey = idempotencyKey;
    }

    public string Instrument { get; }

    public OrderSide Side { get; }

    public decimal Quantity { get; }

    /// <summary>The price per unit the order is to be filled at.</summary>
    public Money Price { get; }

    public OpportunityId OpportunityId { get; }

    /// <summary>The approval that permitted this. An order without one cannot be built.</summary>
    public Guid ApprovalTokenId { get; }

    /// <summary>Replays are refused, not repeated.</summary>
    public string IdempotencyKey { get; }

    /// <summary>Total consideration before fees.</summary>
    public Money Notional => Price.MultiplyBy(Quantity);

    public static VenueOrder Create(
        string instrument,
        OrderSide side,
        decimal quantity,
        Money price,
        OpportunityId opportunityId,
        Guid approvalTokenId,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(price);

        if (string.IsNullOrWhiteSpace(instrument))
        {
            throw new DomainValidationException(nameof(instrument), "An order must name an instrument.");
        }

        var trimmed = instrument.Trim();

        if (trimmed.Length > MaxInstrumentLength)
        {
            throw new DomainValidationException(
                nameof(instrument),
                $"An instrument identifier may not exceed {MaxInstrumentLength} characters.");
        }

        if (side == OrderSide.Unknown || !Enum.IsDefined(side))
        {
            throw new DomainValidationException(
                nameof(side),
                "An order must state its side. Defaulting one would commit capital on a field nobody set.");
        }

        if (quantity <= 0m)
        {
            throw new DomainValidationException(
                nameof(quantity),
                $"An order quantity must be positive; received " +
                $"{quantity.ToString(CultureInfo.InvariantCulture)}. Direction is the side, not the sign.");
        }

        if (!price.IsPositive)
        {
            throw new DomainValidationException(nameof(price), "An order price must be positive.");
        }

        if (approvalTokenId == Guid.Empty)
        {
            throw new DomainRuleViolationException(
                "VenueOrder.RequiresApproval",
                "An order must name the approval that permitted it. An order that cannot say what " +
                "authorised it is one nobody authorised.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainValidationException(
                nameof(idempotencyKey),
                "An order must carry an idempotency key. Without one, a retry buys twice.");
        }

        return new VenueOrder(
            trimmed,
            side,
            quantity,
            price,
            opportunityId,
            approvalTokenId,
            idempotencyKey.Trim());
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Side} {Quantity} {Instrument} @ {Price}");
}
