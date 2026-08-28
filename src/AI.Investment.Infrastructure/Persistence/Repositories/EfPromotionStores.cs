using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>Stores promotion warrants.</summary>
/// <remarks>
/// Add and read only. Revocation happens on a tracked instance through the aggregate, so there is no
/// update method here for a caller to reach for - and the write guard refuses a delete at the
/// context, so a warrant cannot be made to disappear from the record it exists to be part of.
/// </remarks>
public sealed class EfPromotionWarrantStore : IPromotionWarrantStore
{
    private readonly AppDbContext _dbContext;

    public EfPromotionWarrantStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(PromotionWarrant warrant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(warrant);

        await _dbContext.PromotionWarrants.AddAsync(warrant, cancellationToken).ConfigureAwait(false);
    }

    public Task<PromotionWarrant?> FindAsync(
        Guid promotionWarrantId,
        CancellationToken cancellationToken = default) =>
        _dbContext.PromotionWarrants
            .FirstOrDefaultAsync(w => w.PromotionWarrantId == promotionWarrantId, cancellationToken);

    public async Task<IReadOnlyList<PromotionWarrant>> GetActiveAsync(
        Capability capability,
        string environmentName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        return await _dbContext.PromotionWarrants
            .Where(w => w.Capability == capability)
            // Compared exactly, like the grant store does. The domain normalises the environment
            // name on the way in, so an inexact match here would be papering over a difference that
            // should not exist rather than tolerating one that legitimately does.
            .Where(w => w.EnvironmentName == environmentName)
            .Where(w => w.RevokedAtUtc == null && w.ExpiresAtUtc > nowUtc)
            .OrderByDescending(w => w.IssuedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionWarrant>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.PromotionWarrants
            .AsNoTracking()
            .OrderByDescending(w => w.IssuedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Stores live-venue authorisations. Expected to stay empty.</summary>
public sealed class EfLiveVenueAuthorizationStore : ILiveVenueAuthorizationStore
{
    private readonly AppDbContext _dbContext;

    public EfLiveVenueAuthorizationStore(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(
        LiveVenueAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        await _dbContext.LiveVenueAuthorizations
            .AddAsync(authorization, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<LiveVenueAuthorization?> FindAsync(
        Guid liveVenueAuthorizationId,
        CancellationToken cancellationToken = default) =>
        _dbContext.LiveVenueAuthorizations
            .FirstOrDefaultAsync(a => a.LiveVenueAuthorizationId == liveVenueAuthorizationId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Withdrawn and expired rows are returned deliberately. The gate reports which refusal applies,
    /// and cannot distinguish "there is no authorisation" from "the authorisation expired last week"
    /// if the store has already filtered the row away.
    /// </remarks>
    public Task<LiveVenueAuthorization?> FindForAsync(
        string venueId,
        string environmentName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        return _dbContext.LiveVenueAuthorizations
            .Where(a => a.VenueId == venueId)
            .Where(a => a.EnvironmentName == environmentName)
            .OrderByDescending(a => a.AuthorisedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LiveVenueAuthorization>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        await _dbContext.LiveVenueAuthorizations
            .AsNoTracking()
            .OrderByDescending(a => a.AuthorisedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
