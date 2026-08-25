using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Infrastructure.Actions;

/// <summary>
/// Tracks whether an authorised action execution is open, for the lifetime of one scope.
/// </summary>
/// <remarks>
/// <para>
/// Registered as scoped, so the window belongs to one request or one operating cycle and cannot
/// leak into another. State lives in an instance field rather than an AsyncLocal: an async local
/// would flow into background work started inside the window and silently authorise writes there
/// too.
/// </para>
/// <para>
/// Windows do not nest. Attempting to open a second one throws rather than silently reusing the
/// first, because nesting would mean two decisions were live at once and the persistence layer
/// could not say which one authorised a given write.
/// </para>
/// </remarks>
public sealed class ScopedWriteAuthorization : IWriteAuthorization
{
    private PolicyDecision? _current;

    public bool IsAuthorized => _current is not null;

    public Guid? AuthorizingDecisionId => _current?.DecisionId;

    public IDisposable Authorize(PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Outcome != PolicyOutcome.Execute)
        {
            throw new InvalidOperationException(
                $"Cannot authorise writes from a decision whose outcome is {decision.Outcome}. " +
                "Only a decision permitting execution opens an authorisation window.");
        }

        if (_current is not null)
        {
            throw new InvalidOperationException(
                $"An authorisation window is already open for decision {_current.DecisionId}. " +
                "Windows do not nest: two live decisions would make it ambiguous which one " +
                "authorised a given write.");
        }

        _current = decision;
        return new Window(this);
    }

    private void Close() => _current = null;

    private sealed class Window : IDisposable
    {
        private ScopedWriteAuthorization? _owner;

        public Window(ScopedWriteAuthorization owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.Close();
            _owner = null;
        }
    }
}
