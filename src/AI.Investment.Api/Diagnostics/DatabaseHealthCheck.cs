using AI.Investment.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AI.Investment.Api.Diagnostics;

/// <summary>Readiness check: is the database reachable?</summary>
/// <remarks>
/// Depends on an Application abstraction rather than on a persistence type, so the API keeps its
/// rule that Infrastructure is touched only in the composition root.
/// </remarks>
internal sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDatabaseConnectivityProbe _probe;

    public DatabaseHealthCheck(IDatabaseConnectivityProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var reachable = await _probe.CanConnectAsync(cancellationToken).ConfigureAwait(false);

            return reachable
                ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
        }
#pragma warning disable CA1031 // A health check reports; it never propagates. Any failure to
                              // reach the database is an unhealthy result, not an exception
                              // escaping into the health endpoint.
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connectivity check failed.", ex);
        }
#pragma warning restore CA1031
    }
}
