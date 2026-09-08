using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Finds the ingestion run or runs that archived a given payload.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IIngestionRunStore"/> on purpose, and deliberately holding exactly one
/// method. That store answers operational questions - has this source been answering, what ran
/// recently - and every one of its members is about a source or a window. This asks something
/// different: which run produced these exact bytes. Widening the existing interface would also have
/// obliged every implementation of it to answer a question it has no business answering.
/// </para>
/// <para>
/// <strong>The content hash is the identifier, not the request fingerprint.</strong> A fingerprint
/// identifies an intention - this source, this category, this subject, this window - and several
/// runs can share one, because a retry after a failure is the same request made twice. A content
/// hash identifies the bytes themselves and is verifiable against them, which is the stronger
/// deterministic identifier already in the model and the one a replay actually needs: replay is
/// defined by the payload it re-reads.
/// </para>
/// <para>
/// <strong>It returns a list, and that is not defensiveness.</strong> The archive is
/// content-addressed, so identical bytes are stored once and referenced by every run that received
/// them. Five members of the sealed universe whose provider answer was the two-byte document
/// <c>[]</c> share a single archived payload between them. A signature returning one run would have
/// had to pick one of those five silently, and a caller acting on the wrong one would attribute
/// observations to the wrong subject. Ambiguity is reported, never resolved here.
/// </para>
/// </remarks>
public interface IArchivedRunLookup
{
    /// <summary>
    /// Every run that recorded this payload among its artifacts, oldest first.
    /// </summary>
    /// <remarks>
    /// Ordered by start time and then by identity, so the same archive and the same database
    /// always produce the same sequence. A caller that refuses ambiguity needs the count; a caller
    /// that reports it needs the order to be stable between runs.
    /// </remarks>
    Task<IReadOnlyList<IngestionRun>> RunsForArchivedPayloadAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default);
}
