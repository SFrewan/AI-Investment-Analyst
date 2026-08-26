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
/// Returns a result whether or not anything was deleted, so a caller can report what a sweep
/// concluded rather than only what it changed.
/// </para>
/// <para>
/// <strong>The result carries both the obligation and the effect</strong>, because they can
/// disagree. Deletion declares itself irreversible and therefore requires approval unless an
/// installation has deliberately granted otherwise, so "the licence required this payload gone"
/// and "the payload is gone" are different facts. A caller told only the first would record a
/// compliance obligation as discharged while the payload is still on disk.
/// </para>
/// </remarks>
public interface IRetentionEnforcer
{
    Task<RetentionEnforcementResult> EnforceAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default);
}
