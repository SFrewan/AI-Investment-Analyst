using AI.Investment.Domain.Companies;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Abstractions;

/// <summary>Reads and writes companies.</summary>
public interface ICompanyRepository
{
    Task<Company?> GetByIdAsync(CompanyId id, CancellationToken cancellationToken = default);

    Task<Company?> GetByTickerAsync(Ticker ticker, CancellationToken cancellationToken = default);

    Task<bool> ExistsWithTickerAsync(Ticker ticker, CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text search over name and ticker. <paramref name="query"/> null or blank returns
    /// everything, paged.
    /// </summary>
    Task<IReadOnlyList<Company>> SearchAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(string? query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new company. Nothing is persisted until <see cref="IUnitOfWork.SaveChangesAsync"/>,
    /// which itself requires an authorised execution to be in progress.
    /// </summary>
    void Add(Company company);
}
