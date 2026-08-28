using AI.Investment.Domain.Autonomy;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// The resolved autonomy for the unattended work currently in flight on this scope.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism that makes "a cycle-driven proposal always carries a resolution" true rather than
/// conventional. The cycle runner enters a scope before it proposes anything, the policy context
/// provider reads it when assembling the context, and the policy engine refuses any proposal
/// carrying a cycle identifier that arrives without one.
/// </para>
/// <para>
/// Scoped, not ambient across threads, for the same reason <c>IWriteAuthorization</c> is: an async
/// local would flow into background work started inside the scope and quietly lend it an autonomy
/// nobody resolved for it.
/// </para>
/// </remarks>
public interface IAutonomyContext
{
    /// <summary>The resolution in force, or null when the work is attended.</summary>
    AutonomyResolution? Current { get; }

    /// <summary>The cycle the resolution was made for, when there is one.</summary>
    Guid? CycleId { get; }

    /// <summary>
    /// Opens a scope in which unattended work runs under <paramref name="resolution"/>. Disposing
    /// the handle closes it.
    /// </summary>
    IDisposable Enter(Guid cycleId, AutonomyResolution resolution);
}
