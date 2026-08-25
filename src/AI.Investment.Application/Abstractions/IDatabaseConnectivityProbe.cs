namespace AI.Investment.Application.Abstractions;

/// <summary>Reports whether the primary data store is reachable.</summary>
/// <remarks>
/// Exists so the API's readiness health check can test the database without the API project
/// referencing a persistence type. The API's only permitted contact with Infrastructure is
/// registration in the composition root; everything else goes through an Application
/// abstraction, and this is one.
/// </remarks>
public interface IDatabaseConnectivityProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}
