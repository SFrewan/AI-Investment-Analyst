using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Normalization;

/// <summary>Proposed recording of observations, as the safety seam sees it.</summary>
/// <remarks>
/// Describes the shape of what is being recorded - how many observations, from which source and
/// category - rather than the values themselves. The audit trail is append-only and cannot be
/// redacted, and a provider's raw content is exactly what should not be copied into it.
/// </remarks>
public sealed record NormalizationParameters : IActionParameters
{
    public NormalizationParameters(SourceId sourceId, DataCategory category, int observationCount)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        SourceId = sourceId;
        Category = category;
        ObservationCount = observationCount;
    }

    public SourceId SourceId { get; }

    public DataCategory Category { get; }

    public int ObservationCount { get; }

    public string Describe() =>
        $"Record {ObservationCount} {Category} observations normalised from {SourceId}.";
}
