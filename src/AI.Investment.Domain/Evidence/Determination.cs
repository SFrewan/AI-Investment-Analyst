using System.Text;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Evidence;

/// <summary>
/// A value the platform derived, recorded so it can be reproduced rather than trusted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A sibling to <c>Observation</c>, deliberately, and never a widening of it.</strong>
/// <c>Observation.RecordFact</c> is the only recording path there and <c>ToClaim()</c> refuses every
/// non-Fact kind, so a derived value has had nowhere to live. Widening the observation table would
/// have made it possible for a conclusion to occupy the same shape as an observation, and the single
/// most valuable property the evidence base has is that a fact cannot be confused with a conclusion.
/// Two tables cannot be confused; one nullable column can.
/// </para>
/// <para>
/// <strong>It names its inputs by content hash, never by query.</strong> There is no foreign key
/// here to an observation, a security, a universe or a company - and that is the design, not an
/// omission. A row reference resolves to whatever that row says <em>now</em>; a hash resolves to the
/// bytes that were actually used. Reproducibility requires the second, and a determination that
/// cannot be reproduced is an assertion wearing the clothes of evidence.
/// </para>
/// <para>
/// <strong>Input order is part of the contract.</strong> The inputs are kept exactly as given and
/// are never sorted: a rule that consumes a sequence can produce a different answer from a different
/// order, so re-ordering them would quietly change what was computed while leaving the record
/// looking identical. <see cref="ComputeInputsHash"/> is order-sensitive for the same reason.
/// </para>
/// <para>
/// <strong>Append-only.</strong> No setter, no mutator, no correction path. A determination that
/// turned out to be wrong was produced by a rule version that was wrong, and the answer is a new row
/// naming a new version - never an edit, which would rewrite what the platform concluded at a past
/// instant.
/// </para>
/// </remarks>
public sealed class Determination
{
    public const int MaxRuleIdLength = 120;
    public const int MaxRuleVersionLength = 40;
    public const int MaxOutputLength = 8000;

    /// <summary>Separates input hashes when they are folded into one canonical digest.</summary>
    /// <remarks>
    /// A newline, and the hashes either side of it are lower-case hex of a fixed length, so the
    /// joined string has no separator ambiguity and no dependence on culture, locale or numeric
    /// formatting. Nothing here formats a number or a date.
    /// </remarks>
    private const char InputSeparator = '\n';

    private readonly List<ContentHash> _inputs;

