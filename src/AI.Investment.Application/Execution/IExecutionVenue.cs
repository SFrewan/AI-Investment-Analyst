namespace AI.Investment.Application.Execution;

/// <summary>
/// Where an order is actually placed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Simulation first, and simulation on the same path.</strong> A simulated venue is the only
/// implementation registered in this phase, and it is reached through exactly the machinery a real
/// one would be - the same proposal, the same policy decision, the same approval token, the same
/// ledger entries, the same audit records. A simulation that takes a different path proves nothing
/// about production, which is the usual reason paper trading is reassuring and then wrong.
/// </para>
/// <para>
/// Registering a real venue is a separate, formal decision gated behind the validation phase. Until
/// then <see cref="IsSimulated"/> is true for every registered implementation, and an architecture
/// test says so.
/// </para>
/// </remarks>
public interface IExecutionVenue
{
    /// <summary>The venue's identifier, recorded on every fill.</summary>
    string VenueId { get; }

    /// <summary>False only for a venue that moves real money.</summary>
    bool IsSimulated { get; }

    /// <summary>Places the order, or refuses it with a reason.</summary>
    Task<VenueResult> PlaceAsync(VenueOrder order, CancellationToken cancellationToken = default);
}
