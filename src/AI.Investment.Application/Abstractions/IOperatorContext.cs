using AI.Investment.Application.Operators;

namespace AI.Investment.Application.Abstractions;

/// <summary>Who is making the current request, when anybody is.</summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="ICorrelationContext"/>, and for the same reason: an ambient fact
/// about the work in flight that the application layer needs and the transport owns. The API
/// implements it from the authenticated principal; a background worker has no operator and
/// implements it as null.
/// </para>
/// <para>
/// <strong>Null means nobody, and nobody may do nothing.</strong> Every operator action refuses a
/// null identity before it looks at anything else. An implementation that invented a placeholder
/// identity to avoid the null would put a name nobody owns into the audit trail, which is worse
/// than a refusal.
/// </para>
/// </remarks>
public interface IOperatorContext
{
    /// <summary>The authenticated operator, or null when the caller is anonymous.</summary>
    OperatorIdentity? Current { get; }
}
