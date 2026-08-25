using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Retention;

/// <summary>
/// Decides what a source's licence requires of an archived payload.
/// </summary>
/// <remarks>
/// <para>
/// Pure, total, deterministic and versioned, like the policy engine and source admission. The same
/// terms, age and reference state always produce the same answer, and every answer names the rule
/// that produced it - which matters more here than anywhere else, because the outcome can be an
/// irreversible deletion whose justification must survive the data it destroyed.
/// </para>
/// <para>
/// <strong>It reads the obligation from the source's terms.</strong> There is no global retention
/// period and no configuration this engine consults. A source with no contractual cap keeps its
/// data; a source with a twelve-month clause gets twelve months; and adding a provider with a
/// different obligation is a registration rather than a change to this file. Nothing here names a
/// provider, and nothing here should ever need to.
/// </para>
/// <para>
/// <strong>The floor.</strong> A payload referenced by stored evidence is never deleted for any
/// reason other than a licence requiring it. Convenience, age and disk pressure are not reasons -
/// deleting evidence does not undo the analysis that relied on it, it only makes that analysis
/// permanently unexplainable. When a licence does compel deletion of referenced evidence, the
/// decision says so, and the caller marks the evidence unreplayable rather than letting the gap go
/// unrecorded.
/// </para>
/// </remarks>
public static class RetentionPolicy
{
    public const string NoLicensedLimitRule = "retention.no-licensed-limit@1";
    public const string WithinLicensedLimitRule = "retention.within-licensed-limit@1";
    public const string LicensedLimitExceededRule = "retention.licensed-limit-exceeded@1";

    /// <summary>
    /// Evaluates one payload against the terms of the source it came from.
    /// </summary>
    /// <param name="licensing">The source's recorded terms - the authority on its obligation.</param>
    /// <param name="retrievedAtUtc">When the payload was fetched.</param>
    /// <param name="nowUtc">The evaluation instant.</param>
    /// <param name="isReferencedByEvidence">
    /// Whether a stored claim, audit record or ingestion run still points at this payload.
    /// </param>
    public static RetentionDecision Evaluate(
        LicensingTerms licensing,
        DateTime retrievedAtUtc,
        DateTime nowUtc,
        bool isReferencedByEvidence)
    {
        ArgumentNullException.ThrowIfNull(licensing);
        DateRange.EnsureUtc(retrievedAtUtc, nameof(retrievedAtUtc));
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var limit = licensing.Retention;

        if (!limit.IsBounded)
        {
            return new RetentionDecision(
                RetentionOutcome.Retain,
                NoLicensedLimitRule,
                "The source's terms impose no retention limit, so nothing compels deletion.");
        }

        if (!limit.IsExceededBy(retrievedAtUtc, nowUtc))
        {
            return new RetentionDecision(
                RetentionOutcome.Retain,
                WithinLicensedLimitRule,
                $"Retrieved {retrievedAtUtc:O}; the licensed limit of {limit} has not been reached.");
        }

        return new RetentionDecision(
            RetentionOutcome.DeleteRequired,
            LicensedLimitExceededRule,
            $"Retrieved {retrievedAtUtc:O}, which is older than the licensed limit of {limit}. " +
            "The source's terms require deletion." +
            (isReferencedByEvidence
                ? " Stored evidence still references this payload; the evidence is preserved and " +
                  "marked unreplayable rather than the deletion being skipped."
                : string.Empty),
            RequiresEvidenceMarking: isReferencedByEvidence);
    }
}
