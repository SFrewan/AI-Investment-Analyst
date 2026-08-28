using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Opportunities;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Appends ledger entries, and reads them back. Nothing updates or deletes one.</summary>
/// <remarks>
/// Entries are written through the seam's internal save, like audit records and executions, because
/// a posting has to succeed even when no action is authorised - a ledger that shares the fate of the
/// thing it records is not a ledger. They are written as one unit so the books are never visibly
/// unbalanced between two rows of the same posting.
/// </remarks>
public sealed class EfLedgerStore : ILedgerStore
{
    private readonly AppDbContext _dbContext;

    public EfLedgerStore(AppDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AppendAsync(
        IEnumerable<LedgerEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var materialised = entries.ToList();

        if (materialised.Count == 0)
        {
            return;
        }

        await _dbContext.LedgerEntries.AddRangeAsync(materialised, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LedgerEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.LedgerEntries
            .AsNoTracking()
            .OrderBy(entry => entry.OccurredAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<LedgerEntry>> ListForAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.LedgerEntries
            .AsNoTracking()
            .Where(entry => entry.OpportunityId == opportunityId)
            .OrderBy(entry => entry.OccurredAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
