using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Records payloads that could not be read. Append-only.</summary>
/// <remarks>
/// <para>
/// Writes through <c>SaveChangesInternalAsync</c>, like the audit sink and the ingestion ledger.
/// A quarantine must be recordable when nothing is authorised, because a policy denial is one of
/// the things worth quarantining a run over - and a platform unable to write down that it could
/// not read something is a platform whose gaps are invisible.
/// </para>
/// <para>
/// Nothing here is ever updated. A payload that fails twice is the same payload failing the same
/// way, and <see cref="IQuarantineStore.IsQuarantinedAsync"/> exists so the caller can skip the
/// second write rather than overwrite the first record with a later timestamp.
/// </para>
/// </remarks>
public sealed class EfQuarantineStore : IQuarantineStore
{
    private readonly AppDbContext _dbContext;

    public EfQuarantineStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(
        QuarantinedPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await _dbContext.QuarantinedPayloads.AddAsync(payload, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> IsQuarantinedAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        return _dbContext.QuarantinedPayloads
            .AsNoTracking()
            .AnyAsync(p => p.Id == hash, cancellationToken);
    }

    public async Task<IReadOnlyList<QuarantinedPayload>> GetRecentAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take < 1)
        {
            return [];
        }

        return await _dbContext.QuarantinedPayloads
            .AsNoTracking()
            .OrderByDescending(p => p.QuarantinedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
