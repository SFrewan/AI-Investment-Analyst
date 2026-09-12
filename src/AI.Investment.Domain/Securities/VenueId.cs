using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Securities;

/// <summary>
/// The identity of a trading venue - its market code, held as the key.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a code here and a surrogate for a security.</strong> The prohibition the Security
/// Master states is about <em>tickers</em>, and it is not a general preference for opaque keys: a
/// ticker is recycled between issuers, so it cannot name an instrument twice running. A venue code
/// is the opposite - it is registry-assigned, stable, and the thing every source names a venue by.
/// <c>SourceId</c> keys <c>DataSource</c> the same way for the same reason.
/// </para>
/// <para>
/// Stored upper-cased so that two spellings of one venue cannot become two venues.
/// </para>
/// </remarks>
public sealed record VenueId
{
    public const int MinLength = 2;
    public const int MaxLength = 12;

    private VenueId(string value) => Value = value;

    public string Value { get; }

    public static VenueId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A venue code is required.");
        }

        var trimmed = value.Trim().ToUpperInvariant();

        if (trimmed.Length is < MinLength or > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A venue code must be between {MinLength} and {MaxLength} characters.");
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c) && c != '.' && c != '-')
            {
                throw new DomainValidationException(
                    nameof(value),
                    "A venue code may contain only letters, digits, '.' and '-'.");
            }
        }

        return new VenueId(trimmed);
    }

    public override string ToString() => Value;
}
