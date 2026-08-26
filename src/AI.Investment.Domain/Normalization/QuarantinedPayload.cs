using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Normalization;

/// <summary>
/// A payload that was retrieved and archived but could not be turned into observations.
/// </summary>
/// <remarks>
/// <para>
/// Quarantine rather than discard. A payload that fails to normalise is evidence of something -
/// a provider changing a schema, a normaliser with a wrong assumption, a genuinely malformed
/// response - and every one of those is worth investigating. Dropping it silently turns all three
/// into the same symptom: data that never appeared.
/// </para>
/// <para>
/// The payload itself is untouched in the archive. This records only that reading it failed and
/// why, so a fixed normaliser can be re-run against exactly the bytes that defeated the old one.
/// </para>
/// <para>
/// Keyed by content hash: the same bytes fail the same way, and one record per payload is more
/// useful than one per attempt.
/// </para>
/// </remarks>
public sealed class QuarantinedPayload : AggregateRoot<ContentHash>
{
    public const int MaxReasonLength = 2000;
    public const int MaxRuleIdLength = 120;

    private QuarantinedPayload(
        ContentHash contentHash,
        SourceId sourceId,
        DataCategory category,
        string ruleId,
        string reason,
        DateTime quarantinedAtUtc)
        : base(contentHash)
    {
        SourceId = sourceId;
        Category = category;
        RuleId = ruleId;
        Reason = reason;
        QuarantinedAtUtc = quarantinedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private QuarantinedPayload()
    {
        SourceId = null!;
        RuleId = string.Empty;
        Reason = string.Empty;
    }

    public SourceId SourceId { get; private set; }

    public DataCategory Category { get; private set; }

    /// <summary>The versioned rule that rejected it.</summary>
    public string RuleId { get; private set; }

    /// <summary>
    /// Why, in terms safe to store permanently.
    /// </summary>
    /// <remarks>
    /// Never the payload itself, and never an excerpt of it. A malformed response is exactly the
    /// kind of thing that might contain something sensitive, and this record is long-lived and read
    /// during investigations. The bytes are already in the archive for anyone who needs them.
    /// </remarks>
    public string Reason { get; private set; }

    public DateTime QuarantinedAtUtc { get; private set; }

    public static QuarantinedPayload Record(
        ContentHash contentHash,
        SourceId sourceId,
        DataCategory category,
        string ruleId,
        string reason,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(sourceId);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new DomainValidationException(
                nameof(ruleId),
                "A quarantine must name the rule that rejected the payload, or it records that " +
                "something failed without recording what.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(
                nameof(reason),
                "A quarantine must say why. A quarantined payload with no reason is indistinguishable " +
                "from one nobody looked at.");
        }

        return new QuarantinedPayload(
            contentHash,
            sourceId,
            category,
            Truncate(ruleId, MaxRuleIdLength),
            Truncate(reason, MaxReasonLength),
            nowUtc);
    }

    public override string ToString() =>
        $"{Id.Abbreviated} quarantined [{RuleId}] {Reason}";

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
