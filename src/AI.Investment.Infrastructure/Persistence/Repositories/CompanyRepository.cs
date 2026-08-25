using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of the company repository.</summary>
public sealed class CompanyRepository : ICompanyRepository
{
    private readonly AppDbContext _dbContext;

    public CompanyRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<Company?> GetByIdAsync(CompanyId id, CancellationToken cancellationToken = default) =>
        await _dbContext.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Company?> GetByTickerAsync(Ticker ticker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticker);

        return await _dbContext.Companies
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Ticker == ticker, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsWithTickerAsync(Ticker ticker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticker);

        return await _dbContext.Companies
            .AnyAsync(c => c.Ticker == ticker, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Company>> SearchAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        await Filter(query)
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> CountAsync(string? query, CancellationToken cancellationToken = default) =>
        await Filter(query).CountAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Company company)
    {
        ArgumentNullException.ThrowIfNull(company);
        _dbContext.Companies.Add(company);
    }

    /// <summary>
    /// Case-insensitive containment over the name, plus an exact ticker match.
    /// </summary>
    /// <remarks>
    /// <c>EF.Functions.ILike</c> rather than <c>ToLower().Contains()</c>: Npgsql translates it to
    /// a native <c>ILIKE</c>, whereas the lower-case form builds an expression no index can serve.
    /// <para>
    /// The ticker is matched exactly rather than by pattern, because it is stored through a value
    /// converter and a LIKE over a converted column does not translate. Exact matching is also
    /// the better behaviour: searching "A" should not return every company whose symbol contains
    /// an A. If the query is not a well-formed symbol the clause is simply omitted.
    /// </para>
    /// <para>
    /// Adequate for reference data at this scale. Full-text search over documents is a different
    /// problem and belongs with the data plane.
    /// </para>
    /// </remarks>
    private IQueryable<Company> Filter(string? query)
    {
        var companies = _dbContext.Companies.AsQueryable();

        if (string.IsNullOrWhiteSpace(query))
        {
            return companies;
        }

        var trimmed = query.Trim();
        var pattern = $"%{trimmed}%";

        if (Ticker.TryCreate(trimmed, out var ticker) && ticker is not null)
        {
            return companies.Where(c =>
                EF.Functions.ILike(c.Name, pattern) || c.Ticker == ticker);
        }

        return companies.Where(c => EF.Functions.ILike(c.Name, pattern));
    }
}
