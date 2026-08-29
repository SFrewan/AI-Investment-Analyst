using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Portfolio;

/// <summary>
/// The arithmetic a holding is made of.
/// </summary>
/// <remarks>
/// Every number here is exact. The one place rounding appears is the presented average cost, and
/// the tests below show that nothing computes with it - a fully closed position reports a basis of
/// exactly zero, which is the property a rounded average would destroy.
/// </remarks>
public sealed class PositionTests
{
    private static readonly Currency Usd = Currency.Usd;

    [Fact]
    public void A_new_position_holds_nothing()
    {
        var position = Position.Flat("AAPL.US", Usd);

        Assert.Equal(0m, position.Quantity);
        Assert.True(position.CostBasis.IsZero);
        Assert.True(position.RealisedPnL.IsZero);
        Assert.False(position.IsOpen);
    }

    /// <summary>An average cost of nothing is undefined, not zero.</summary>
    [Fact]
    public void A_flat_position_has_no_average_cost() =>
        Assert.Null(Position.Flat("AAPL.US", Usd).AverageCost);

    [Fact]
    public void Opening_a_position_records_quantity_and_cost()
    {
        var position = Position.Flat("AAPL.US", Usd).Acquire(10m, Money.Create(1000m, Usd));

        Assert.Equal(10m, position.Quantity);
        Assert.Equal(1000m, position.CostBasis.Amount);
        Assert.Equal(100m, position.AverageCost!.Amount);
        Assert.True(position.IsOpen);
    }

    [Fact]
    public void Increasing_a_position_blends_the_cost()
    {
        var position = Position.Flat("AAPL.US", Usd)
            .Acquire(10m, Money.Create(1000m, Usd))
            .Acquire(10m, Money.Create(1400m, Usd));

        Assert.Equal(20m, position.Quantity);
        Assert.Equal(2400m, position.CostBasis.Amount);
        Assert.Equal(120m, position.AverageCost!.Amount);
        Assert.True(position.RealisedPnL.IsZero);
    }

    [Fact]
    public void Reducing_a_position_relieves_cost_in_proportion_and_realises_the_difference()
    {
        var position = Position.Flat("AAPL.US", Usd)
            .Acquire(10m, Money.Create(1000m, Usd))
            .Dispose(4m, Money.Create(480m, Usd));

        // Four of ten units relieve four hundred of the thousand; proceeds were four hundred and
        // eighty, so eighty is realised and six hundred remains at cost.
        Assert.Equal(6m, position.Quantity);
        Assert.Equal(600m, position.CostBasis.Amount);
        Assert.Equal(80m, position.RealisedPnL.Amount);
        Assert.Equal(100m, position.AverageCost!.Amount);
    }

    [Fact]
    public void A_loss_is_realised_the_same_way()
    {
        var position = Position.Flat("AAPL.US", Usd)
            .Acquire(10m, Money.Create(1000m, Usd))
            .Dispose(5m, Money.Create(400m, Usd));

        Assert.Equal(-100m, position.RealisedPnL.Amount);
    }

