using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests;

/// <summary>
/// That a batch of observations from one payload keeps the subject each one is about.
/// </summary>
/// <remarks>
/// <para>
/// Written after a live cycle fetched AAPL prices successfully and then lost every observation it
/// derived from them. The normaliser did what any normaliser does - one subject object, hundreds
/// of observations - and the provider, which owns a value by reference, attached that instance to
/// the first observation and left the rest without one. The insert went out without
/// <c>subject_kind</c> and PostgreSQL refused the batch.
/// </para>
/// <para>
/// A unit test cannot see this: the objects are all correct in memory. It only appears once real
/// change tracking and a real NOT NULL constraint are both in play, which is what this is for.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class ObservationPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private const string Attribute = "price.close";

    private readonly PostgresFixture _fixture;

    public ObservationPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Observations_sharing_one_subject_instance_all_persist_their_subject()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        // Exactly the shape a normaliser produces: one subject object, many observations.
        var subject = IngestionSubject.Create("Security", "AAPL.US");

        var observations = new List<Observation>();

        for (var day = 1; day <= 5; day++)
        {
            observations.Add(Observation.RecordFact(
                subject,
                Attribute,
                ObservationValue.Number(200m + day),
                Provenance.Create(
                    SourceId.Create("eodhd-eod"),
                    Now.AddDays(-day),
                    Now.AddDays(-day),
                    Now)));
        }

        // Observations are beliefs, so they are NOT exempt from the write guard.
        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfObservationStore(context).RecordAsync(observations);
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await verification.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == Attribute)
            .ToListAsync();

        // Compared as sequences rather than by count, so a short read fails as loudly as a null
        // subject would - and every row has to carry the subject, not just the first.
        Assert.Equal(
            Enumerable.Repeat("Security", 5).ToArray(),
            stored.Select(o => o.Subject.Kind).ToArray());

        Assert.Equal(
            Enumerable.Repeat("AAPL.US", 5).ToArray(),
            stored.Select(o => o.Subject.Identifier).ToArray());
    }
}
