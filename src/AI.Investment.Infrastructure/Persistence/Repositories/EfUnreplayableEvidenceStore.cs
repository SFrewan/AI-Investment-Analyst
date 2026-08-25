using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Records payloads deleted under licence. Append-only.</summary>
/// <remarks>
/// Writes inside the authorisation window the retention deletion opened, through the ordinary
/// guarded save path. Unlike the ingestion ledger this is not seam bookkeeping: a marker is only
/// ever written as part of an authorised deletion, so it has no need to be writable when nothing
/// is authorised - and exempting it would grant a write path nothing requires.
/// </remarks>
public sealed class EfUnreplayableEvidenceStore : IUnreplayableEvidenceStore
{
    private readonly AppDbContext _dbContext;

    public EfUnreplayableEvidenceStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(UnreplayableEvidence marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);

        await _dbContext.UnreplayableEvidence.AddAsync(marker, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<UnreplayableEvidence?> FindAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        return _dbContext.UnreplayableEvidence
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == hash, cancellationToken);
    }

    public Task<bool> IsUnreplayableAsync(ContentHash hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        return _dbContext.UnreplayableEvidence
            .AsNoTracking()
            .AnyAsync(e => e.Id == hash, cancellationToken);
    }
}
