using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// A normalised equity ticker symbol.
/// </summary>
/// <remarks>
/// In a system whose entire purpose is to be correct about which instrument it is discussing,
/// the identifier is the last thing that should be an unconstrained string. Normalisation to
/// upper case at construction means equality is reliable everywhere downstream, and a lookup
/// cannot miss because a caller typed lower case.
/// <para>
/// A ticker alone does not identify a security globally - the same symbol can exist on several
/// venues, and symbols are reused after a delisting. Exchange qualification and a durable
/// identifier (FIGI, CUSIP, ISIN) belong to the reference-data work in Phase 2; this type is
/// the Phase 1 foundation, not the final answer.
/// </para>
/// </remarks>
public sealed record Ticker
{
    public const int MaxLength = 12;

    private Ticker(string value) => Value = value;

    public string Value { get; }

    public static Ticker Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A ticker symbol is required.");
        }

        var normalised = value.Trim().ToUpperInvariant();

        if (normalised.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A ticker symbol may not exceed {MaxLength} characters. Received '{value}'.");
        }

        // Letters, plus '.' and '-' for share-class notation such as BRK.B and BRK-B.
        // Digits are permitted because they occur on non-US venues (for example 7203 in Tokyo).
        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '.' && c != '-')
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"A ticker symbol may contain only letters, digits, '.' and '-'. Received '{value}'.");
            }
        }

        if (normalised[0] == '.' || normalised[0] == '-' ||
            normalised[^1] == '.' || normalised[^1] == '-')
        {
            throw new DomainValidationException(
                nameof(value),
                $"A ticker symbol may not begin or end with a separator. Received '{value}'.");
        }

        return new Ticker(normalised);
    }

    /// <summary>
    /// Attempts to create a ticker, returning false instead of throwing for malformed input.
    /// </summary>
    /// <remarks>
    /// For places where invalid input is an ordinary occurrence rather than a defect - a search
    /// box, for instance, where the user may have typed a company name. Exceptions are for
    /// broken expectations, not for a caller who typed prose.
    /// </remarks>
    public static bool TryCreate(string? value, out Ticker? ticker)
    {
        try
        {
            ticker = Create(value!);
            return true;
        }
        catch (DomainValidationException)
        {
            ticker = null;
            return false;
        }
    }

    public override string ToString() => Value;
}
