using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Xunit;

namespace AI.Investment.Integration.Tests.Opportunities;

/// <summary>
/// The price read, against the database that actually stores the observations.
/// </summary>
/// <remarks>
/// <para>
/// Every number the screen sees comes through here. Its point-in-time and restatement behaviour
/// was covered only against an in-memory double, and a defect in either would feed a price into
/// discovery that nobody could have known at the time - producing candidates that backtest
/// beautifully and lose money live. That is the failure this file exists to make loud.
/// </para>
/// <para>
/// It also covers the split adjustment end to end, because a split lives in the observation store
/// like any other claim and the whole point is that the read resolves both together.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class PriceSeriesPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime FirstSession = new(2026, 6, 1, 20, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    private const string Close = "security.close";

    private const string Split = "security.split-ratio";

    private const decimal Tolerance = 0.5m;

    // Hoisted for CA1861, and named so each assertion states what it is asserting.
    private static readonly decimal[] ThreeRising = [100m, 101m, 102m];

    private static readonly decimal[] TwoRising = [100m, 101m];

    private static readonly decimal[] LastTwo = [103m, 104m];

    private static readonly decimal[] RestatedByFour = [100m, 101m, 101m, 102m];

    private readonly PostgresFixture _fixture;

    public PriceSeriesPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static IngestionSubject Apple() => IngestionSubject.Create("Security", "AAPL.US");

    [SkippableFact]
    public async Task Stored_closes_come_back_oldest_first()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync(Closes(100m, 101m, 102m));

        var series = await ReadAsync(maxSessions: 10);

        Assert.Equal(ThreeRising, series.Select(p => p.Close).ToArray());
    }

    /// <summary>
    /// A price that had not been published by the instant asked about is not visible at it.
    /// </summary>
    /// <remarks>
    /// The guarantee the whole platform's validity rests on. Covered against a fake before now;
    /// this asserts it against the query that actually runs.
    /// </remarks>
    [SkippableFact]
    public async Task A_close_published_after_the_instant_asked_about_is_not_returned()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync(Closes(100m, 101m, 102m));

        // As at the second session's publication, the third close does not exist yet.
        var series = await ReadAsync(maxSessions: 10, asAtUtc: FirstSession.AddDays(1).AddHours(1));

        Assert.Equal(TwoRising, series.Select(p => p.Close).ToArray());
    }

    /// <summary>
    /// Where a session was restated, the figure published latest by the instant wins.
    /// </summary>
    [SkippableFact]
    public async Task A_restated_close_resolves_to_what_was_known_at_the_time()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var session = FirstSession;

        await SeedAsync(
        [
            Observation.RecordFact(
                Apple(),
                Close,
                ObservationValue.Number(100m),
                Provenance.Create("test-source", session, session.AddMinutes(15), Now)),

            // The same session, corrected an hour later.
            Observation.RecordFact(
                Apple(),
                Close,
                ObservationValue.Number(104m),
                Provenance.Create("test-source", session, session.AddHours(1), Now)),
        ]);

        // Before the correction was published: the original stands.
        var early = await ReadAsync(maxSessions: 10, asAtUtc: session.AddMinutes(30));
        Assert.Equal(100m, Assert.Single(early).Close);

        // After it: the correction wins, and the session still appears once.
        var late = await ReadAsync(maxSessions: 10, asAtUtc: session.AddHours(2));
        Assert.Equal(104m, Assert.Single(late).Close);
    }

    [SkippableFact]
    public async Task The_window_keeps_the_most_recent_sessions()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync(Closes(100m, 101m, 102m, 103m, 104m));

        var series = await ReadAsync(maxSessions: 2);

        Assert.Equal(LastTwo, series.Select(p => p.Close).ToArray());
    }

    /// <summary>
    /// A split stored as an observation restates the history it precedes.
    /// </summary>
    /// <remarks>
    /// End to end through the store, because that is where the two attributes have to be resolved
    /// together. Raw, this series is a seventy-five per cent collapse.
    /// </remarks>
    [SkippableFact]
    public async Task A_stored_split_restates_the_series_rather_than_reading_as_a_collapse()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var observations = Closes(400m, 404m, 101m, 102m).ToList();

        observations.Add(Observation.RecordFact(
            Apple(),
            Split,
            ObservationValue.Number(4m),
            Provenance.Create(
                "test-source",
                FirstSession.AddDays(2),
                FirstSession.AddDays(2).AddMinutes(15),
                Now)));

        await SeedAsync(observations);

        var adjusted = await ReadAdjustedAsync();

        Assert.True(adjusted.IsUsable);
        Assert.Equal(RestatedByFour, adjusted.Observations.Select(p => p.Close).ToArray());

        // Every restated close still maps to its own stored row, so the evidence an opportunity
        // would cite still resolves. The screen sees a restated number; the trail keeps the raw one.
        Assert.Equal(4, adjusted.Observations.Select(o => o.Id).Distinct().Count());
    }

    /// <summary>
    /// <strong>The guard.</strong> The same series without the split is refused, not screened.
    /// </summary>
    [SkippableFact]
    public async Task The_same_series_without_the_split_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync(Closes(400m, 404m, 101m, 102m));

        var adjusted = await ReadAdjustedAsync();

        Assert.False(adjusted.IsUsable);
        Assert.Equal(SeriesRefusal.UnexplainedDiscontinuity, adjusted.Refusal);
        Assert.Empty(adjusted.Observations);
    }

    /// <summary>
    /// A split the platform did not know about yet does not restate a past instant's history.
    /// </summary>
    /// <remarks>
    /// What makes a replay honest. Asked about a moment before the split was published, the read
    /// must not use it - and, because the raw series is then discontinuous, must refuse rather
    /// than answer. Both halves matter: the point-in-time rule, and the refusal that follows it.
    /// </remarks>
    [SkippableFact]
    public async Task A_split_published_later_does_not_restate_an_earlier_instant()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var observations = Closes(400m, 404m, 101m, 102m).ToList();

        observations.Add(Observation.RecordFact(
            Apple(),
            Split,
            ObservationValue.Number(4m),
            Provenance.Create(
                "test-source",
                FirstSession.AddDays(2),
                Now.AddDays(-1),
                Now)));

        await SeedAsync(observations);

        // As at a moment before the split was published, it is invisible.
        var earlier = await ReadAdjustedAsync(asAtUtc: FirstSession.AddDays(10));

        Assert.False(earlier.IsUsable);
        Assert.Equal(SeriesRefusal.UnexplainedDiscontinuity, earlier.Refusal);

        // And once it is public, the same window resolves.
        var later = await ReadAdjustedAsync(asAtUtc: Now);

        Assert.True(later.IsUsable);
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<IReadOnlyList<PricedObservation>> ReadAsync(
        int maxSessions,
        DateTime? asAtUtc = null)
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await new PriceSeriesReader(new EfObservationStore(context))
            .ReadAsync(Apple(), Close, maxSessions, asAtUtc ?? Now);
    }

    private async Task<AdjustedPriceSeries> ReadAdjustedAsync(DateTime? asAtUtc = null)
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await new PriceSeriesReader(new EfObservationStore(context))
            .ReadAdjustedAsync(Apple(), Close, Split, 120, asAtUtc ?? Now, Tolerance);
    }

    private async Task SeedAsync(IReadOnlyList<Observation> observations)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfObservationStore(context).RecordAsync(observations);
        }
    }

    /// <summary>
    /// One close per session, each published fifteen minutes after its own session closes.
    /// </summary>
    private static List<Observation> Closes(params decimal[] closes) =>
        closes
            .Select((close, index) =>
            {
                var session = FirstSession.AddDays(index);

                return Observation.RecordFact(
                    Apple(),
                    Close,
                    ObservationValue.Number(close),
                    Provenance.Create("test-source", session, session.AddMinutes(15), Now));
            })
            .ToList();
}
