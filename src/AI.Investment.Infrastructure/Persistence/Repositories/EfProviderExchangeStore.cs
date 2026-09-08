using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Append-only store of provider exchanges.</summary>
/// <remarks>
/// <para>
/// Writes through <c>SaveChangesInternalAsync</c>, like the quarantine store and the ingestion
/// ledger: evidence about an exchange must be recordable even when nothing is authorised, because
/// "the policy engine stopped this" is one of the things worth having evidence of.
/// </para>
/// <para>
/// Nothing here updates or deletes. There is no method to do so, and adding one later would change
/// what the table means.
/// </para>
/// </remarks>
public sealed class EfProviderExchangeStore : IProviderExchangeStore
{
    private readonly AppDbContext _dbContext;

    public EfProviderExchangeStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(
        IReadOnlyList<ProviderExchange> exchanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exchanges);

        if (exchanges.Count == 0)
        {
            // A run that made no exchange has nothing to say. Writing a placeholder row would be
            // inventing evidence of a request that never happened.
            return;
        }

        await _dbContext.ProviderExchanges.AddRangeAsync(exchanges, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProviderExchange>> ForRunAsync(
        IngestionRunId runId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.ProviderExchanges
            .AsNoTracking()
            .Where(e => e.IngestionRunId == runId)
            .OrderBy(e => e.ExchangeOrdinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ProviderExchange>> ForResponseContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return [];
        }

        return await _dbContext.ProviderExchanges
            .AsNoTracking()
            .Where(e => e.ResponseContentHash == contentHash)
            .OrderBy(e => e.RetrievedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
