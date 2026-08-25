using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Infrastructure.Persistence;

namespace AI.Investment.Infrastructure.Actions;

/// <summary>Persists completed action executions. Append-only.</summary>
public sealed class EfActionExecutionStore : IActionExecutionStore
{
    private readonly AppDbContext _dbContext;

    public EfActionExecutionStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(ActionExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        await _dbContext.ActionExecutions.AddAsync(execution, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }
}
