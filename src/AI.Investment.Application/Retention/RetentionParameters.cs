using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Retention;

/// <summary>
/// A proposed retention deletion, as the safety seam sees it.
/// </summary>
/// <remarks>
/// <see cref="Describe"/> is written into the audit trail, and it is the record that outlives the
/// payload: which bytes, from which source, under which rule, and why. Once the deletion happens
/// this text is the only remaining account of it, so it states the justification in full rather
/// than referring to something that will still be readable.
/// </remarks>
public sealed record RetentionParameters : IActionParameters
{
    public RetentionParameters(ContentHash contentHash, SourceId sourceId, RetentionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(decision);

        ContentHash = contentHash;
        SourceId = sourceId;
        Decision = decision;
    }

    public ContentHash ContentHash { get; }

    public SourceId SourceId { get; }

    public RetentionDecision Decision { get; }

    public string Describe() =>
        $"Delete archived payload {ContentHash.Value} from {SourceId} under {Decision.RuleId}: " +
        $"{Decision.Reason}" +
        (Decision.RequiresEvidenceMarking
            ? " Referenced evidence is preserved and marked unreplayable."
            : " No stored evidence references this payload.");
}
