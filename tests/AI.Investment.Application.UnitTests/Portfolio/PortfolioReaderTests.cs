using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.Portfolio;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Portfolio;

/// <summary>
/// The portfolio read model, and above all what it does when there is no price.
/// </summary>
public sealed class PortfolioReaderTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc);

    private static readonly Currency Usd = Currency.Usd;

    private static readonly OpportunityId Opportunity = new(Guid.NewGuid());

    private readonly RecordingPositionEventStore _positions = new();
    private readonly StubLedgerStore _ledger = new();
    private readonly SeededObservations _observations = new();

    [Fact]
    public async Task An_empty_portfolio_reports_nothing_held()
    {
        var snapshot = await Reader().ReadAsync();

        Assert.Empty(snapshot.Positions);
        Assert.Equal(0, snapshot.OpenPositions);
        Assert.True(snapshot.Cash.IsZero);
        Assert.True(snapshot.IsFullyValued);
        Assert.Equal(0m, snapshot.TotalValue!.Amount);
    }

    [Fact]
    public async Task A_held_position_with_a_published_price_is_valued()
    {
        Applied("AAPL.US", PositionChange.Acquired, 10m, 100m, "v1");
        Observed("AAPL.US", 130m);

        var position = Assert.Single((await Reader().ReadAsync()).Positions);

        Assert.Equal(PriceAvailability.Available, position.Availability);
        Assert.Equal(130m, position.CurrentPrice);
        Assert.Equal(1300m, position.MarketValue!.Amount);
        Assert.Equal(300m, position.UnrealisedPnL!.Amount);
        Assert.Equal(1000m, position.CostBasis.Amount);
        Assert.Equal(1000m, position.Exposure.Amount);
    }

    /// <summary>
    /// The rule this block exists to get right: no price means no number, not a fallback to cost
    /// and not a zero.
    /// </summary>
    [Fact]
    public async Task A_held_position_with_no_observed_price_is_reported_unvalued()
    {
        Applied("AAPL.US", PositionChange.Acquired, 10m, 100m, "v1");

        var snapshot = await Reader().ReadAsync();
        var position = Assert.Single(snapshot.Positions);

        Assert.Equal(PriceAvailability.NoObservedPrice, position.Availability);
        Assert.Null(position.CurrentPrice);
        Assert.Null(position.MarketValue);
        Assert.Null(position.UnrealisedPnL);

        // And the cost is still known, because cost does not depend on a feed.
        Assert.Equal(1000m, position.CostBasis.Amount);
        Assert.Equal(1000m, position.Exposure.Amount);
    }

    /// <summary>
    /// A total that quietly skipped the unpriced positions would be smaller than the truth and
    /// would still look like an answer.
    /// </summary>
    [Fact]
    public async Task One_unpriced_position_makes_the_portfolio_total_unavailable()
    {
        Applied("AAPL.US", PositionChange.Acquired, 10m, 100m, "v1");
        Applied("MSFT.US", PositionChange.Acquired, 5m, 200m, "v2");
        Observed("AAPL.US", 130m);

        var snapshot = await Reader().ReadAsync();

        Assert.Null(snapshot.TotalValue);
        Assert.Null(snapshot.MarketValue);
        Assert.Null(snapshot.UnrealisedPnL);
        Assert.False(snapshot.IsFullyValued);
        Assert.Equal(2, snapshot.OpenPositions);
        Assert.Equal(1, snapshot.ValuedPositions);
        Assert.Equal(1, snapshot.UnvaluedPositions);
    }

    /// <summary>A settled position needs no price and is not a broken feed.</summary>
    [Fact]
    public async Task A_closed_position_is_not_reported_as_missing_a_price()
    {
        Applied("AAPL.US", PositionChange.Acquired, 10m, 100m, "v1");
        Applied("AAPL.US", PositionChange.Disposed, 10m, 150m, "v2");

        var snapshot = await Reader().ReadAsync();
        var position = Assert.Single(snapshot.Positions);

        Assert.Equal(PriceAvailability.NotHeld, position.Availability);
        Assert.Equal(500m, position.RealisedPnL.Amount);
        Assert.Equal(0, snapshot.OpenPositions);
        Assert.True(snapshot.IsFullyValued);
    }

    /// <summary>Cash is the ledger's. This model reads capital; it does not keep it.</summary>
    [Fact]
    public async Task Cash_comes_from_the_capital_ledger()
    {
        _ledger.Entries.Add(LedgerEntry.Post(
            LedgerAccount.Cash,
            LedgerAccount.ContributedCapital,
            Money.Create(50_000m, Usd),
            Now,
            "Opening contribution",
            Opportunity));

        Assert.Equal(50_000m, (await Reader().ReadAsync()).Cash.Amount);
    }

    /// <summary>
    /// The valuation is point-in-time: a close published after now is not visible, because the
    /// series reader admits an observation only when it was public by the instant asked for.
    /// </summary>
    [Fact]
    public async Task A_price_published_after_now_is_not_used()
    {
        Applied("AAPL.US", PositionChange.Acquired, 10m, 100m, "v1");
        Observed("AAPL.US", 130m, publishedAtUtc: Now.AddHours(1));

        var position = Assert.Single((await Reader().ReadAsync()).Positions);

        Assert.Equal(PriceAvailability.NoObservedPrice, position.Availability);
    }

    /// <summary>Determinism: reading twice from the same state gives the same answer.</summary>
    [Fact]
    public async Task Two_reads_of_the_same_state_agree()
    {
        Applied("AAPL.US", PositionChange.Acquired, 10m, 100m, "v1");
        Applied("AAPL.US", PositionChange.Acquired, 5m, 120m, "v2");
        Applied("AAPL.US", PositionChange.Disposed, 4m, 150m, "v3");
        Observed("AAPL.US", 130m);

        var first = await Reader().ReadAsync();
        var second = await Reader().ReadAsync();

        Assert.Equal(first.Positions, second.Positions);
        Assert.Equal(first.TotalValue, second.TotalValue);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private PortfolioReader Reader() =>
        new(
            _positions,
            _ledger,
            new PriceSeriesReader(_observations),
            DiscoverySettings.Standard,
            new FixedClock(Now));

    private void Applied(
        string instrument,
        PositionChange change,
        decimal quantity,
        decimal price,
        string reference) =>
        _positions.Events.Add(PositionEvent.Record(
            instrument,
            change,
            quantity,
            Money.Create(price, Usd),
            Money.Create(1m, Usd),
            reference,
            Opportunity,
            // Increasing, so replay order matches the order they were applied in. A helper that
            // counted downwards would put a disposal before the acquisition that funded it.
            Now.AddHours(-24).AddMinutes(_positions.Events.Count)));

    private void Observed(string instrument, decimal close, DateTime? publishedAtUtc = null) =>
        _observations.Add(Observation.RecordFact(
            IngestionSubject.Create("Security", instrument),
            DiscoverySettings.Standard.PriceAttribute,
            ObservationValue.Number(close),
            Provenance.Create(
                SourceId.Create("test-prices"),
                Now.AddDays(-1),
                publishedAtUtc ?? Now.AddHours(-1),

                // Retrieval must not precede publication - the domain refuses a claim that was
                // read before it existed - so the fixture derives it rather than fixing it.
                (publishedAtUtc ?? Now.AddHours(-1)).AddMinutes(1),
                sourceRecordId: instrument)));

    private sealed class FixedClock : IClock
    {
        private readonly DateTime _now;

        public FixedClock(DateTime now) => _now = now;

        public DateTime UtcNow => _now;
    }

    private sealed class RecordingPositionEventStore : IPositionEventStore
    {
        public List<PositionEvent> Events { get; } = [];

        public Task<bool> AppendAsync(PositionEvent positionEvent, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(positionEvent);

            if (Events.Exists(e => string.Equals(
                    e.VenueReference,
                    positionEvent.VenueReference,
                    StringComparison.Ordinal)))
            {
                return Task.FromResult(false);
            }

            Events.Add(positionEvent);

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<PositionEvent>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PositionEvent>>(Events);

        public Task<IReadOnlyList<PositionEvent>> ListForAsync(
            string instrument,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PositionEvent>>(Events
                .FindAll(e => string.Equals(e.Instrument, instrument, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class StubLedgerStore : ILedgerStore
    {
        public List<LedgerEntry> Entries { get; } = [];

        public Task AppendAsync(IEnumerable<LedgerEntry> entries, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(entries);

            Entries.AddRange(entries);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LedgerEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerEntry>>(Entries);

        public Task<IReadOnlyList<LedgerEntry>> ListForAsync(
            OpportunityId opportunityId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LedgerEntry>>(
                Entries.FindAll(e => e.OpportunityId == opportunityId));
    }

    private sealed class SeededObservations : IObservationStore
    {
        private readonly List<Observation> _observations = [];

        public void Add(Observation observation) => _observations.Add(observation);

        public Task RecordAsync(
            IReadOnlyList<Observation> observations,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(observations);

            _observations.AddRange(observations);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Observation>> ForSubjectAsync(
            IngestionSubject subject,
            DateTime asAtUtc,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(subject);

            return Task.FromResult<IReadOnlyList<Observation>>(_observations
                .FindAll(o =>
                    string.Equals(o.Subject.Kind, subject.Kind, StringComparison.Ordinal) &&
                    string.Equals(o.Subject.Identifier, subject.Identifier, StringComparison.OrdinalIgnoreCase) &&
                    o.Provenance.PublishedAtUtc <= asAtUtc));
        }

        public Task<Observation?> LatestAsync(
            IngestionSubject subject,
            string attribute,
            DateTime asAtUtc,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(subject);

            return Task.FromResult(_observations
                .FindAll(o =>
                    string.Equals(o.Attribute, attribute, StringComparison.Ordinal) &&
                    o.Provenance.PublishedAtUtc <= asAtUtc)
                .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                .FirstOrDefault());
        }
    }
}
