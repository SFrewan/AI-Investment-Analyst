using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// An ISO 4217 alpha-3 currency code.
/// </summary>
/// <remarks>
/// A separate type rather than a string on <see cref="Money"/>, so that "usd", "USD " and
/// "Dollars" cannot all reach the same comparison and quietly disagree. Only the shape of the
/// code is validated: maintaining the authoritative list of live ISO codes is a reference-data
/// concern for a later phase, and rejecting a legitimate but unlisted code would be worse than
/// accepting a well-formed unknown one.
/// </remarks>
public sealed record Currency
{
    private Currency(string code) => Code = code;

    public string Code { get; }

    /// <summary>United States dollar. The reporting currency for the initial equities use case.</summary>
    public static Currency Usd { get; } = new("USD");

    public static Currency Create(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainValidationException(nameof(code), "A currency code is required.");
        }

        var normalised = code.Trim().ToUpperInvariant();

        if (normalised.Length != 3)
        {
            throw new DomainValidationException(
                nameof(code),
                $"A currency code must be exactly 3 letters (ISO 4217). Received '{code}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterUpper(c))
            {
                throw new DomainValidationException(
                    nameof(code),
                    $"A currency code must contain only letters. Received '{code}'.");
            }
        }

        return new Currency(normalised);
    }

    public override string ToString() => Code;
}
