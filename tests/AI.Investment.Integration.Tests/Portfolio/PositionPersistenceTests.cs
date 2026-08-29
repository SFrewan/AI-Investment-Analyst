using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Portfolio;

/// <summary>
/// Position events against a real PostgreSQL, where the idempotency actually lives.
/// </summary>
/// <remarks>
/// The uniqueness constraint on the venue reference is the whole mechanism, so it has to be
/// exercised against the database that enforces it. An in-memory double shows the application code
/// is shaped correctly; only this shows that a fill cannot be applied twice.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class PositionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc);

    private static readonly Currency Usd = Currency.Usd;

    private readonly PostgresFixture _fixture;

    public PositionPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_position_event_is_saved_and_reloaded()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        Assert.True(await AppendAsync(Event("AAPL.US", "venue-1")));

        var reloaded = Assert.Single(await ReadAsync());

        Assert.Equal("AAPL.US", reloaded.Instrument);
        Assert.Equal(PositionChange.Acquired, reloaded.Change);
        Assert.Equal(10m, reloaded.Quantity);
        Assert.Equal(100m, reloaded.Price.Amount);
        Assert.Equal(Usd, reloaded.Price.Currency);
        Assert.Equal(1m, reloaded.Fees.Amount);
        Assert.Equal("venue-1", reloaded.VenueReference);
        Assert.Equal(Now, reloaded.OccurredAtUtc);
    }

    /// <summary>The property the whole design turns on.</summary>
    [SkippableFact]
    public async Task The_same_fill_applied_twice_is_recorded_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        Assert.True(await AppendAsync(Event("AAPL.US", "venue-1")));

        // A second event object carrying the same venue reference - which is what a replayed
        // message or a retried cycle produces.
        Assert.False(await AppendAsync(Event("AAPL.US", "venue-1")));

        var events = await ReadAsync();

        Assert.Single(events);
        Assert.Equal(10m, PositionCalculator.ReplayFor("AAPL.US", Usd, events).Quantity);
    }

    /// <summary>
    /// The race the constraint exists for: callers that all looked first and all found nothing.
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_application_of_one_fill_records_it_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var applied = await Task.WhenAll(
            AppendAsync(Event("AAPL.US", "venue-race")),
            AppendAsync(Event("AAPL.US", "venue-race")),
            AppendAsync(Event("AAPL.US", "venue-race")));

        Assert.Equal(1, applied.Count(wrote => wrote));

        var events = await ReadAsync();

        Assert.Single(events);
        Assert.Equal(10m, PositionCalculator.ReplayFor("AAPL.US", Usd, events).Quantity);
    }

    [SkippableFact]
    public async Task Sequential_fills_for_one_instrument_accumulate()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await AppendAsync(Event("AAPL.US", "v1", quantity: 10m, price: 100m));
        await AppendAsync(Event("AAPL.US", "v2", quantity: 5m, price: 120m, minutes: 1));

        var position = PositionCalculator.ReplayFor("AAPL.US", Usd, await ReadAsync());

        Assert.Equal(15m, position.Quantity);
        Assert.Equal(1600m, position.CostBasis.Amount);
    }

    [SkippableFact]
    public async Task Fills_for_different_instruments_stay_separate()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await AppendAsync(Event("AAPL.US", "v1"));
        await AppendAsync(Event("MSFT.US", "v2", minutes: 1));

        var positions = PositionCalculator.Replay(await ReadAsync());

        Assert.Equal(2, positions.Count);
        Assert.All(positions, position => Assert.Equal(10m, position.Quantity));
    }

    [SkippableFact]
    public async Task A_position_can_be_closed_and_reopened()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await AppendAsync(Event("AAPL.US", "v1", quantity: 10m, price: 100m));
        await AppendAsync(Event("AAPL.US", "v2", PositionChange.Disposed, 10m, 150m, minutes: 1));
        await AppendAsync(Event("AAPL.US", "v3", quantity: 4m, price: 90m, minutes: 2));

        var position = PositionCalculator.ReplayFor("AAPL.US", Usd, await ReadAsync());

        Assert.Equal(4m, position.Quantity);
        Assert.Equal(360m, position.CostBasis.Amount);
        Assert.Equal(500m, position.RealisedPnL.Amount);
    }

    /// <summary>
    /// A holding is replayed from these rows, so editing one edits a quantity, a cost and a
    /// realised profit at once. The guard refuses it even inside an authorised window.
    /// </summary>
    [SkippableFact]
    public async Task A_recorded_event_cannot_be_modified()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await AppendAsync(Event("AAPL.US", "v1"));

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var stored = await context.PositionEvents.FirstAsync();

        context.Entry(stored).Property(nameof(PositionEvent.Instrument)).CurrentValue = "MSFT.US";

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => context.SaveChangesAsync());

            Assert.Contains("append-only", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task A_recorded_event_cannot_be_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await AppendAsync(Event("AAPL.US", "v1"));

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        context.PositionEvents.Remove(await context.PositionEvents.FirstAsync());

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
        }
    }

    /// <summary>
    /// A fill moves money, so recording one needs a decision behind it. This is the difference
    /// between a position event and an audit record, which must be written either way.
    /// </summary>
    [SkippableFact]
    public async Task A_position_event_cannot_be_written_without_an_authorised_decision()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var store = new EfPositionEventStore(context);

        await Assert.ThrowsAsync<UnauthorizedWriteException>(
            () => store.AppendAsync(Event("AAPL.US", "v1")));

        Assert.Empty(await ReadAsync());
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Appends through the real store, inside its own authorisation window and its own context -
    /// which is also what makes the concurrency test a real race rather than three calls on one
    /// change tracker.
    /// </summary>
    private async Task<bool> AppendAsync(PositionEvent positionEvent)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            return await new EfPositionEventStore(context).AppendAsync(positionEvent);
        }
    }

    private async Task<IReadOnlyList<PositionEvent>> ReadAsync()
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await new EfPositionEventStore(context).ListAsync();
    }

    private static PositionEvent Event(
        string instrument,
        string reference,
        PositionChange change = PositionChange.Acquired,
        decimal quantity = 10m,
        decimal price = 100m,
        int minutes = 0) =>
        PositionEvent.Record(
            instrument,
            change,
            quantity,
            Money.Create(price, Usd),
            Money.Create(1m, Usd),
            reference,
            new OpportunityId(Guid.NewGuid()),
            Now.AddMinutes(minutes));
}
