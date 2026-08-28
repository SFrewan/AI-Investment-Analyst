using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Opportunities;

/// <summary>Which discoverer found this opportunity, and when.</summary>
/// <remarks>
/// Recorded because "where did this come from?" is the first question asked of anything the system
/// surfaces, and because measuring a discoverer's hit rate later is impossible if its output was
/// never attributed to it.
/// </remarks>
public sealed record OpportunitySource
{
    private OpportunitySource(SourceId discovererId, DateTime discoveredAtUtc)
    {
        DiscovererId = discovererId;
        DiscoveredAtUtc = discoveredAtUtc;
    }

    /// <summary>The registered producer that found it.</summary>
    public SourceId DiscovererId { get; }

    public DateTime DiscoveredAtUtc { get; }

    public static OpportunitySource Create(SourceId discovererId, DateTime discoveredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(discovererId);
        ValueObjects.DateRange.EnsureUtc(discoveredAtUtc, nameof(discoveredAtUtc));

        return new OpportunitySource(discovererId, discoveredAtUtc);
    }

    public static OpportunitySource Create(string discovererId, DateTime discoveredAtUtc) =>
        Create(SourceId.Create(discovererId), discoveredAtUtc);

    public override string ToString() => $"{DiscovererId} @ {DiscoveredAtUtc:O}";
}
