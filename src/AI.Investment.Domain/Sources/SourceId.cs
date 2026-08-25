using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Sources;

/// <summary>
/// Stable identifier of a registered data source, such as <c>sec-edgar</c> or <c>fred</c>.
/// </summary>
/// <remarks>
/// A readable slug rather than a GUID, and deliberately so: this value is written into the
/// provenance of every claim the source produces and read by a human investigating why the
/// system believed something. <c>sec-edgar</c> answers that question at a glance; a GUID sends
/// the reader to a lookup table.
/// </remarks>
public sealed record SourceId
{
    public const int MaxLength = 64;

    private SourceId(string value) => Value = value;

    public string Value { get; }

    public static SourceId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A source identifier is required.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A source identifier may not exceed {MaxLength} characters. Received '{value}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-' && c != '.')
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"A source identifier may contain only lower-case letters, digits, '-' and '.'. " +
                    $"Received '{value}'.");
            }
        }

        if (normalised[0] == '-' || normalised[0] == '.' ||
            normalised[^1] == '-' || normalised[^1] == '.')
        {
            throw new DomainValidationException(
                nameof(value),
                $"A source identifier may not begin or end with a separator. Received '{value}'.");
        }

        return new SourceId(normalised);
    }

    public override string ToString() => Value;
}
