using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;

namespace AI.Investment.Application.Ingestion;

/// <summary>One ingestion run as returned across the application boundary.</summary>
/// <remarks>
/// <para>
/// <see cref="RefusalRuleId"/> is on the wire for the same reason the run records it: when data
/// does not appear, "which rule stopped this?" is the question, and an outcome of <c>Refused</c>
/// with no rule turns a compliance decision into an unexplained absence.
/// </para>
/// <para>
/// <see cref="ArtifactCount"/> rather than the hashes themselves. A caller reading a list of runs
/// wants to know whether anything was archived; the hashes are the archive's addressing scheme and
/// belong to whatever replays a run, not to a status listing.
/// </para>
/// </remarks>
public sealed record IngestionRunDto(
    Guid Id,
    string SourceId,
    string Category,
    string SubjectKind,
    string? SubjectIdentifier,
    string Outcome,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    int ArtifactCount,
    string? RefusalRuleId,
    string? Reason);

/// <summary>One quarantined payload as returned across the application boundary.</summary>
/// <remarks>
/// The operator's queue. <see cref="Reason"/> never contains an excerpt of the payload - see
/// <see cref="QuarantinedPayload.Reason"/> - so this shape is safe to render anywhere the rest of
/// the status surface is.
/// </remarks>
public sealed record QuarantinedPayloadDto(
    string ContentHash,
    string SourceId,
    string Category,
    string RuleId,
    string Reason,
    DateTime QuarantinedAtUtc);

/// <summary>Maps ingestion and quarantine records to their wire shapes.</summary>
public static class IngestionMapper
{
    public static IngestionRunDto ToDto(IngestionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new IngestionRunDto(
            run.Id.Value,
            run.Request.SourceId.Value,
            run.Request.Category.ToString(),
            run.Request.Subject.Kind,
            run.Request.Subject.Identifier,
            run.Outcome.ToString(),
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.Artifacts.Count,
            run.RefusalRuleId,
            run.Reason);
    }

    public static QuarantinedPayloadDto ToDto(QuarantinedPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new QuarantinedPayloadDto(
            payload.Id.Value,
            payload.SourceId.Value,
            payload.Category.ToString(),
            payload.RuleId,
            payload.Reason,
            payload.QuarantinedAtUtc);
    }
}
