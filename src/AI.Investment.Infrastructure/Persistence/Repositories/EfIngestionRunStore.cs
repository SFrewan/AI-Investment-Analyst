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

    /// <summary>
    /// Writes the run to the ledger. Retries once on a cleared tracker if the first attempt is
    /// defeated by unrelated work already pending on this context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This retry exists because a lost ledger row is unrecoverable, and it has cost this
    /// platform an instrument.</strong> The action seam claims an idempotency key <em>before</em>
    /// the effect runs, and this store records the run <em>after</em> it. If the effect succeeds
    /// and this write then throws, the claim stands and the row does not - so the request can never
    /// be fetched again (the seam suppresses it as a duplicate) and can never be recorded (nothing
    /// re-runs it). That happened to <c>AAPL.US</c> prices during the Block 2B backfill, and the
    /// state it produced cannot be undone: <c>ProcessedAction</c> deletion is refused by
    /// <see cref="AppDbContext"/>'s write guard unconditionally, by design.
    /// </para>
    /// <para>
    /// The failure mode is not exotic. This store shares the caller's scoped context, so anything
    /// else that has already poisoned the change tracker - a shared owned-entity instance, a
    /// half-applied domain change, a previous save that threw - defeats a write that has nothing
    /// to do with it. Clearing the tracker and re-adding the run isolates the ledger from that.
    /// </para>
    /// <para>
    /// <strong>What clearing costs.</strong> Other pending changes on this context are discarded.
    /// That is a real loss, and it is the right trade: this path only runs when a save on this
    /// context has just failed, so those changes were not going to commit either - and the
    /// alternative is a permanently inconsistent ledger. Nothing is smuggled past the seam by it:
    /// <c>SaveChangesInternalAsync</c> still runs the guard, which still admits only the exempt
    /// append-only types.
    /// </para>
    /// </remarks>
    public async Task RecordAsync(IngestionRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            await AddAndSaveAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // Deliberately narrow: these two are what a poisoned tracker throws. A cancellation,
            // a connection failure or a programming error still propagates on the first attempt.
            _dbContext.ChangeTracker.Clear();

            await AddAndSaveAsync(run, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AddAndSaveAsync(IngestionRun run, CancellationToken cancellationToken)
    {
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
