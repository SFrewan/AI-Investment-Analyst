using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Normalization;

/// <summary>
/// One archived payload, with everything a normaliser needs to interpret it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RetrievedAtUtc"/> and <see cref="ContentHash"/> travel with the bytes because both
/// end up in the provenance of every observation produced: when the platform fetched it, and which
/// exact bytes it read. Without the hash a claim cannot be replayed; without the retrieval time it
/// cannot be placed in history.
/// </para>
/// <para>
/// <see cref="Subject"/> is the subject of the ingestion request, not something parsed out of the
/// payload. A response says what it says; which company was asked about is the platform's own
/// knowledge, and taking it from the request keeps a mislabelled response from silently attaching
/// facts to the wrong subject.
/// </para>
/// </remarks>
/// <param name="SourceId">Where the bytes came from.</param>
/// <param name="Category">What kind of data was requested.</param>
/// <param name="Subject">What the request was about.</param>
/// <param name="ContentHash">The archived payload's address.</param>
/// <param name="Payload">The exact bytes, unparsed.</param>
/// <param name="RetrievedAtUtc">When they were fetched.</param>
public sealed record NormalizationInput(
    SourceId SourceId,
    DataCategory Category,
    IngestionSubject Subject,
    ContentHash ContentHash,
    ReadOnlyMemory<byte> Payload,
    DateTime RetrievedAtUtc);
