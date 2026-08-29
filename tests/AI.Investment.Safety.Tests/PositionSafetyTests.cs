using System.Reflection;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Portfolio;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The properties a holding must have that are not about arithmetic.
/// </summary>
/// <remarks>
/// A position is money. The structural claims below - that nothing can edit a recorded event, that
/// the model cannot open a short, that no read path can write - are the ones that stay true when
/// somebody adds a feature in six months without reading the arithmetic tests.
/// </remarks>
public sealed class PositionSafetyTests
{
    /// <summary>
    /// A recorded event has no setter anybody can reach. Editing one would rewrite a quantity, a
    /// cost and a realised profit at once, with no counter-entry anywhere.
    /// </summary>
    [Fact]
    public void No_public_member_can_change_a_recorded_event()
    {
        var settable = typeof(PositionEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(settable);
    }

    /// <summary>And no method that looks like a mutation either.</summary>
    [Theory]
    [InlineData("Set")]
    [InlineData("Update")]
    [InlineData("Adjust")]
    [InlineData("Correct")]
    [InlineData("Delete")]
    [InlineData("Reverse")]
    public void A_recorded_event_has_no_mutator(string forbidden) =>
        Assert.DoesNotContain(
            typeof(PositionEvent)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => method.Name),
            name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The store appends and reads. An update or a delete on it would make a holding editable.
    /// </summary>
    [Theory]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("Remove")]
    [InlineData("Set")]
    [InlineData("Clear")]
    [InlineData("Correct")]
    public void The_position_store_can_only_append_and_read(string forbidden) =>
        Assert.DoesNotContain(
            typeof(IPositionEventStore).GetMethods().Select(method => method.Name),
            name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Long only, structurally. Every route into a position refuses to take it below zero, so a
    /// short position cannot be represented rather than merely being avoided.
    /// </summary>
    [Fact]
    public void No_sequence_of_disposals_can_produce_a_negative_holding()
    {
        var position = Position.Flat("AAPL.US", Currency.Usd)
            .Acquire(10m, Money.Create(1000m, Currency.Usd));

        Assert.ThrowsAny<Exception>(() => position.Dispose(11m, Money.Create(1100m, Currency.Usd)));

        var closed = position.Dispose(10m, Money.Create(1500m, Currency.Usd));

        Assert.Equal(0m, closed.Quantity);
        Assert.ThrowsAny<Exception>(() => closed.Dispose(1m, Money.Create(100m, Currency.Usd)));
    }

    /// <summary>
    /// The read model reads. A portfolio surface that could write would be a way to change
    /// financial state without an execution behind it.
    /// </summary>
    [Fact]
    public void The_portfolio_reader_only_reads()
    {
        var writers = typeof(PortfolioReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.Name.StartsWith("Read", StringComparison.Ordinal))
            .Select(method => method.Name)
            .ToList();

        Assert.Empty(writers);
    }

    /// <summary>
    /// The valuation states its own uncertainty rather than defaulting. A model that could report
    /// a market value without a price would be one that invented one.
    /// </summary>
    [Fact]
    public void A_position_view_without_a_price_carries_no_value()
    {
        var view = new PositionView(
            "AAPL.US",
            10m,
            Money.Create(100m, Currency.Usd),
            Money.Create(1000m, Currency.Usd),
            Money.Create(1000m, Currency.Usd),
            Money.Create(0m, Currency.Usd),
            PriceAvailability.NoObservedPrice,
            CurrentPrice: null,
            PriceAsOfUtc: null,
            PricePublishedAtUtc: null,
            MarketValue: null,
            UnrealisedPnL: null);

        Assert.Null(view.MarketValue);
        Assert.Null(view.UnrealisedPnL);
        Assert.NotEqual(0m, view.Exposure.Amount);
    }

    /// <summary>
    /// An unset availability is not a claim of availability. Zero is <c>Unknown</c>, so a
    /// default-initialised view never reads as "priced".
    /// </summary>
    [Fact]
    public void The_default_price_availability_is_unknown() =>
        Assert.Equal(PriceAvailability.Unknown, default(PriceAvailability));
}