    private Determination(
        DeterminationId id,
        ClaimKind kind,
        string ruleId,
        string ruleVersion,
        List<ContentHash> inputs,
        string outputCanonical,
        ContentHash outputContentHash,
        DateTime asOfUtc,
        Confidence? confidence,
        DateTime recordedAtUtc)
    {
        Id = id;
        Kind = kind;
        RuleId = ruleId;
        RuleVersion = ruleVersion;
        _inputs = inputs;
        InputsHash = ComputeInputsHash(inputs).Value;
        OutputCanonical = outputCanonical;
        OutputContentHash = outputContentHash;
        AsOfUtc = asOfUtc;
        Confidence = confidence;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Determination()
    {
        _inputs = [];
        InputsHash = string.Empty;
        RuleId = string.Empty;
        RuleVersion = string.Empty;
        OutputCanonical = string.Empty;
        OutputContentHash = null!;
    }

    public DeterminationId Id { get; private set; }

    /// <summary>
    /// The epistemic status of this value. Restricted to <see cref="ClaimKind.Calculation"/> today.
    /// </summary>
    /// <remarks>
    /// The store's shape admits the inference kinds, and they are refused here until the stage that
    /// introduces them. Accepting one now would mean storing a value whose confidence rules nothing
    /// yet enforces, which is how an inference ends up read as a calculation.
    /// </remarks>
    public ClaimKind Kind { get; private set; }

    /// <summary>Which rule produced this. Named, so the answer can be traced to a rule.</summary>
    public string RuleId { get; private set; }

    /// <summary>That rule's version. A rule without a version cannot be reproduced.</summary>
    public string RuleVersion { get; private set; }

    /// <summary>
    /// The inputs, by content hash, in the order the rule consumed them.
    /// </summary>
    /// <remarks>
    /// Wrapped rather than returned directly. Handing back the list itself types as read-only but
    /// casts back to <c>IList</c>, so a caller could append to the very collection the
    /// reproducibility digest was computed over and leave the row describing inputs it never used.
    /// </remarks>
    public IReadOnlyList<ContentHash> Inputs => _inputs.AsReadOnly();

    /// <summary>The result, in a canonical form that recomputation must reproduce exactly.</summary>
    public string OutputCanonical { get; private set; }

    /// <summary>The hash of <see cref="OutputCanonical"/>, so "byte-equal" is a comparison.</summary>
    public ContentHash OutputContentHash { get; private set; }

    /// <summary>
    /// The instant this determination describes. Inputs were selected as published by then.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="RecordedAtUtc"/> on purpose. Collapsing them would answer "what do we
    /// now believe was true then" instead of "what could have been concluded then", which is the
    /// question a replay must never be allowed to substitute.
    /// </remarks>
    public DateTime AsOfUtc { get; private set; }

    /// <summary>
    /// Always null for a calculation, and present for the inference kinds when they arrive.
    /// </summary>
    /// <remarks>
    /// A calculation is exact given its inputs; uncertainty belongs to the claims it derives from.
    /// <c>Claim.Validate</c> enforces the same rule on the claim side, and the two agree rather than
    /// one relaxing what the other refuses.
    /// </remarks>
    public Confidence? Confidence { get; private set; }

    /// <summary>When this row was written here.</summary>
    public DateTime RecordedAtUtc { get; private set; }

    /// <summary>
    /// The order-sensitive digest of the input hashes. The fourth part of the reproducibility key.
    /// </summary>
    /// <remarks>
    /// Computed once, at recording, and then stored rather than recomputed on every read. A derived
    /// value that is recomputed on read would be computed by <em>this</em> build; the stored one is
    /// what the row was actually keyed on, and <see cref="InputsHashMatchesInputs"/> is how the two
    /// are held to each other.
    /// </remarks>
    public string InputsHash { get; private set; }

    /// <summary>
    /// Records a determination. Every argument is required except the confidence, which a
    /// calculation may not carry at all.
    /// </summary>
    public static Determination Record(
        DeterminationId id,
        ClaimKind kind,
        string ruleId,
        string ruleVersion,
        IEnumerable<ContentHash> inputs,
        string outputCanonical,
        DateTime asOfUtc,
        DateTime recordedAtUtc,
        Confidence? confidence = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        DateRange.EnsureUtc(asOfUtc, nameof(asOfUtc));
        DateRange.EnsureUtc(recordedAtUtc, nameof(recordedAtUtc));

        if (id.Value == Guid.Empty)
        {
            throw new DomainValidationException(nameof(id), "A determination id is required.");
        }

        if (kind != ClaimKind.Calculation)
        {
            throw new DomainRuleViolationException(
                "Determination.CalculationOnly",
                $"Only {nameof(ClaimKind.Calculation)} determinations may be recorded today. "
                + $"Refusing {kind} rather than storing a value whose rules nothing yet enforces.");
        }

        if (confidence is not null)
        {
            throw new DomainRuleViolationException(
                "Determination.CalculationHasNoConfidence",
                "A calculation may not carry its own confidence. It is exact given its inputs; "
                + "uncertainty belongs to the claims it derives from.");
        }

        var ordered = inputs.ToList();

        if (ordered.Count == 0)
        {
            throw new DomainRuleViolationException(
                "Determination.IdentifiesInputs",
                "A determination must name the inputs it was computed from, by content hash, "
                + "otherwise its result cannot be reproduced or explained.");
        }

        if (ordered.Any(h => h is null))
        {
            throw new DomainValidationException(
                nameof(inputs),
                "An input content hash may not be null.");
        }

        var canonical = ValidateOutput(outputCanonical);

        return new Determination(
            id,
            kind,
            ValidateText(ruleId, nameof(ruleId), MaxRuleIdLength),
            ValidateText(ruleVersion, nameof(ruleVersion), MaxRuleVersionLength),
            ordered,
            canonical,
            ContentHash.Compute(Encoding.UTF8.GetBytes(canonical)),
            asOfUtc,
            confidence,
            recordedAtUtc);
    }

    /// <summary>
    /// Folds an ordered input list into one deterministic, order-sensitive digest.
    /// </summary>
    /// <remarks>
    /// Built from the hashes' own canonical strings - fixed-length lower-case hex, normalised by
    /// <c>ContentHash</c> itself - joined by a separator that cannot occur inside one, then hashed
    /// with the same <c>ContentHash.Compute</c> the archive uses. No new hashing scheme, and nothing
    /// in the input to this depends on a runtime's formatting of anything.
    /// </remarks>
    public static ContentHash ComputeInputsHash(IEnumerable<ContentHash> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var joined = string.Join(InputSeparator, inputs.Select(h => h.Value));

        return ContentHash.Compute(Encoding.UTF8.GetBytes(joined));
    }

    /// <summary>
    /// Whether this determination was produced by the same rule, version, instant and inputs.
    /// </summary>
    /// <remarks>
    /// The reproducibility key stated as a predicate. Two determinations matching on all four must
    /// carry the same output, and the unique index refuses a second row that claims otherwise.
    /// </remarks>
    public bool HasSameReproducibilityKeyAs(Determination other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(RuleId, other.RuleId, StringComparison.Ordinal)
            && string.Equals(RuleVersion, other.RuleVersion, StringComparison.Ordinal)
            && AsOfUtc == other.AsOfUtc
            && string.Equals(InputsHash, other.InputsHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the stored digest still matches the inputs beside it.
    /// </summary>
    /// <remarks>
    /// A row whose digest and input list disagree was keyed on something other than what it says it
    /// used. Nothing can produce that through the domain, so this exists to catch a row that arrived
    /// some other way rather than to defend against this type.
    /// </remarks>
    public bool InputsHashMatchesInputs() =>
        string.Equals(InputsHash, ComputeInputsHash(_inputs).Value, StringComparison.Ordinal);

    private static string ValidateText(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                parameterName,
                "A determination must name the rule and the version that produced it.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"This value may not exceed {maxLength} characters.");
        }

        return trimmed;
    }

    private static string ValidateOutput(string outputCanonical)
    {
        if (string.IsNullOrWhiteSpace(outputCanonical))
        {
            throw new DomainValidationException(
                nameof(outputCanonical),
                "A determination must record the result it produced, or there is nothing for a "
                + "recomputation to reproduce.");
        }

        if (outputCanonical.Length > MaxOutputLength)
        {
            throw new DomainValidationException(
                nameof(outputCanonical),
                $"A canonical output may not exceed {MaxOutputLength} characters.");
        }

        // Not trimmed: the output is canonical bytes, and trimming would change what was hashed.
        return outputCanonical;
    }
}
