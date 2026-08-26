using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;

namespace AI.Investment.Application.Abstractions;

/// <summary>Records payloads that could not be read. Append-only.</summary>
public interface IQuarantineStore
{
    Task RecordAsync(QuarantinedPayload payload, CancellationToken cancellationToken = default);

    Task<bool> IsQuarantinedAsync(ContentHash hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recently quarantined payloads, newest first - the queue an operator works through.
    /// </summary>
    Task<IReadOnlyList<QuarantinedPayload>> GetRecentAsync(
        int take,
        CancellationToken cancellationToken = default);
}
