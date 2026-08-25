using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Common;

/// <summary>
/// Identifier threading one logical operation through every stage that touches it.
/// </summary>
/// <remarks>
/// Carried by proposals, decisions, executions and audit records so that "why did this happen?"
/// is a query rather than an archaeology exercise. It is a value object rather than a raw
/// string because it is written into the audit trail, and an unconstrained string reaching a
/// log or an audit row is a log-injection vector.
/// </remarks>
public sealed record CorrelationId
{
    public const int MaxLength = 128;

    private CorrelationId(string value) => Value = value;

    public string Value { get; }

    public static CorrelationId New() =>
        new(Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture));

    public static CorrelationId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A correlation identifier is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A correlation identifier may not exceed {MaxLength} characters.");
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new DomainValidationException(
                    nameof(value),
                    "A correlation identifier may contain only ASCII letters, digits, '-' and '_'.");
            }
        }

        return new CorrelationId(trimmed);
    }

    public override string ToString() => Value;
}
