using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Reads and writes registered sources.</summary>
/// <remarks>
/// Tracked reads, deliberately. A source is an aggregate that gets mutated - activated,
/// deactivated, re-licensed, re-scored - and a detached instance would silently discard those
/// changes at save time.
/// </remarks>
public sealed class EfSourceRegistry : ISourceRegistry
{
    private readonly AppDbContext _dbContext;

    public EfSourceRegistry(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<DataSource?> GetByIdAsync(SourceId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _dbContext.DataSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public Task<bool> ExistsAsync(SourceId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _dbContext.DataSources.AnyAsync(s => s.Id == id, cancellationToken);
    }

    /// <summary>
    /// Every registered source that declares <paramref name="category"/> and covers
    /// <paramref name="region"/> - admissible or not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Region is filtered in SQL; category is filtered in memory. Categories live in a
    /// <c>jsonb</c> column and <see cref="DataSource.Supplies"/> is domain logic, neither of which
    /// EF can translate. The region predicate does the work that matters - it is what makes this
    /// a small result set rather than the whole table - and the rest is a set intersection over
    /// what a registry realistically holds.
    /// </para>
    /// <para>
    /// Inactive and unlicensed sources are returned on purpose. Filtering belongs to
    /// <see cref="SourceAdmission"/>, which is pure and testable; a repository that silently
    /// dropped rows would put a licensing rule inside a SQL query and make the reason for an empty
    /// result invisible.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DataSource>> FindSuppliersAsync(
        DataCategory category,
        Region region,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);

        var candidates = await _dbContext.DataSources
            .Where(s => s.Region == region || s.Region == Region.Global)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates.Where(s => s.Supplies(category, region)).ToList();
    }

    public async Task<IReadOnlyList<DataSource>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.DataSources
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(DataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _dbContext.DataSources.Add(source);
    }
}
