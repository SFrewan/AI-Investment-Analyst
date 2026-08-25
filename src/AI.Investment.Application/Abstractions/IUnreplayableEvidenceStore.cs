using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;

namespace AI.Investment.Application.Abstractions;

/// <summary>Records payloads deleted under licence. Append-only.</summary>
/// <remarks>
/// Written before the deletion it describes, never after. A crash between the two leaves a marker
/// for a payload that still exists - conservative and self-correcting - whereas the other order
/// would leave a deleted payload with nothing recording why, which is precisely the silent gap the
/// marker exists to prevent.
/// </remarks>
public interface IUnreplayableEvidenceStore
{
    Task RecordAsync(UnreplayableEvidence marker, CancellationToken cancellationToken = default);

    Task<UnreplayableEvidence?> FindAsync(ContentHash hash, CancellationToken cancellationToken = default);

    Task<bool> IsUnreplayableAsync(ContentHash hash, CancellationToken cancellationToken = default);
}
