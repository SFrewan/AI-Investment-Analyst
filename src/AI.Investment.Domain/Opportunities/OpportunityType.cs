using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// What kind of opportunity this is - <c>equity</c>, <c>resale</c>, <c>supplier</c>.
/// </summary>
/// <remarks>
/// <para>
/// A validated slug rather than an enum, and the reason is the same one that kept
/// <c>MetricId</c> from being an enum: an enum makes the set of possible opportunity types a
/// compile-time property of this assembly. Adding "supplier opportunities" in year two would then
/// be a change to the core rather than three interface implementations and a policy registration,
/// which is exactly the generalisation the architecture is built to allow.
/// </para>
/// <para>
/// The type is the key into the per-type behaviour - discoverer, economics calculator, evidence
/// requirement - and the schema its <see cref="OpportunityDetail"/> is validated against.
/// </para>
/// </remarks>
public sealed record OpportunityType
{
    public const int MaxLength = 60;

    private OpportunityType(string value) => Value = value;

    public string Value { get; }

    public static OpportunityType Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                nameof(value),
                "An opportunity type is required. Without it there is no way to know which economics " +
                "calculator produced the numbers or which evidence the type demands.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"An opportunity type may not exceed {MaxLength} characters. Received '{value}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"An opportunity type may contain only lower-case letters, digits and '-'. " +
                    $"Received '{value}'.");
            }
        }

        if (normalised[0] == '-' || normalised[^1] == '-')
        {
            throw new DomainValidationException(
                nameof(value),
                $"An opportunity type may not begin or end with '-'. Received '{value}'.");
        }

        return new OpportunityType(normalised);
    }

    public override string ToString() => Value;
}
