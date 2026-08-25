namespace AI.Investment.Application.Abstractions;

/// <summary>Remembers which action keys have already been performed.</summary>
/// <remarks>
/// Retries are the normal case in an unattended system, not an exception. "It retried and did
/// the thing twice" is the most likely way this platform first causes real harm - ahead of any
/// failure of analysis - so deduplication is part of the seam from the beginning rather than
/// something added to the execution layer later.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims a key. Returns true if this caller claimed it and should proceed;
    /// false if it was already claimed and the effect must NOT be repeated.
    /// </summary>
    Task<bool> TryClaimAsync(
        string idempotencyKey,
        Guid proposalId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
