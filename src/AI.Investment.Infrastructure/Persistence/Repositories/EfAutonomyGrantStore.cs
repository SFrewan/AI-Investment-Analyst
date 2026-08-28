using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Reads and stages autonomy grants.</summary>
/// <remarks>
/// Staging only: a new grant is committed by the unit of work inside the authorisation window the
/// action gateway opened, so issuing one is atomic with the audit record that says who issued it.
/// A store that saved on its own would let the grant exist with no record of where it came from.
/// </remarks>
public sealed class EfAutonomyGrantStore : IAutonomyGrantStore
{
    private readonly AppDbContext _dbContext;

    public EfAutonomyGrantStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<AutonomyGrant>> GetActiveAsync(
        Capability capability,
        string environmentName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        // Expiry and revocation are filtered in the database; the resolver checks them again on
        // every candidate. Two mechanisms, because a clock skew between the database and the process
        // must not be able to widen what is in force.
        return await _dbContext.AutonomyGrants
            .Where(grant => grant.Capability == capability)
            .Where(grant => grant.EnvironmentName == environmentName)
            .Where(grant => grant.ExpiresAtUtc > nowUtc)
            .Where(grant => grant.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutonomyGrant>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.AutonomyGrants
            .OrderByDescending(grant => grant.GrantedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<AutonomyGrant?> FindAsync(Guid autonomyGrantId, CancellationToken cancellationToken = default) =>
        _dbContext.AutonomyGrants
            .FirstOrDefaultAsync(grant => grant.AutonomyGrantId == autonomyGrantId, cancellationToken);

    public async Task AddAsync(AutonomyGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        await _dbContext.AutonomyGrants.AddAsync(grant, cancellationToken).ConfigureAwait(false);
    }
}
