using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Infrastructure.Persistence;

namespace AI.Investment.Infrastructure.Auditing;

/// <summary>Writes audit records to the database immediately.</summary>
/// <remarks>
/// <para>
/// Committed on its own rather than deferred to the caller's unit of work, and deliberately so:
/// a denial produces an audit record but no other write, and a failing effect produces an audit
/// record precisely because its transaction is about to be abandoned. An audit trail that shares
/// the fate of the thing it is recording is not an audit trail.
/// </para>
/// <para>
/// The consequence, accepted for Phase 1: an audit write and the effect it describes are not
/// atomic with each other. A crash between them leaves a decision recorded with no execution,
/// which is the safe direction to fail - the trail over-reports intent rather than
/// under-reporting action. A transactional outbox makes this exact when continuous operation
/// arrives.
/// </para>
/// </remarks>
public sealed class EfAuditSink : IAuditSink
{
    private readonly AppDbContext _dbContext;

    public EfAuditSink(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _dbContext.AuditRecords.AddAsync(record, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesInternalAsync(cancellationToken).ConfigureAwait(false);
    }
}
