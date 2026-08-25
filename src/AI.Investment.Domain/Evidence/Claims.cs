using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Evidence;

/// <summary>
/// The only way to create a claim. One factory per <see cref="ClaimKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no general-purpose "create a claim" method and no constructor available outside
/// this assembly. Producing a value therefore forces the producer to say, at the call site,
/// what kind of knowledge it is - observed, computed, interpreted or predicted. That is the
/// mechanism by which the platform's FACT / CALCULATION / AI INTERPRETATION / PREDICTION
/// distinction survives contact with real code, rather than being a rule everyone agrees with
/// and nobody applies.
/// </para>
/// <para>
/// A non-generic host class so the type argument is inferred at the call site -
/// <c>Claims.Fact(revenue, provenance)</c> rather than
/// <c>Claim&lt;decimal&gt;.Fact(revenue, provenance)</c>.
/// </para>
/// </remarks>
public static class Claims
{
    /// <summary>
    /// An observation obtained from a source. Carries provenance; never confidence, never a
    /// derivation.
    /// </summary>
    public static Claim<T> Fact<T>(T value, Provenance provenance, IEnumerable<string>? caveats = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Claim<T>(ClaimId.New(), value, ClaimKind.Fact, provenance, null, null, caveats);
    }

    /// <summary>
    /// A value computed from other claims. Exact given its inputs, which it must identify.
    /// </summary>
    public static Claim<T> Calculation<T>(
        T value,
        Provenance provenance,
        IEnumerable<ClaimId> derivedFrom,
        IEnumerable<string>? caveats = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Claim<T>(ClaimId.New(), value, ClaimKind.Calculation, provenance, derivedFrom, null, caveats);
    }

    /// <summary>
    /// A model's reading of evidence. Requires both a confidence and the evidence it rests on.
    /// </summary>
    public static Claim<T> AiInterpretation<T>(
        T value,
        Provenance provenance,
        IEnumerable<ClaimId> derivedFrom,
        Confidence confidence,
        IEnumerable<string>? caveats = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(confidence);
        return new Claim<T>(ClaimId.New(), value, ClaimKind.AiInterpretation, provenance, derivedFrom, confidence, caveats);
    }

    /// <summary>
    /// A claim about the future. Requires both a confidence and the evidence it rests on.
    /// </summary>
    public static Claim<T> Prediction<T>(
        T value,
        Provenance provenance,
        IEnumerable<ClaimId> derivedFrom,
        Confidence confidence,
        IEnumerable<string>? caveats = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(confidence);
        return new Claim<T>(ClaimId.New(), value, ClaimKind.Prediction, provenance, derivedFrom, confidence, caveats);
    }
}
