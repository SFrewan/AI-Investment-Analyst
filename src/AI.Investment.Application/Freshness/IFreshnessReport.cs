using AI.Investment.Domain.Freshness;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Freshness;

/// <summary>
/// Answers "how current is what we hold?" across every registered source.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and therefore deliberately outside the safety seam. The seam exists to gate side
/// effects; asking a question is not one, and routing reads through it would fill the audit trail
/// with rows recording that somebody looked.
/// </para>
/// <para>
/// This reports. It does not schedule, and it does not refresh. Deciding to fetch something is a
/// side effect and goes through <see cref="Ingestion.IIngestionGateway"/> like everything else;
/// what this produces is the input to that decision, which is a different thing and testable
/// without a network.
/// </para>
/// </remarks>
public interface IFreshnessReport
{
    /// <summary>Assesses every registered source, active or not.</summary>
    Task<IReadOnlyList<SourceFreshness>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Assesses one source, or returns null when it is not registered.</summary>
    Task<SourceFreshness?> GetAsync(SourceId sourceId, CancellationToken cancellationToken = default);
}

/// <summary>One source and what is known about how current its data is.</summary>
/// <param name="SourceId">Which source.</param>
/// <param name="Name">Its display name, so a report does not have to be joined to be read.</param>
/// <param name="Cadence">What it was expected to do.</param>
/// <param name="IsActive">Whether it is switched on.</param>
/// <param name="Assessment">What was concluded, and by which rule.</param>
public sealed record SourceFreshness(
    SourceId SourceId,
    string Name,
    UpdateCadence Cadence,
    bool IsActive,
    FreshnessAssessment Assessment)
{
    public bool NeedsRefresh => Assessment.NeedsRefresh;

    public override string ToString() => $"{SourceId}: {Assessment}";
}