    /// <summary>
    /// The property a rounded average cost would destroy: the last unit out closes the basis
    /// exactly, whatever the numbers divided into.
    /// </summary>
    [Theory]
    [InlineData(3, 1000)]
    [InlineData(7, 1000)]
    [InlineData(3, 100.07)]
    [InlineData(11, 999.99)]
    public void Closing_a_position_leaves_exactly_zero_cost(int units, decimal cost)
    {
        var position = Position.Flat("AAPL.US", Usd)
            .Acquire(units, Money.Create(cost, Usd))
            .Dispose(1m, Money.Create(10m, Usd))
            .Dispose(units - 1m, Money.Create(20m, Usd));

        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.CostBasis.Amount);
        Assert.False(position.IsOpen);
        Assert.Null(position.AverageCost);
        Assert.Equal(30m - cost, position.RealisedPnL.Amount);
    }

    /// <summary>Realised profit survives the close. It is the account of what happened.</summary>
    [Fact]
    public void Realised_profit_is_kept_after_the_position_closes()
    {
        var position = Position.Flat("AAPL.US", Usd)
            .Acquire(10m, Money.Create(1000m, Usd))
            .Dispose(10m, Money.Create(1500m, Usd));

        Assert.Equal(500m, position.RealisedPnL.Amount);
    }

    [Fact]
    public void Reopening_after_a_close_starts_a_fresh_basis()
    {
        var position = Position.Flat("AAPL.US", Usd)
            .Acquire(10m, Money.Create(1000m, Usd))
            .Dispose(10m, Money.Create(1500m, Usd))
            .Acquire(5m, Money.Create(250m, Usd));

        Assert.Equal(5m, position.Quantity);
        Assert.Equal(250m, position.CostBasis.Amount);
        Assert.Equal(50m, position.AverageCost!.Amount);
        Assert.Equal(500m, position.RealisedPnL.Amount);
    }

    /// <summary>
    /// Long only. The alternative to refusing is opening a short position, which nothing in this
    /// platform's execution path can do.
    /// </summary>
    [Fact]
    public void Disposing_of_more_than_is_held_is_refused()
    {
        var position = Position.Flat("AAPL.US", Usd).Acquire(10m, Money.Create(1000m, Usd));

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => position.Dispose(11m, Money.Create(1100m, Usd)));

        Assert.Contains("long positions only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disposing_from_a_flat_position_is_refused() =>
        Assert.Throws<DomainRuleViolationException>(
            () => Position.Flat("AAPL.US", Usd).Dispose(1m, Money.Create(10m, Usd)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.0001)]
    public void A_non_positive_quantity_is_refused(decimal quantity)
    {
        var position = Position.Flat("AAPL.US", Usd).Acquire(10m, Money.Create(1000m, Usd));

        Assert.Throws<DomainValidationException>(() => position.Acquire(quantity, Money.Create(10m, Usd)));
        Assert.Throws<DomainValidationException>(() => position.Dispose(quantity, Money.Create(10m, Usd)));
    }

    /// <summary>A position in two currencies is two positions.</summary>
    [Fact]
    public void A_mismatched_currency_is_refused()
    {
        var position = Position.Flat("AAPL.US", Usd).Acquire(10m, Money.Create(1000m, Usd));

        Assert.Throws<DomainValidationException>(
            () => position.Acquire(1m, Money.Create(100m, Currency.Create("EUR"))));
    }

    /// <summary>Deterministic: the same inputs give the same position, every time.</summary>
    [Fact]
    public void The_same_sequence_produces_the_same_position()
    {
        static Position Build() => Position.Flat("AAPL.US", Usd)
            .Acquire(7m, Money.Create(933.33m, Usd))
            .Dispose(3m, Money.Create(410m, Usd))
            .Acquire(2m, Money.Create(260m, Usd))
            .Dispose(1m, Money.Create(140m, Usd));

        Assert.Equal(Build(), Build());
    }
}

/// <summary>Constructing the record of a fill's effect.</summary>
public sealed class PositionEventTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc);

    private static readonly OpportunityId Opportunity = new(Guid.NewGuid());

    [Fact]
    public void A_recorded_event_keeps_what_it_was_given()
    {
        var recorded = Record();

        Assert.Equal("AAPL.US", recorded.Instrument);
        Assert.Equal(PositionChange.Acquired, recorded.Change);
        Assert.Equal(10m, recorded.Quantity);
        Assert.Equal(100m, recorded.Price.Amount);
        Assert.Equal(1m, recorded.Fees.Amount);
        Assert.Equal("venue-1", recorded.VenueReference);
        Assert.Equal(1000m, recorded.Notional.Amount);
    }

    /// <summary>Fees are carried but excluded from the consideration, as in the ledger.</summary>
    [Fact]
    public void Notional_excludes_fees() =>
        Assert.Equal(1000m, Record(fees: 25m).Notional.Amount);

    [Fact]
    public void An_unknown_change_is_refused() =>
        Assert.Throws<DomainValidationException>(() => Record(change: PositionChange.Unknown));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_quantity_is_refused(decimal quantity) =>
        Assert.Throws<DomainValidationException>(() => Record(quantity: quantity));

    [Fact]
    public void A_non_positive_price_is_refused() =>
        Assert.Throws<DomainValidationException>(() => Record(price: 0m));

    [Fact]
    public void Negative_fees_are_refused() =>
        Assert.Throws<DomainValidationException>(() => Record(fees: -1m));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_event_without_a_venue_reference_is_refused(string? reference) =>
        Assert.Throws<DomainValidationException>(() => Record(venueReference: reference));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_event_without_an_instrument_is_refused(string? instrument) =>
        Assert.Throws<DomainValidationException>(() => Record(instrument: instrument));

    /// <summary>Replay order is decided by the timestamp, so it may not be ambiguous.</summary>
    [Fact]
    public void A_non_utc_timestamp_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => Record(occurredAt: new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void Fees_in_another_currency_are_refused() =>
        Assert.Throws<DomainValidationException>(() => PositionEvent.Record(
            "AAPL.US",
            PositionChange.Acquired,
            10m,
            Money.Create(100m, Currency.Usd),
            Money.Create(1m, Currency.Create("EUR")),
            "venue-1",
            Opportunity,
            Now));

    private static PositionEvent Record(
        string? instrument = "AAPL.US",
        PositionChange change = PositionChange.Acquired,
        decimal quantity = 10m,
        decimal price = 100m,
        decimal fees = 1m,
        string? venueReference = "venue-1",
        DateTime? occurredAt = null) =>
        PositionEvent.Record(
            instrument!,
            change,
            quantity,
            Money.Create(price, Currency.Usd),
            Money.Create(fees, Currency.Usd),
            venueReference!,
            Opportunity,
            occurredAt ?? Now);
}
