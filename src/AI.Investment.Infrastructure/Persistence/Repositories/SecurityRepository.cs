using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>EF Core implementation of the security repository.</summary>
/// <remarks>
/// The identifier lookup includes the owned collection deliberately. A caller that found a security
/// by one of its identifiers almost always needs to know what else was asserted about it, and
/// returning a security whose identifiers are silently empty would look like an instrument with no
/// names rather than one whose names were not loaded.
/// </remarks>
public sealed class SecurityRepository : ISecurityRepository
{
    private readonly AppDbContext _dbContext;

    public SecurityRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<Security?> GetByIdAsync(
        SecurityId id,
        CancellationToken cancellationToken = default) =>
        await _dbContext.Securities
            .Include(s => s.Identifiers)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Security?> FindByIdentifierAsync(
        SecurityIdentifierKind kind,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalised = value.Trim();

        return await _dbContext.Securities
            .Include(s => s.Identifiers)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Identifiers.Any(i => i.Kind == kind && i.Value == normalised),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SecurityId>> FindByIdentifierAsAtAsync(
        SecurityIdentifierKind kind,
        string value,
        SourceId source,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(source);

        var normalised = value.Trim();

        // The interval test is the same one SecurityIdentifier.CoversDate applies, written as a
        // predicate the provider can translate: inclusive at both ends, and a null ValidTo means no
        // observed end rather than an end in the far future.
        return await _dbContext.Securities
            .AsNoTracking()
            .Where(s => s.Identifiers.Any(i =>
                i.Kind == kind &&
                i.Value == normalised &&
                i.SourceId == source &&
                i.ValidFrom <= asOf &&
                (i.ValidTo == null || asOf <= i.ValidTo)))
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(int OfKind, int OfKindAndValue)> CountIdentifierAssertionsAsync(
        SecurityIdentifierKind kind,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalised = value.Trim();

        var ofKind = await _dbContext.Securities
            .AsNoTracking()
            .CountAsync(s => s.Identifiers.Any(i => i.Kind == kind), cancellationToken)
            .ConfigureAwait(false);

        if (ofKind == 0)
        {
            return (0, 0);
        }

        var ofKindAndValue = await _dbContext.Securities
            .AsNoTracking()
            .CountAsync(
                s => s.Identifiers.Any(i => i.Kind == kind && i.Value == normalised),
                cancellationToken)
            .ConfigureAwait(false);

        return (ofKind, ofKindAndValue);
    }

    public async Task<bool> AnyIdentifierFromSourceAsync(
        SecurityIdentifierKind kind,
        string value,
        SourceId source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(source);

        var normalised = value.Trim();

        return await _dbContext.Securities
            .AsNoTracking()
            .AnyAsync(
                s => s.Identifiers.Any(i =>
                    i.Kind == kind && i.Value == normalised && i.SourceId == source),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Add(Security security)
    {
        ArgumentNullException.ThrowIfNull(security);
        _dbContext.Securities.Add(security);
    }
}
