using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// A trading venue code - an ISO 10383 MIC such as XNYS or XNAS, or a common short code.
/// </summary>
/// <remarks>
/// Only the shape is validated, for the same reason as <see cref="Currency"/>: the
/// authoritative venue list is reference data belonging to Phase 2, and rejecting a valid but
/// unlisted venue would be worse than accepting a well-formed unknown one. What matters now is
/// that the value is normalised so comparisons are reliable.
/// </remarks>
public sealed record Exchange
{
    public const int MinLength = 2;
    public const int MaxLength = 12;

    private Exchange(string code) => Code = code;

    public string Code { get; }

    public static Exchange Create(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainValidationException(nameof(code), "An exchange code is required.");
        }

        var normalised = code.Trim().ToUpperInvariant();

        if (normalised.Length is < MinLength or > MaxLength)
        {
            throw new DomainValidationException(
                nameof(code),
                $"An exchange code must be between {MinLength} and {MaxLength} characters. Received '{code}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                throw new DomainValidationException(
                    nameof(code),
                    $"An exchange code may contain only letters and digits. Received '{code}'.");
            }
        }

        return new Exchange(normalised);
    }

    public override string ToString() => Code;
}
