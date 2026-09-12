using AI.Investment.Domain.Companies;

namespace AI.Investment.Application.Abstractions;

/// <summary>Reads and writes companies.</summary>
public interface ICompanyRepository
{
    Task<Company?> GetByIdAsync(CompanyId id, CancellationToken cancellationToken = default);

    /// <summary>The company holding this CIK, or null when none does.</summary>
    /// <remarks>
    /// The CIK is the only external identifier <c>Company</c> carries and it is unique when present,
    /// so this is the one lookup that can safely say "this issuer is already held". It replaces
    /// nothing: the ticker lookups went with the ticker at D4, because a symbol names an instrument
    /// rather than a legal entity.
    /// </remarks>
    Task<Company?> FindByCikAsync(Cik cik, CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text search over name. <paramref name="query"/> null or blank returns everything, paged.
    /// Ticker search went with the ticker: a symbol now identifies a security, not a company.
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
