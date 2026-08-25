using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Persists ingestion runs. Append-only.</summary>
/// <remarks>
/// <para>
/// Writes through <c>SaveChangesInternalAsync</c>, like the audit sink and the execution store.
/// An ingestion run must be recordable when nothing is authorised, because that is exactly the
/// situation a refusal creates - and a refusal that cannot be written down is a platform that
/// declines to ingest without leaving any trace of having done so.
/// </para>
/// <para>
/// Reads are untracked. A run is written once and never revised, so tracking would buy nothing and
/// cost an identity-map entry per row on queries that exist to be scanned.
/// </para>
/// </remarks>
public sealed class EfIngestionRunStore : IIngestionRunStore
{
    private readonly AppDbContext _dbContext;

    public EfIngestionRunStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(IngestionRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var entry = await _dbContext.IngestionRuns.AddAsync(run, cancellationToken).ConfigureAwait(false);

        // The fingerprint is derived rather than domain state, so it lives as a shadow property
        // and is written here - by the one component that knows both the request and the column.
        entry.Property<string>(IngestionRunConfiguration.FingerprintProperty).CurrentValue =
            run.Request.Fingerprint();

        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IngestionRun?> GetLatestForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return _dbContext.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.SourceId == sourceId)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<IngestionRun?> GetLatestSuccessfulForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return _dbContext.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.SourceId == sourceId)
            .Where(r => r.Outcome == IngestionOutcome.Succeeded
                        || r.Outcome == IngestionOutcome.PartiallySucceeded)
            .OrderByDescending(r => r.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> HasCompletedAsync(
        string requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);

        return _dbContext.IngestionRuns
            .AsNoTracking()
            .AnyAsync(
                r => EF.Property<string>(r, IngestionRunConfiguration.FingerprintProperty) == requestFingerprint
                     && r.Outcome == IngestionOutcome.Succeeded,
                cancellationToken);
    }

    public async Task<IReadOnlyList<IngestionRun>> GetRecentAsync(
        DateTime sinceUtc,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take < 1)
        {
            return [];
        }

        return await _dbContext.IngestionRuns
            .AsNoTracking()
            .Where(r => r.StartedAtUtc >= sinceUtc)
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
