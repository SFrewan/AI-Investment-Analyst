using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Answers whether stored evidence still points at an archived payload.
/// </summary>
/// <remarks>
/// The retention floor depends on this answer, so it errs toward "yes". A false positive keeps a
/// payload that could have been deleted, which costs disk; a false negative deletes evidence
/// something relied on, which cannot be undone. An implementation that cannot determine the answer
/// should say true.
/// </remarks>
public interface IPayloadReferenceIndex
{
    /// <summary>
    /// True when any ingestion run, claim or audit record references <paramref name="hash"/>.
    /// </summary>
    Task<bool> IsReferencedAsync(ContentHash hash, CancellationToken cancellationToken = default);
}
