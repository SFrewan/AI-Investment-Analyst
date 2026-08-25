using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Ingestion;

/// <summary>Keeps requests within the rate a provider declares.</summary>
/// <remarks>
/// <para>
/// Staying under a declared quota is compliance with a provider's terms; backing off after a 429
/// is reacting to enforcement. The platform is required to do the former, which is why this is
/// consulted before a fetch rather than wrapped around a failure.
/// </para>
/// <para>
/// <see cref="TryAcquireAsync"/> does not wait. A blocked ingestion run is recorded as refused,
/// with the rule that refused it, and retried later by whatever scheduled it - so a saturated
/// provider produces a visible ledger entry rather than a thread parked for an unknown period.
/// </para>
/// </remarks>
public interface IProviderRateLimiter
{
    /// <summary>
    /// Reserves one request against <paramref name="quota"/>, returning false when the quota is
    /// already spent.
    /// </summary>
    Task<bool> TryAcquireAsync(
        SourceId sourceId,
        ProviderQuota quota,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
