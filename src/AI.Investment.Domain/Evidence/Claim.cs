using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Evidence;

/// <summary>
/// A single value together with the epistemic status the system is entitled to claim for it.
/// </summary>
/// <remarks>
/// <para>
/// This type is what turns the platform's mandatory FACT / CALCULATION / AI INTERPRETATION /
/// PREDICTION distinction from a documentation promise into something the compiler and the test
/// suite enforce. A report becomes a graph of claims; "show me why you said this" becomes a
/// traversal rather than a prose explanation.
/// </para>
/// <para>
/// The non-generic base exists so that claims of differing value types can be held in one
/// collection and persisted through one mapping. The typed value lives on <see cref="Claim{T}"/>.
/// </para>
/// <para>
/// <strong>Phase 1 scope.</strong> This is the model, not the evidence graph. Traversal,
/// querying and persistence of claims arrive with the data plane in Phase 2, which is the first
/// point at which anything produces a claim.
/// </para>
/// </remarks>
public abstract class Claim : IEquatable<Claim>
{
    private readonly List<ClaimId> _derivedFrom;
    private readonly List<string> _caveats;

    private protected Claim(
        ClaimId id,
        ClaimKind kind,
        Provenance provenance,
        IEnumerable<ClaimId>? derivedFrom,
        Confidence? confidence,
        IEnumerable<string>? caveats)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        _derivedFrom = derivedFrom?.ToList() ?? [];
        _caveats = caveats?.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList() ?? [];

        Validate(kind, provenance, _derivedFrom, confidence);

        Id = id;
        Kind = kind;
        Provenance = provenance;
        Confidence = confidence;
    }

    public ClaimId Id { get; }

    public ClaimKind Kind { get; }

    public Provenance Provenance { get; }

    /// <summary>The claims this one was derived from. Empty only for a <see cref="ClaimKind.Fact"/>.</summary>
    public IReadOnlyList<ClaimId> DerivedFrom => _derivedFrom;

    /// <summary>
    /// Present for <see cref="ClaimKind.AiInterpretation"/> and <see cref="ClaimKind.Prediction"/>.
    /// Always <c>null</c> for a fact or a calculation.
    /// </summary>
    public Confidence? Confidence { get; }

    /// <summary>Known limitations of this claim. Empty is normal; it is not a required field.</summary>
    public IReadOnlyList<string> Caveats => _caveats;

    /// <summary>The value, untyped. Prefer <see cref="Claim{T}.Value"/>.</summary>
    public abstract object? UntypedValue { get; }

    /// <summary>The CLR type name of the value, recorded so a persisted claim can be rehydrated.</summary>
    public abstract string ValueTypeName { get; }

    public bool IsFact => Kind == ClaimKind.Fact;

    /// <summary>
    /// True when this claim is the system's own judgement rather than an observation - that is,
    /// an interpretation or a prediction.
    /// </summary>
    public bool IsJudgement => Kind is ClaimKind.AiInterpretation or ClaimKind.Prediction;

    public bool Equals(Claim? other) => other is not null && Id.Equals(other.Id);

    public override bool Equals(object? obj) => Equals(obj as Claim);

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => $"{Kind} {UntypedValue} (from {Provenance.SourceId})";

    /// <summary>
    /// The rules that make the epistemic distinction real. Each one exists because its absence
    /// permits a specific, known failure.
    /// </summary>
    /// <remarks>
    /// <paramref name="derivedFrom"/> is the concrete <see cref="List{T}"/> rather than an
    /// interface (CA1859). This is a private helper with exactly one call site, which always
    /// passes the backing field, so the interface bought nothing but an interface dispatch on
    /// every <c>Count</c>. Abstraction is worth paying for at a boundary; this is not one.
    /// </remarks>
    private static void Validate(
        ClaimKind kind,
        Provenance provenance,
        List<ClaimId> derivedFrom,
        Confidence? confidence)
    {
        switch (kind)
        {
            case ClaimKind.Fact:
                // A fact is what a source says. Attaching a probability to it makes a sourced
                // observation look like a judgement, which is exactly the confusion this model
                // exists to prevent.
                if (confidence is not null)
                {
                    throw new DomainRuleViolationException(
                        "Claim.FactHasNoConfidence",
                        "A fact may not carry confidence. A fact is either what the source states or it is not; " +
                        "if the value is uncertain it is an interpretation or a prediction, not a fact.");
                }

                if (derivedFrom.Count > 0)
                {
                    throw new DomainRuleViolationException(
                        "Claim.FactIsNotDerived",
                        "A fact is observed, not derived. A value computed from other claims is a Calculation.");
                }

                // Observations of the world obey a real ordering: a value describes a period,
                // becomes public at or after the end of that period, and is fetched at or after
                // it becomes public. A violation here is a data-quality defect and must be
                // quarantined rather than silently accepted - see the ingestion validators.
                if (provenance.PublishedAtUtc < provenance.AsOfUtc)
                {
                    throw new DomainRuleViolationException(
                        "Claim.PublicationFollowsPeriod",
                        $"A fact cannot become public ({provenance.PublishedAtUtc:O}) before the period it " +
                        $"describes ({provenance.AsOfUtc:O}).");
                }

                if (provenance.RetrievedAtUtc < provenance.PublishedAtUtc)
                {
                    throw new DomainRuleViolationException(
                        "Claim.RetrievalFollowsPublication",
                        $"A fact cannot be retrieved ({provenance.RetrievedAtUtc:O}) before it was published " +
                        $"({provenance.PublishedAtUtc:O}). Retrieving data before its publication date is the " +
                        "signature of look-ahead bias.");
                }

                break;

            case ClaimKind.Calculation:
                // Arithmetic is exact given its inputs. Uncertainty in a calculated value comes
                // from the claims it was derived from, which is why those must be identified.
                if (derivedFrom.Count == 0)
                {
                    throw new DomainRuleViolationException(
                        "Claim.CalculationIdentifiesInputs",
                        "A calculation must identify the claims it was derived from, otherwise its result " +
                        "cannot be reproduced or explained.");
                }

                if (confidence is not null)
                {
                    throw new DomainRuleViolationException(
                        "Claim.CalculationHasNoConfidence",
                        "A calculation may not carry its own confidence. It is exact given its inputs; " +
                        "uncertainty belongs to the claims it derives from.");
                }

                break;

            case ClaimKind.AiInterpretation:
            case ClaimKind.Prediction:
                if (confidence is null)
                {
                    throw new DomainRuleViolationException(
                        "Claim.JudgementStatesConfidence",
                        $"A {kind} must state its confidence. A judgement presented without stated uncertainty " +
                        "is indistinguishable downstream from a measured fact.");
                }

                if (derivedFrom.Count == 0)
                {
                    throw new DomainRuleViolationException(
                        "Claim.JudgementCitesEvidence",
                        $"A {kind} must identify the evidence it rests on. A judgement with no traceable " +
                        "supporting claim cannot be checked for groundedness and must be treated as fabricated.");
                }

                break;

            default:
                // Fail closed: an unrecognised kind is refused rather than defaulted to
                // something permissive.
                throw new DomainRuleViolationException(
                    "Claim.UnknownKind",
                    $"Unrecognised claim kind '{kind}'.");
        }
    }
}
