using System.Text.Json;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// The per-type payload: whatever an equity, a resale or a supplier opportunity needs that the
/// core does not.
/// </summary>
/// <remarks>
/// <para>
/// Schema-flexible on purpose, and stored as JSON. Per-type detail is exactly the part that should
/// not require a migration when a new type is added, and everything the policy engine reads lives
/// in the strongly-typed core rather than in here. A safety decision must never depend on parsing
/// this.
/// </para>
/// <para>
/// Validated as far as it usefully can be at this level: it must be a JSON object, so that a stray
/// array or bare string cannot be stored where a shape is expected. The type's own schema check
/// belongs to its <see cref="IEvidenceRequirement"/>, which is the thing that knows what the shape
/// should be.
/// </para>
/// </remarks>
public sealed record OpportunityDetail
{
    public const int MaxLength = 64 * 1024;

    private OpportunityDetail(OpportunityType type, string json)
    {
        Type = type;
        Json = json;
    }

    public OpportunityType Type { get; }

    /// <summary>The payload, as a JSON object.</summary>
    public string Json { get; }

    /// <summary>An empty payload, for a type that needs none.</summary>
    public static OpportunityDetail Empty(OpportunityType type) => Create(type, "{}");

    public static OpportunityDetail Create(OpportunityType type, string json)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainValidationException(
                nameof(json),
                "An opportunity detail payload is required. Use Empty for a type that carries none, " +
                "so that 'no detail' is a decision rather than a blank.");
        }

        var trimmed = json.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(json),
                $"An opportunity detail payload may not exceed {MaxLength} characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DomainValidationException(
                    nameof(json),
                    $"An opportunity detail payload must be a JSON object; received " +
                    $"{document.RootElement.ValueKind}.");
            }
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException(
                nameof(json),
                $"An opportunity detail payload must be valid JSON: {exception.Message}");
        }

        return new OpportunityDetail(type, trimmed);
    }

    public override string ToString() => $"{Type} detail ({Json.Length} chars)";
}
