using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Opportunities;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>
/// Stores approval tokens, and consumes each one exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConsumeAsync"/> is the interesting method. It reloads the token from the database
/// rather than trusting anything already tracked, applies the domain's own checks, and then writes
/// with a filter on the row still being unconsumed. If no row is affected, another caller consumed
/// it between the read and the write, and this call reports <see cref="ApprovalRefusal.AlreadyConsumed"/>
/// rather than proceeding.
/// </para>
/// <para>
/// That is the difference between single use as an intention and single use as a fact. "It retried
/// and bought twice" is the most likely way this platform loses money first, and it happens in
/// exactly this window.
/// </para>
/// </remarks>
public sealed class EfApprovalTokenStore : IApprovalTokenStore
{
    private readonly AppDbContext _dbContext;

    public EfApprovalTokenStore(AppDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(ApprovalToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        var tracked = _dbContext.ApprovalTokens.Local
            .FirstOrDefault(candidate => candidate.ApprovalTokenId == token.ApprovalTokenId);

        if (tracked is not null)
        {
            return;
        }

        var existing = await _dbContext.ApprovalTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.ApprovalTokenId == token.ApprovalTokenId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await _dbContext.ApprovalTokens.AddAsync(token, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _dbContext.Entry(token).State = EntityState.Modified;
        }

        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ApprovalToken?> GetAsync(Guid approvalTokenId, CancellationToken cancellationToken = default) =>
        _dbContext.ApprovalTokens
            .FirstOrDefaultAsync(candidate => candidate.ApprovalTokenId == approvalTokenId, cancellationToken);

    public async Task<ApprovalRefusal> ConsumeAsync(
        Guid approvalTokenId,
        OpportunityId opportunityId,
        ActionProposal proposal,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var token = await _dbContext.ApprovalTokens
            .FirstOrDefaultAsync(candidate => candidate.ApprovalTokenId == approvalTokenId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return ApprovalRefusal.Revoked;
        }

        var refusal = token.Check(opportunityId, proposal, nowUtc);

        if (refusal != ApprovalRefusal.None)
        {
            return refusal;
        }

        // The conditional write. Another caller may have consumed the token since the read above,
        // and the only place that can be settled is the database.
        var affected = await _dbContext.ApprovalTokens
            .Where(candidate =>
                candidate.ApprovalTokenId == approvalTokenId &&
                candidate.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(
                update => update.SetProperty(candidate => candidate.ConsumedAtUtc, nowUtc),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            return ApprovalRefusal.AlreadyConsumed;
        }

        // Keep the tracked instance consistent with the row that was just written, so a caller
        // holding it does not see an unconsumed token that the database says is spent.
        await _dbContext.Entry(token).ReloadAsync(cancellationToken).ConfigureAwait(false);

        return ApprovalRefusal.None;
    }
}
