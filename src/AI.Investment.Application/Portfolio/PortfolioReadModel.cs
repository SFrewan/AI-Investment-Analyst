using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Portfolio;

/// <summary>Why a position has, or has not, a current price.</summary>
/// <remarks>
/// A distinct state rather than a null price with no explanation. "Nobody has observed a price"
/// and "the instrument is closed so none is needed" lead an operator to different actions, and a
/// screen that showed both as a dash would hide a broken feed behind a flat position.
/// </remarks>
public enum PriceAvailability
{
    /// <summary>Never returned. Present so a default-initialised value is not a claim.</summary>
    Unknown = 0,

    /// <summary>A published close was found, and the valuation below uses it.</summary>
    Available = 1,

    /// <summary>
    /// No published close exists for this instrument. Market value and unrealised profit are
    /// <c>null</c>, and no number was invented to stand in for them.
    /// </summary>
    NoObservedPrice = 2,

    /// <summary>Nothing is held, so no price is needed to value it.</summary>
    NotHeld = 3,
}

/// <summary>One holding, valued where it can honestly be valued.</summary>
/// <remarks>
/// <para>
/// <see cref="MarketValue"/> and <see cref="UnrealisedPnL"/> are nullable and stay null whenever
/// <see cref="Availability"/> is not <see cref="PriceAvailability.Available"/>. There is no
/// fallback to cost, no last-known price carried forward and no zero: each of those would put a
/// number on a screen that no observation supports, and the whole point of the observation store
/// is that every number traces to something somebody published.
/// </para>
/// <para>
/// <see cref="Exposure"/> is cost, not market value, and is therefore always available. That is
/// deliberate and it is what the limit engine compares against: the capital ledger's
/// <c>Positions</c> balance is at cost, so an exposure at market value would be measured against a
/// total it does not belong to - and a concentration ceiling that silently loosened whenever a
/// price feed went quiet would be worse than one that could not be computed at all.
/// </para>
/// </remarks>
public sealed record PositionView(
    string Instrument,
    decimal Quantity,
    Money? AverageCost,
    Money CostBasis,
    Money Exposure,
    Money RealisedPnL,
    PriceAvailability Availability,
    decimal? CurrentPrice,
    DateTime? PriceAsOfUtc,
    DateTime? PricePublishedAtUtc,
    Money? MarketValue,
    Money? UnrealisedPnL)
{
    public bool IsOpen => Quantity > 0m;
}

/// <summary>
/// The portfolio as an operator or a dashboard reads it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TotalValue"/> is null unless every open position could be valued. A total that
/// quietly omitted the positions whose prices were missing would be smaller than the truth and
/// would look like a number. <see cref="ValuedPositions"/> and <see cref="UnvaluedPositions"/> say
/// how much of the portfolio the valuation covers.
/// </para>
/// <para>
/// <see cref="Cash"/> comes from the existing capital ledger and nowhere else. This model does not
/// keep capital; it reads it.
/// </para>
/// </remarks>
public sealed record PortfolioSnapshot(
    Currency Currency,
    DateTime AsAtUtc,
    Money Cash,
    Money CostBasis,
    Money RealisedPnL,
    Money? UnrealisedPnL,
    Money? MarketValue,
    Money? TotalValue,
    int OpenPositions,
    int ValuedPositions,
    int UnvaluedPositions,
    IReadOnlyList<PositionView> Positions)
{
    /// <summary>Whether every open position could be valued at a published price.</summary>
    public bool IsFullyValued => UnvaluedPositions == 0;
}
