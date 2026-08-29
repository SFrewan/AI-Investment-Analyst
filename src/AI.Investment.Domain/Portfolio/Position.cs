using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Portfolio;

/// <summary>
/// What is held in one instrument, replayed from that instrument's events.
/// </summary>
/// <remarks>
/// <para>
/// A projection, never a stored row. Two readings of the same events produce the same position, and
/// there is no way for a position to disagree with the events behind it.
/// </para>
/// <para>
/// <strong><see cref="CostBasis"/> is the authoritative number; <see cref="AverageCost"/> is
/// derived from it.</strong> The other way round loses money: an average rounded to four decimal
/// places and multiplied back by a quantity does not reproduce what was paid, and the error
/// accumulates over every partial disposal until a fully closed position reports a basis that is
/// not zero. Relief on a disposal is proportional to the basis, so closing the last unit closes the
/// basis exactly.
/// </para>
/// <para>
/// <strong>Fees are not in the basis and not in <see cref="RealisedPnL"/></strong>, because the
/// capital ledger posts them to their own account rather than into <c>Positions</c>. This model
/// agrees with the ledger about what a holding cost, and neither of them blends the venue's charge
/// into the price.
/// </para>
/// </remarks>
public sealed record Position
{
    /// <summary>Decimal places an average cost is presented to.</summary>
    /// <remarks>
    /// The same precision the money columns are stored at. It is a presentation figure: nothing in
    /// this type or the calculator computes with it.
    /// </remarks>
    public const int AverageCostDecimals = 4;

    private Position(
        string instrument,
        decimal quantity,
        Money costBasis,
        Money realisedPnL)
    {
        Instrument = instrument;
        Quantity = quantity;
        CostBasis = costBasis;
        RealisedPnL = realisedPnL;
    }

    public string Instrument { get; }

    /// <summary>Units held. Zero for a position that has been fully closed.</summary>
    public decimal Quantity { get; }

    /// <summary>What the held units cost, fees excluded. Exactly zero when nothing is held.</summary>
    public Money CostBasis { get; }

    /// <summary>
    /// Profit and loss on quantity that has actually been disposed of. Never touches open units.
    /// </summary>
    public Money RealisedPnL { get; }

    public bool IsOpen => Quantity > 0m;

    /// <summary>
    /// The mean cost of a held unit, rounded for presentation. Null when nothing is held.
    /// </summary>
    /// <remarks>
    /// Null rather than zero: an average cost of nothing is not zero, it is undefined, and a zero
    /// would read as a free holding on any screen that shows it.
    /// </remarks>
    public Money? AverageCost => Quantity > 0m
        ? Money.Create(
            Math.Round(CostBasis.Amount / Quantity, AverageCostDecimals, MidpointRounding.ToEven),
            CostBasis.Currency)
        : null;

    /// <summary>An instrument with no events, or one that has been closed.</summary>
    public static Position Flat(string instrument, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return new Position(Name(instrument), 0m, Money.Zero(currency), Money.Zero(currency));
    }

    /// <summary>Applies an acquisition.</summary>
    public Position Acquire(decimal quantity, Money notional)
    {
        ArgumentNullException.ThrowIfNull(notional);

        EnsurePositive(quantity);
        EnsureCurrency(notional);

        return new Position(
            Instrument,
            Quantity + quantity,
            CostBasis.Add(notional),
            RealisedPnL);
    }

    /// <summary>
    /// Applies a disposal, relieving cost in proportion and realising the difference.
    /// </summary>
    /// <remarks>
    /// Disposing of the whole holding relieves the whole basis by construction - the proportion is
    /// one - so a closed position reports exactly zero rather than a rounding residue.
    /// </remarks>
    public Position Dispose(decimal quantity, Money proceeds)
    {
        ArgumentNullException.ThrowIfNull(proceeds);

        EnsurePositive(quantity);
        EnsureCurrency(proceeds);

        if (quantity > Quantity)
        {
            throw new DomainRuleViolationException(
                "Position.OverDisposal",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cannot dispose of {quantity} units of '{Instrument}' when {Quantity} are held. " +
                    $"This platform holds long positions only; the alternative to refusing is opening " +
                    $"a short one, which nothing in its execution path can do."));
        }

        var relieved = quantity == Quantity
            ? CostBasis
            : CostBasis.MultiplyBy(quantity / Quantity);

        return new Position(
            Instrument,
            Quantity - quantity,
            CostBasis.Subtract(relieved),
            RealisedPnL.Add(proceeds.Subtract(relieved)));
    }

    private void EnsureCurrency(Money amount)
    {
        if (amount.Currency != CostBasis.Currency)
        {
            throw new DomainValidationException(
                nameof(amount),
                $"'{Instrument}' is held in {CostBasis.Currency} and the amount is in " +
                $"{amount.Currency}. This model does not convert; a position in two currencies is " +
                "two positions.");
        }
    }

    private static void EnsurePositive(decimal quantity)
    {
        if (quantity <= 0m)
        {
            throw new DomainValidationException(
                nameof(quantity),
                "A position change must move a positive number of units. Zero would be a fill that " +
                $"did not happen. Received {quantity.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static string Name(string? instrument) =>
        string.IsNullOrWhiteSpace(instrument)
            ? throw new DomainValidationException(nameof(instrument), "A position needs an instrument.")
            : instrument.Trim();

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Instrument}: {Quantity} at {CostBasis} (realised {RealisedPnL})");
}
