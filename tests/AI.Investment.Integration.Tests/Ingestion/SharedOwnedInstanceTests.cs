using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// That no two owners can end up holding the same owned instance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This platform has lost four shipped features to one rule.</strong> The persistence
/// provider associates an owned entity with its owner by <em>reference</em> identity, so one
/// instance held by two owners in the same save is one object with two owners - and the provider
/// resolves that by writing one of them as null, or by refusing the save outright. It has cost a
/// ledger entry (<c>LedgerAccount</c>, Phase 5), an observation batch (<c>IngestionSubject</c>,
/// Phase 7), a paid-for backfill fetch (<c>DateRange</c>, Block 2B), and a first-run source seed
/// (<c>VerificationPolicy</c>, found by these tests).
/// </para>
/// <para>
/// Every one of those was a caller innocently reusing a value. So the answer is not to keep
/// correcting callers: the two shapes that produce the mistake are closed here instead - the
/// factories copy what they are given, and no cached singleton of an owned type is handed out.
/// The tests below are written from the caller's side deliberately, doing the natural, sharing
/// thing, and asserting it is simply no longer possible to get wrong.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SharedOwnedInstanceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Four rows of one document, which is the point: they share a provenance.</summary>
    private static readonly decimal[] Closes = [100m, 101m, 102m, 103m];

    private readonly PostgresFixture _fixture;

    public SharedOwnedInstanceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Every shipped source definition registers together, as a first start-up does it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one that was live and unnoticed. All the connector definitions name
    /// <c>VerificationPolicy.RequiresCorroboration</c>, which was a cached singleton, and
    /// <c>RegisterKnownSourcesHandler</c> seeds every definition inside a single scope. A fresh
    /// installation with EODHD enabled therefore wrote the first source's verification columns and
    /// left the second's null, which PostgreSQL refused.
    /// </para>
    /// <para>
    /// It was invisible on this installation only by accident of history: the price source was
    /// registered weeks before the splits source existed, so the two were never added to one change
    /// tracker. A new deployment would have hit it on its first start.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task Every_shipped_source_definition_can_be_seeded_in_one_scope()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var options = Options.Create(AcquisitionHarness.EodhdOptionsFor());
        var registry = new EfSourceRegistry(context);

        ISourceDefinition[] definitions =
        [
            new EodhdSource(options),
            new EodhdSplitsSource(options),
        ];

        // One at a time, exactly as the seeding handler does it: a save per source, on one context.
        foreach (var definition in definitions)
        {
            registry.Add(definition.Definition(Now));

            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await context.SaveChangesAsync();
            }
        }

        var registered = await registry.GetAllAsync();

        Assert.Equal(definitions.Length, registered.Count);

        // The columns that went null the first time. Reading them back is the assertion.
        foreach (var source in registered)
        {
            Assert.NotNull(source.Verification);
            Assert.NotNull(source.Cadence);
            Assert.NotNull(source.Licensing);
        }
    }

    /// <summary>
    /// Observations built from one provenance instance each keep their own provenance.
    /// </summary>
    /// <remarks>
    /// The natural way to write a normaliser: one retrieval, one provenance, many rows. The subject
    /// was already copied by <c>RecordFact</c>; the provenance beside it was not, which left half
    /// the trap open and made it look closed. One live normaliser was writing this shape.
    /// </remarks>
    [SkippableFact]
    public async Task Observations_sharing_one_provenance_all_keep_it()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var subject = IngestionSubject.Create("Security", "AAPL.US");

        // ONE provenance and ONE subject, reused across every row of the document.
        var provenance = Provenance.Create(
            "eodhd-eod",
            Now.AddDays(-2),
            Now.AddDays(-1),
            Now,
            sourceRecordId: "AAPL.US");

        var observations = Closes
            .Select(close => Observation.RecordFact(
                subject,
                "security.close",
                ObservationValue.Number(close),
                provenance))
            .ToList();

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfObservationStore(context).RecordAsync(observations);
        }

        var stored = await new EfObservationStore(context).ForSubjectAsync(subject, Now.AddDays(1));

        Assert.Equal(Closes.Length, stored.Count);

        foreach (var observation in stored)
        {
            Assert.Equal(SourceId.Create("eodhd-eod"), observation.Provenance.SourceId);
            Assert.Equal("AAPL.US", observation.Provenance.SourceRecordId);
            Assert.Equal(Now, observation.Provenance.RetrievedAtUtc);
            Assert.Equal("AAPL.US", observation.Subject.Identifier);
        }
    }

    /// <summary>
    /// Two ingestion requests built from one window and one subject both record.
    /// </summary>
    /// <remarks>
    /// The Block 2B shape, at the level the fix was made. A caller that builds one window for a
    /// batch and issues a request per instrument is doing the obvious thing, and it used to cost a
    /// paid provider call and an unrecoverable ledger gap.
    /// </remarks>
    [SkippableFact]
    public async Task Two_requests_built_from_one_window_and_subject_both_record()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var runs = new EfIngestionRunStore(context);

        var window = Domain.ValueObjects.DateRange.Create(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));

        var subject = IngestionSubject.Create("Security", "AAPL.US");

        foreach (var correlation in new[] { "shared-one", "shared-two" })
        {
            var request = IngestionRequest.Create(
                EodhdProvider.Id,
                Domain.Sources.DataCategory.MarketPrices,
                Region.Global,
                subject,
                Domain.Common.CorrelationId.Create(correlation),
                Now,
                window);

            await runs.RecordAsync(
                IngestionRun.Refuse(request, "test.refused@1", "shared-instance regression", Now));
        }

        Assert.Equal(2, await context.IngestionRuns.AsNoTracking().CountAsync());

        // Both rows kept their own window and subject rather than one of them being written null.
        foreach (var run in await context.IngestionRuns.AsNoTracking().ToListAsync())
        {
            Assert.NotNull(run.Request.Window);
            Assert.Equal("AAPL.US", run.Request.Subject.Identifier);
        }
    }
}
