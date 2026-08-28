using AI.Investment.Application.Execution;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The only execution venue this platform has, and the boundary that keeps it the only one.
/// </summary>
/// <remarks>
/// Everything about a real execution happens on this path except the money. That is the point: the
/// simulated path is the production path, so switching venues changes one registration rather than
/// revealing which parts were never exercised.
/// </remarks>
public sealed class SimulatedVenueTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_venue_says_it_is_simulated()
    {
        var venue = Build();

        Assert.True(venue.IsSimulated);
        Assert.Equal("simulated", venue.VenueId);
    }

    [Fact]
    public async Task An_order_fills_at_the_price_it_stated_rather_than_at_invented_slippage()
    {
        var result = await Build().PlaceAsync(Order(quantity: 10m, price: 100m));

        var fill = result.RequireFill();

        Assert.Equal(10m, fill.Quantity);
        Assert.Equal(100m, fill.Price.Amount);
        Assert.Equal(1_000m, fill.Notional.Amount);
    }

    [Fact]
    public async Task Commission_is_the_configured_rate_when_it_exceeds_the_minimum()
    {
        var venue = Build(commissionRate: 0.01m, minimumFee: 1m);

        var fill = (await venue.PlaceAsync(Order(quantity: 10m, price: 100m))).RequireFill();

        Assert.Equal(10m, fill.Fees.Amount);
        Assert.Equal(1_010m, fill.TotalCost.Amount);
    }

    [Fact]
    public async Task A_small_order_pays_the_minimum_fee_rather_than_a_rounding_of_nothing()
    {
        var venue = Build(commissionRate: 0.001m, minimumFee: 1m);

        var fill = (await venue.PlaceAsync(Order(quantity: 1m, price: 10m))).RequireFill();

        Assert.Equal(1m, fill.Fees.Amount);
    }

    [Fact]
    public async Task A_replayed_order_produces_the_same_venue_reference()
    {
        var venue = Build();
        var order = Order();

        var first = (await venue.PlaceAsync(order)).RequireFill();
        var second = (await venue.PlaceAsync(order)).RequireFill();

        Assert.Equal(first.VenueReference, second.VenueReference);
    }

    [Fact]
    public async Task Two_different_orders_do_not_share_a_reference()
    {
        var venue = Build();

        var first = (await venue.PlaceAsync(Order())).RequireFill();
        var second = (await venue.PlaceAsync(Order())).RequireFill();

        Assert.NotEqual(first.VenueReference, second.VenueReference);
    }

    [Fact]
    public async Task A_currency_the_venue_does_not_settle_is_refused_rather_than_converted()
    {
        var venue = Build(currencyCode: "USD");

        var order = VenueOrder.Create(
            "AAPL",
            OrderSide.Buy,
            1m,
            Money.Create(10m, Currency.Create("EUR")),
            OpportunityId.New(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("n"));

        var result = await venue.PlaceAsync(order);

        Assert.False(result.Filled);
        Assert.Contains("exchange rate", result.Refusal!, StringComparison.Ordinal);
    }

    private static SimulatedVenue Build(
        decimal commissionRate = 0.001m,
        decimal minimumFee = 1m,
        string currencyCode = "USD") =>
        new(
            Options.Create(new SimulatedVenueOptions
            {
                CommissionRate = commissionRate,
                MinimumFee = minimumFee,
                CurrencyCode = currencyCode,
            }),
            new FakeClock(Now));

    private static VenueOrder Order(decimal quantity = 10m, decimal price = 100m) =>
        VenueOrder.Create(
            "AAPL",
            OrderSide.Buy,
            quantity,
            Money.Create(price, Currency.Usd),
            OpportunityId.New(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("n"));
}
