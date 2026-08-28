using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Autonomy;

namespace AI.Investment.Application.Operations;

/// <summary>
/// Holds the resolved autonomy for the unattended work in flight on one scope.
/// </summary>
/// <remarks>
/// <para>
/// State in an instance field rather than an <c>AsyncLocal</c>, for the same reason
/// <c>ScopedWriteAuthorization</c> makes that choice: an async local flows into background work
/// started inside the scope, which would lend a fire-and-forget task an autonomy nobody resolved for
/// it - and background work started by an autonomous cycle is exactly the shape of thing that would
/// happen.
/// </para>
/// <para>
/// Scopes do not nest. A second one would mean two resolutions were live at once and the policy
/// context could not say which governed a given proposal, which is the same ambiguity the
/// authorisation window refuses for the same reason.
/// </para>
/// </remarks>
public sealed class AutonomyContext : IAutonomyContext
{
    private AutonomyResolution? _current;
    private Guid? _cycleId;

    public AutonomyResolution? Current => _current;

    public Guid? CycleId => _cycleId;

    public IDisposable Enter(Guid cycleId, AutonomyResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (cycleId == Guid.Empty)
        {
            throw new ArgumentException(
                "An autonomy scope belongs to a specific cycle. Entering one without a cycle would " +
                "make the policy engine's structural check - that a cycle-driven proposal carries a " +
                "resolution - satisfiable by anything.",
                nameof(cycleId));
        }

        if (_current is not null)
        {
            throw new InvalidOperationException(
                $"An autonomy scope is already open for cycle {_cycleId}. Scopes do not nest: two " +
                "live resolutions would make it ambiguous which one governed a proposal.");
        }

        _current = resolution;
        _cycleId = cycleId;

        return new Scope(this);
    }

    private void Exit()
    {
        _current = null;
        _cycleId = null;
    }

    private sealed class Scope : IDisposable
    {
        private AutonomyContext? _owner;

        public Scope(AutonomyContext owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.Exit();
            _owner = null;
        }
    }
}
