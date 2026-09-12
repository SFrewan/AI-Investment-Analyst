using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Securities;

/// <summary>
/// One source's claim that a security carried a given identifier over a given interval.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An identifier is an assertion, not an attribute.</strong> The Security Master's invariant
/// is that <em>every identifier assertion carries an interval and a source</em>, and this type has
/// no shape in which it could fail to. Modelling a ticker as a property on the security instead -
/// which is what <c>Company</c> does today - loses both, and with them the ability to answer what a
/// symbol meant on a past date rather than what it means now.
/// </para>
/// <para>
/// <strong>An open interval is honest, not missing.</strong> A null <see cref="ValidTo"/> means the
/// assertion has no observed end, which is different from asserting that it runs forever. Nothing
/// here fills it in from a last price, a filing, or the absence of a later claim.
/// </para>
/// </remarks>
/// <param name="Kind">Which identifier system the value belongs to.</param>
/// <param name="Value">The identifier as the source stated it.</param>
/// <param name="ValidFrom">First date the source's claim covers.</param>
/// <param name="ValidTo">Last date covered, or null when no end has been observed.</param>
/// <param name="SourceId">Who made the claim. Never inferred.</param>
public sealed record SecurityIdentifier(
    SecurityIdentifierKind Kind,
    string Value,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    SourceId SourceId)
{
    public const int MaxValueLength = 64;

    /// <summary>Creates an assertion, refusing every shape that could not be evidence.</summary>
    public static SecurityIdentifier Assert(
        SecurityIdentifierKind kind,
        string value,
        DateOnly validFrom,
        DateOnly? validTo,
        SourceId sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        if (kind == SecurityIdentifierKind.Unknown)
        {
            throw new DomainValidationException(
                nameof(kind),
                "An identifier assertion must name which identifier system it belongs to.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "An identifier value is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxValueLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"An identifier value may not exceed {MaxValueLength} characters.");
        }

        if (validTo is { } end && end < validFrom)
        {
            throw new DomainRuleViolationException(
                "SecurityIdentifier.IntervalOrdered",
                $"An identifier assertion cannot end ({end:O}) before it begins ({validFrom:O}).");
        }

        return new SecurityIdentifier(kind, trimmed, validFrom, validTo, sourceId);
    }

    /// <summary>Whether this assertion covers <paramref name="asOf"/>.</summary>
    public bool CoversDate(DateOnly asOf) =>
        asOf >= ValidFrom && (ValidTo is null || asOf <= ValidTo);
}
