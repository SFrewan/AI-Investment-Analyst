using System.Collections.Concurrent;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Infrastructure.Ingestion;

/// <summary>
/// Keeps requests within a declared quota, using an in-memory sliding window per source.
/// </summary>
/// <remarks>
/// <para>
/// A sliding window rather than a fixed one, deliberately. A fixed window permits a full quota at
/// the end of one interval and another immediately at the start of the next - twice the declared
/// rate across the boundary. A provider enforcing its own limit sees that burst as a violation,
/// and it would be a violation.
/// </para>
/// <para>
/// <strong>Per process, not distributed.</strong> Two instances of this application will each
/// keep to the quota and together exceed it. That is stated rather than hidden: a shared limiter
/// needs shared state, which is a real piece of infrastructure and belongs to whatever phase
/// introduces horizontal scale. Until then a single instance is the deployment this is correct
/// for, and the ceiling is the SEC's published one rather than something inferred.
/// </para>
/// <para>
/// Never waits. <see cref="TryAcquireAsync"/> answers immediately and the gateway records a
/// refusal, so a saturated provider produces a visible ledger entry that a scheduler can retry -
/// not a thread parked for an unknown period while the rest of a batch waits behind it.
/// </para>
/// </remarks>
public sealed class SlidingWindowRateLimiter : IProviderRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public Task<bool> TryAcquireAsync(
        SourceId sourceId,
        ProviderQuota quota,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(quota);

        var window = _windows.GetOrAdd(sourceId.Value, static _ => new Window());

        return Task.FromResult(window.TryAcquire(quota, nowUtc));
    }

    private sealed class Window
    {
        private readonly Queue<DateTime> _grants = new();

        public bool TryAcquire(ProviderQuota quota, DateTime nowUtc)
        {
            lock (_grants)
            {
                var cutoff = nowUtc - quota.Window;

                while (_grants.Count > 0 && _grants.Peek() <= cutoff)
                {
                    _grants.Dequeue();
                }

                if (_grants.Count >= quota.MaxRequests)
                {
                    return false;
                }

                _grants.Enqueue(nowUtc);

                return true;
            }
        }
    }
}
