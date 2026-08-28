using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Execution;

/// <summary>What actually happened at the venue.</summary>
/// <remarks>
/// Fees are separate from the fill price rather than folded into it. A blended price hides what the
/// venue charged, and cost is the part of a strategy's result that is knowable in advance and most
/// often forgotten.
/// </remarks>
public sealed record VenueFill
{
    private VenueFill(
        string venueReference,
        decimal quantity,
        Money price,
        Money fees,
        DateTime filledAtUtc)
    {
        VenueReference = venueReference;
        Quantity = quantity;
        Price = price;
        Fees = fees;
        FilledAtUtc = filledAtUtc;
    }

    /// <summary>The venue's own identifier for this fill.</summary>
    public string VenueReference { get; }

    public decimal Quantity { get; }

    public Money Price { get; }

    public Money Fees { get; }

    public DateTime FilledAtUtc { get; }

    /// <summary>Consideration before fees.</summary>
    public Money Notional => Price.MultiplyBy(Quantity);

    /// <summary>What leaves or arrives in cash, fees included.</summary>
    public Money TotalCost => Notional.Add(Fees);

    public static VenueFill Create(
        string venueReference,
        decimal quantity,
        Money price,
        Money fees,
        DateTime filledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(fees);
        if (filledAtUtc.Kind != DateTimeKind.Utc)
        {
            // The domain's own guard is internal to that assembly, so the application layer states
            // the same rule itself rather than widening the domain's surface for one call site.
            throw new DomainValidationException(nameof(filledAtUtc), "A fill timestamp must be UTC.");
        }

        if (string.IsNullOrWhiteSpace(venueReference))
        {
            throw new DomainValidationException(
                nameof(venueReference),
                "A fill must carry the venue's own reference, or it cannot be reconciled against one.");
        }

        if (quantity <= 0m)
        {
            throw new DomainValidationException(
                nameof(quantity),
                $"A filled quantity must be positive; received {quantity.ToString(CultureInfo.InvariantCulture)}.");
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

        return new VenueFill(venueReference.Trim(), quantity, price, fees, filledAtUtc);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Quantity} @ {Price} + {Fees} fees [{VenueReference}]");
}
