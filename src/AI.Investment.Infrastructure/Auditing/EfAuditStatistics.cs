using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Auditing;

/// <summary>Counts denials and failures for one capability, from the audit trail itself.</summary>
/// <remarks>
/// <para>
/// Two counts over <c>audit_records</c>, narrowed on capability and on the window. No new table, no
/// projection and no cache: the trail is the record of what happened, and a derived counter would be
/// a second account that could drift from it silently.
/// </para>
/// <para>
/// <strong>The window is closed at both ends.</strong> An open-ended count would include records
/// written after the instant the breaker is reasoning about, which on a busy installation means two
/// grants examined in the same sweep could be judged against different amounts of history.
/// </para>
/// <para>
/// Untracked reads. Nothing here modifies an audit record, and the change tracker holding one would
/// be an opportunity to.
/// </para>
/// </remarks>
public sealed class EfAuditStatistics : IAuditStatistics
{
    private readonly AppDbContext _dbContext;

    public EfAuditStatistics(AppDbContext dbContext) =>
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<CapabilityIncidents> CountIncidentsAsync(
        Capability capability,
        DateTime sinceUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (nowUtc < sinceUtc)
        {
            // An inverted window is a caller defect, and answering it with zeros would report "all
            // clear" for a question nobody could have asked on purpose.
            throw new ArgumentOutOfRangeException(
                nameof(sinceUtc),
                sinceUtc,
                "A counting window may not end before it starts.");
        }

        var window = _dbContext.AuditRecords
            .AsNoTracking()
            .Where(record => record.Capability == capability)
            .Where(record => record.OccurredAtUtc >= sinceUtc && record.OccurredAtUtc <= nowUtc);

        var breaches = await window
            .CountAsync(record => record.EventType == AuditEventType.ActionDenied, cancellationToken)
            .ConfigureAwait(false);

        var failures = await window
            .CountAsync(record => record.EventType == AuditEventType.ActionFailed, cancellationToken)
            .ConfigureAwait(false);

        return new CapabilityIncidents(breaches, failures);
    }
}
