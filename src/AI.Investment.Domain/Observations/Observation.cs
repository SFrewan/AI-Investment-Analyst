using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Observations;

/// <summary>
/// One thing the platform knows about one subject, and where it came from.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Claim{T}"/> carries a value, its provenance and its epistemic status - but not
/// what the value is <em>about</em>. "3571" with impeccable provenance is still not knowledge until
/// something says it is Apple's SIC code. An observation is that missing sentence: subject,
/// attribute, value, and the claim's epistemic state.
/// </para>
/// <para>
/// <strong>Attribute is a dotted string, not an enumeration.</strong> The platform's scope spans
/// companies, products, suppliers, currencies and routes, and an enum of every attribute any domain
/// might have would need editing before a new one could be described. <c>company.name</c>,
/// <c>product.unit-price</c> and <c>route.transit-days</c> are all the same shape to everything
/// that stores or queries them.
/// </para>
/// <para>
/// <strong>The epistemic invariants are not re-implemented here.</strong> <see cref="ToClaim"/>
/// materialises through <see cref="Claims"/>, and <see cref="Record"/> checks the same rules on the
/// way in, so an observation that could not be a valid claim cannot be stored. Duplicating the
/// rules would be two places for them to drift.
/// </para>
/// </remarks>
public sealed class Observation : AggregateRoot<ObservationId>
{
    public const int MaxAttributeLength = 120;
    public const int MaxCaveatLength = 500;
    public const int MaxCaveats = 20;

    private readonly List<string> _caveats;

    private Observation(
        ObservationId id,
        IngestionSubject subject,
        string attribute,
        ObservationValue value,
        ClaimKind kind,
        Provenance provenance,
        Confidence? confidence,
        List<string> caveats)
        : base(id)
    {
        Subject = subject;
        Attribute = attribute;
        Value = value;
        Kind = kind;
        Provenance = provenance;
        Confidence = confidence;
        _caveats = caveats;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Observation()
    {
        Subject = null!;
        Attribute = string.Empty;
        Value = null!;
        Provenance = null!;
        _caveats = [];
    }

    /// <summary>What the observation is about.</summary>
    public IngestionSubject Subject { get; private set; }

    /// <summary>Which property of the subject, as a dotted name.</summary>
    public string Attribute { get; private set; }

    public ObservationValue Value { get; private set; }

    public ClaimKind Kind { get; private set; }

    public Provenance Provenance { get; private set; }

    /// <summary>Forbidden on a fact, required on a judgement.</summary>
    public Confidence? Confidence { get; private set; }

    public IReadOnlyList<string> Caveats => _caveats;

    /// <summary>When the value became public - the only legitimate backtest filter.</summary>
    public DateTime PublishedAtUtc => Provenance.PublishedAtUtc;

    /// <summary>
    /// Records something read directly from a source.
    /// </summary>
    /// <remarks>
    /// Only facts, for now. Calculations and model interpretations both require the claims they
    /// derive from, and nothing in this phase produces either - so rather than build a derivation
    /// path with no caller, <see cref="Record"/> takes the one kind normalisation actually emits
    /// and the others are refused with a reason until the phase that produces them arrives.
    /// </remarks>
    public static Observation RecordFact(
        IngestionSubject subject,
        string attribute,
        ObservationValue value,
        Provenance provenance,
        IEnumerable<string>? caveats = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(provenance);

        var validatedAttribute = ValidateAttribute(attribute);
        var validatedCaveats = ValidateCaveats(caveats);

        // Materialised through Claims.Fact so the ordering rules that make a fact a fact - it
        // cannot be published before the period it describes, nor retrieved before it was
        // published - are checked by the type that owns them. A payload that violates them is a
        // data-quality defect, and this is where it surfaces.
        _ = Claims.Fact(value.Canonical, provenance, validatedCaveats);

        return new Observation(
            ObservationId.New(),
            subject,
            validatedAttribute,
            value,
            ClaimKind.Fact,
            provenance,
            confidence: null,
            validatedCaveats);
    }

    /// <summary>Rebuilds an observation as the claim it represents.</summary>
    public Claim ToClaim() => Kind switch
    {
        ClaimKind.Fact => MaterialiseFact(),

        // A stored kind this build cannot rebuild is refused rather than downgraded to a fact.
        // Presenting a prediction as an observation is the single worst thing this model can do.
        _ => throw new DomainRuleViolationException(
            "Observation.UnsupportedClaimKind",
            $"Observations of kind {Kind} cannot yet be rebuilt as claims. Refusing rather than " +
            "returning something of the wrong epistemic status."),
    };

    public override string ToString() => $"{Subject}.{Attribute} = {Value}";

    private Claim MaterialiseFact() => Value.Kind switch
    {
        ObservationValueKind.Number => Claims.Fact(Value.AsNumber(), Provenance, _caveats),
        ObservationValueKind.Boolean => Claims.Fact(Value.AsBoolean(), Provenance, _caveats),
        ObservationValueKind.Timestamp => Claims.Fact(Value.AsTimestamp(), Provenance, _caveats),
        _ => Claims.Fact(Value.Canonical, Provenance, _caveats),
    };

    private static string ValidateAttribute(string attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            throw new DomainValidationException(
                nameof(attribute),
                "An observation must say which property of the subject it describes. A value with " +
                "no attribute is a number nobody can use.");
        }

        var trimmed = attribute.Trim();

        if (trimmed.Length > MaxAttributeLength)
        {
            throw new DomainValidationException(
                nameof(attribute),
                $"An attribute name may not exceed {MaxAttributeLength} characters.");
        }

        return trimmed;
    }

    private static List<string> ValidateCaveats(IEnumerable<string>? caveats)
    {
        var validated = new List<string>();

        if (caveats is null)
        {
            return validated;
        }

        foreach (var caveat in caveats)
        {
            if (string.IsNullOrWhiteSpace(caveat))
            {
                continue;
            }

            var trimmed = caveat.Trim();

            validated.Add(trimmed.Length <= MaxCaveatLength ? trimmed : trimmed[..MaxCaveatLength]);

            if (validated.Count == MaxCaveats)
            {
                break;
            }
        }

        return validated;
    }
}
