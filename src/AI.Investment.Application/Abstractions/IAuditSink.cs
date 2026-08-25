using AI.Investment.Domain.Auditing;

namespace AI.Investment.Application.Abstractions;

/// <summary>Writes an entry to the append-only audit trail.</summary>
/// <remarks>
/// There is deliberately no read, update or delete method on this interface. Reading the audit
/// trail is a separate concern with a separate contract; modifying it is not a concern at all.
/// </remarks>
public interface IAuditSink
{
    Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
