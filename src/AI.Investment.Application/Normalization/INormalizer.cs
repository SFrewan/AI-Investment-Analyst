using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Normalization;

/// <summary>
/// Turns one source's payloads into observations.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="Ingestion.IDataProvider"/>: a connector knows how to <em>fetch</em>
/// a source's bytes, a normaliser knows how to <em>read</em> them. They are separate because they
/// change for different reasons - a provider moving to a new endpoint does not change what its
/// JSON means, and a provider renaming a field does not change how it is authenticated.
/// </para>
/// <para>
/// Implementations parse and nothing else. They do not fetch, do not store, do not decide whether
/// a source may be used, and do not delete anything. Given bytes, they return observations or a
/// reason the bytes could not be read.
/// </para>
/// <para>
/// A normaliser must never invent a value it did not find. Absent is absent: an observation that
/// exists only because a field was missing is worse than a gap, because a gap is visible.
/// </para>
/// </remarks>
public interface INormalizer
{
    /// <summary>Whether this normaliser reads that source's payloads for that category.</summary>
    bool CanNormalize(SourceId sourceId, DataCategory category);

    Task<NormalizationResult> NormalizeAsync(
        NormalizationInput input,
        CancellationToken cancellationToken = default);
}
