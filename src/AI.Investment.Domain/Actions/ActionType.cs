using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// The specific operation an action performs, as a dotted identifier such as
/// <c>company.create</c> or, later, <c>order.place</c>.
/// </summary>
/// <remarks>
/// Finer-grained than <see cref="Enums.Capability"/>, which is what policy is expressed against.
/// This value exists for the audit trail, for idempotency keys and for future per-action rules;
/// keeping it a validated value object rather than a free string means it stays queryable.
/// </remarks>
public sealed record ActionType
{
    public const int MaxLength = 100;

    private ActionType(string value) => Value = value;

    public string Value { get; }

    public static ActionType Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "An action type is required.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"An action type may not exceed {MaxLength} characters.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '.' && c != '-')
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"An action type may contain only lower-case letters, digits, '.' and '-'. Received '{value}'.");
            }
        }

        if (normalised[0] == '.' || normalised[^1] == '.')
        {
            throw new DomainValidationException(
                nameof(value),
                $"An action type may not begin or end with '.'. Received '{value}'.");
        }

        return new ActionType(normalised);
    }

    public override string ToString() => Value;
}
