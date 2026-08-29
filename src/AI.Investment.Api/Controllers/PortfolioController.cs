using AI.Investment.Api.Security;
using AI.Investment.Application.Portfolio;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>What is held, what it cost, and what it is worth where that can be known.</summary>
/// <remarks>
/// <para>
/// Read-only, and authenticated. There is no endpoint here that opens, closes or adjusts a
/// position: holdings change only as a consequence of a fill recorded inside the execution seam,
/// and an endpoint that could change one directly would be a way to write financial state without
/// a decision behind it.
/// </para>
/// <para>
/// <strong>Financial state is not anonymous.</strong> It requires the <c>ViewPortfolio</c>
/// privilege - the only read privilege in the system, and separate from the four decision ones
/// because reading what is held and being able to act on it are different grants.
/// </para>
/// <para>
/// No business logic here: the read model is composed in the application layer, so a second caller
/// that is not HTTP gets the same numbers.
/// </para>
/// </remarks>
[ApiController]
[Route("api/portfolio")]
[Produces("application/json")]
[Authorize(Policy = OperatorPolicies.ViewPortfolio)]
public sealed class PortfolioController : ControllerBase
{
    private readonly PortfolioReader _portfolio;

    public PortfolioController(PortfolioReader portfolio) =>
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));

    /// <summary>Cash, holdings, cost, realised and unrealised profit.</summary>
    /// <remarks>
    /// <c>totalValue</c>, <c>marketValue</c> and <c>unrealisedPnL</c> are <c>null</c> unless every
    /// open position could be valued at a published price. A total that quietly omitted the unpriced
    /// positions would be smaller than the truth and would still look like an answer.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(PortfolioResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _portfolio.ReadAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new PortfolioResponse(
            snapshot.Currency.Code,
            snapshot.AsAtUtc,
            snapshot.Cash.Amount,
            snapshot.CostBasis.Amount,
            snapshot.RealisedPnL.Amount,
            snapshot.UnrealisedPnL?.Amount,
            snapshot.MarketValue?.Amount,
            snapshot.TotalValue?.Amount,
            snapshot.IsFullyValued,
            snapshot.OpenPositions,
            snapshot.ValuedPositions,
            snapshot.UnvaluedPositions,
            snapshot.Positions.Select(Map).ToList()));
    }

    /// <summary>The positions alone.</summary>
    [HttpGet("positions")]
    [ProducesResponseType(typeof(IReadOnlyList<PositionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var positions = await _portfolio.ReadPositionsAsync(cancellationToken).ConfigureAwait(false);

        return Ok(positions.Select(Map).ToList());
    }

    /// <summary>
    /// The view as JSON, with the price state named rather than implied by a null.
    /// </summary>
    private static PositionResponse Map(PositionView view) =>
        new(
            view.Instrument,
            view.Quantity,
            view.AverageCost?.Amount,
            view.CostBasis.Amount,
            view.Exposure.Amount,
            view.RealisedPnL.Amount,
            view.Availability.ToString(),
            view.CurrentPrice,
            view.PriceAsOfUtc,
            view.PricePublishedAtUtc,
            view.MarketValue?.Amount,
            view.UnrealisedPnL?.Amount,
            view.IsOpen);
}

/// <summary>The portfolio, as the operator console and the future dashboard read it.</summary>
public sealed record PortfolioResponse(
    string Currency,
    DateTime AsAtUtc,
    decimal Cash,
    decimal CostBasis,
    decimal RealisedPnL,
    decimal? UnrealisedPnL,
    decimal? MarketValue,
    decimal? TotalValue,
    bool IsFullyValued,
    int OpenPositions,
    int ValuedPositions,
    int UnvaluedPositions,
    IReadOnlyList<PositionResponse> Positions);

/// <summary>One holding.</summary>
/// <remarks>
/// <c>priceAvailability</c> is a name, not a flag: <c>Available</c>, <c>NoObservedPrice</c> or
/// <c>NotHeld</c>. A dash on a screen tells an operator nothing about whether a feed is broken.
/// </remarks>
public sealed record PositionResponse(
    string Instrument,
    decimal Quantity,
    decimal? AverageCost,
    decimal CostBasis,
    decimal Exposure,
    decimal RealisedPnL,
    string PriceAvailability,
    decimal? CurrentPrice,
    DateTime? PriceAsOfUtc,
    DateTime? PricePublishedAtUtc,
    decimal? MarketValue,
    decimal? UnrealisedPnL,
    bool IsOpen);
