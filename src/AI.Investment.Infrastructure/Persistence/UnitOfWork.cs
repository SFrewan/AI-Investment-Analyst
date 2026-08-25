using AI.Investment.Application.Abstractions;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>Commits domain changes through the guarded context.</summary>
/// <remarks>
/// A thin wrapper on purpose. Its value is that the application layer depends on an abstraction
/// it can substitute in tests, while the actual commit still goes through
/// <see cref="AppDbContext.SaveChangesAsync"/> and therefore through the write guard. There is
/// no path here that bypasses it.
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _dbContext;

    public UnitOfWork(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
