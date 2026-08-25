using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Retention;

/// <summary>
/// A record that an archived payload was deleted under licence, and that anything referencing it
/// can no longer be replayed.
/// </summary>
/// <remarks>
/// <para>
/// The visible form of a gap. When a licence compels deletion of a payload some claim relied on,
/// the claim is preserved and this marker is written; a later replay finds the marker and reports
/// that the evidence is gone and why, instead of returning nothing and looking like an analysis
/// that found nothing.
/// </para>
/// <para>
/// Keyed by content hash rather than by claim, deliberately. One payload can underpin many claims,
/// and one row per deleted payload is both smaller and more truthful than a flag copied onto every
/// claim that touched it. It also means a claim recorded years later against the same hash is
/// correctly reported as unreplayable without anything having to go back and amend it.
/// </para>
/// <para>
/// Append-only. The evidence is not coming back, so there is nothing to update - and a marker that
/// could be removed would let a gap be closed on paper without being closed in fact.
/// </para>
/// </remarks>
public sealed class UnreplayableEvidence : AggregateRoot<ContentHash>
{
    public const int MaxReasonLength = 1000;
    public const int MaxRuleIdLength = 120;

    private UnreplayableEvidence(
        ContentHash contentHash,
        SourceId sourceId,
        string ruleId,
        string reason,
        DateTime markedAtUtc)
        : base(contentHash)
    {
        SourceId = sourceId;
        RuleId = ruleId;
        Reason = reason;
        MarkedAtUtc = markedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private UnreplayableEvidence()
    {
        SourceId = null!;
        RuleId = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>The source whose terms required the deletion.</summary>
    public SourceId SourceId { get; private set; }

    /// <summary>The retention rule that required it.</summary>
    public string RuleId { get; private set; }

    /// <summary>Why, in terms safe to store permanently.</summary>
    public string Reason { get; private set; }

    public DateTime MarkedAtUtc { get; private set; }

    public static UnreplayableEvidence Mark(
        ContentHash contentHash,
        SourceId sourceId,
        RetentionDecision decision,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(decision);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!decision.RequiresDeletion)
        {
            throw new DomainRuleViolationException(
                "UnreplayableEvidence.MarkingRequiresDeletion",
                "Evidence may only be marked unreplayable when its payload is actually being " +
                "deleted. Marking evidence that still exists would report a gap that is not there.");
        }

        return new UnreplayableEvidence(
            contentHash,
            sourceId,
            Truncate(decision.RuleId, MaxRuleIdLength, nameof(decision)),
            Truncate(decision.Reason, MaxReasonLength, nameof(decision)),
            nowUtc);
    }

    public override string ToString() =>
        $"{Id.Abbreviated} unreplayable [{RuleId}] since {MarkedAtUtc:O}";

    private static string Truncate(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                parameterName,
                "A retention decision must carry both a rule identifier and a reason; a marker " +
                "without them records a gap without recording why it exists.");
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
