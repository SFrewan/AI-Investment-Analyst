using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;

namespace AI.Investment.Application.Retention;

/// <summary>
/// Applies each source's licensed retention obligation to archived payloads.
/// </summary>
/// <remarks>
/// <para>
/// Per payload rather than per sweep, on purpose. Deciding what one payload requires is pure and
/// testable; deciding <em>when</em> to walk the archive is scheduling, and belongs with whatever
/// runs recurring work. Keeping them apart means the rule that deletes evidence can be exercised
/// exhaustively without a scheduler, a clock or a filesystem full of fixtures.
/// </para>
/// <para>
/// Returns the decision whether or not anything was deleted, so a caller can report what a sweep
/// concluded rather than only what it changed.
/// </para>
/// </remarks>
public interface IRetentionEnforcer
{
    Task<RetentionDecision> EnforceAsync(ContentHash hash, CancellationToken cancellationToken = default);
}
