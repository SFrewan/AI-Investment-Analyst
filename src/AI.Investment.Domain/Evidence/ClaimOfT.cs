using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Evidence;

/// <summary>
/// A strongly-typed <see cref="Claim"/>.
/// </summary>
/// <typeparam name="T">The type of the value being claimed.</typeparam>
/// <remarks>
/// Construction goes through the four named factories on <see cref="Claims"/>, one per
/// <see cref="ClaimKind"/>. The constructor is internal, so a caller outside this assembly
/// cannot create a claim without stating what kind of thing it is - which is the whole point.
/// </remarks>
public sealed class Claim<T> : Claim
{
    internal Claim(
        ClaimId id,
        T value,
        ClaimKind kind,
        Provenance provenance,
        IEnumerable<ClaimId>? derivedFrom,
        Confidence? confidence,
        IEnumerable<string>? caveats)
        : base(id, kind, provenance, derivedFrom, confidence, caveats)
    {
        Value = value;
    }

    public T Value { get; }

    public override object? UntypedValue => Value;

    public override string ValueTypeName => typeof(T).FullName ?? typeof(T).Name;

    /// <summary>
    /// Returns the value only if this claim is a <see cref="ClaimKind.Fact"/>, and throws
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// This is the explicit gate that stops a prediction being consumed as though it were
    /// measured. Code that genuinely requires an observed value calls this; code that can work
    /// with a judgement reads <see cref="Value"/> and is thereby forced to acknowledge, at the
    /// call site, that it might be handling one.
    /// </remarks>
    public T RequireFactValue()
    {
        if (Kind != ClaimKind.Fact)
        {
            throw new DomainRuleViolationException(
                "Claim.FactRequired",
                $"This code requires an observed fact, but the claim is a {Kind}. " +
                "A judgement must not be consumed as though it were measured.");
        }

        return Value;
    }
}
