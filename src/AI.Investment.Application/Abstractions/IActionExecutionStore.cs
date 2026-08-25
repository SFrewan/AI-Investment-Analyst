using AI.Investment.Domain.Actions;

namespace AI.Investment.Application.Abstractions;

/// <summary>Persists completed action executions.</summary>
/// <remarks>
/// Separate from <see cref="IAuditSink"/> because the two answer different questions. The audit
/// trail is the narrative - what was decided, by whom, why. This is the operational ledger of
/// effects actually attempted, which is what a later phase queries to measure outcomes and to
/// detect a capability whose failure rate is climbing.
/// <para>Append-only: there is no update and no delete.</para>
/// </remarks>
public interface IActionExecutionStore
{
    Task RecordAsync(ActionExecution execution, CancellationToken cancellationToken = default);
}
