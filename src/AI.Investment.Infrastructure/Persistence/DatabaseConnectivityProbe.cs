using AI.Investment.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>Reports whether PostgreSQL is reachable, for the readiness health check.</summary>
public sealed class DatabaseConnectivityProbe : IDatabaseConnectivityProbe
{
    private readonly AppDbContext _dbContext;

    public DatabaseConnectivityProbe(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        _dbContext.Database.CanConnectAsync(cancellationToken);
}
