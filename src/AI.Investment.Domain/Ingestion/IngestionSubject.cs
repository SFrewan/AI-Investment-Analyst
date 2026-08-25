using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// What an ingestion run is about: the kind of thing, and which one.
/// </summary>
/// <remarks>
/// <para>
/// Two validated strings rather than a typed reference, and deliberately so. The platform's
/// stated scope is opportunity discovery across companies, products, suppliers, commodities,
/// currencies and shipping routes; a subject typed as <c>Ticker</c> would quietly narrow the data
/// plane to equities and would have to be torn out at the first non-equity domain.
/// </para>
/// <para>
/// <see cref="Identifier"/> is optional. A run for a specific company names it; a run that sweeps
/// everything a source published today does not have one, and inventing a placeholder would make
/// the two indistinguishable.
/// </para>
/// <para>
/// Mirrors <see cref="Actions.ActionTarget"/> on purpose. The same shape answers the same question
/// on the action side, and two different shapes for "which thing?" would be one more thing to
/// remember.
/// </para>
/// </remarks>
public sealed record IngestionSubject
{
    public const int MaxKindLength = 60;
    public const int MaxIdentifierLength = 200;

    private IngestionSubject(string kind, string? identifier)
    {
        Kind = kind;
        Identifier = identifier;
    }

    /// <summary>The type of thing, for example "Company", "Product" or "CurrencyPair".</summary>
    public string Kind { get; }

    /// <summary>Which one, when the run is about a specific thing. Null for a sweep.</summary>
    public string? Identifier { get; }

    /// <summary>True when this subject names one specific thing.</summary>
    public bool IsSpecific => Identifier is not null;

    public static IngestionSubject Create(string kind, string? identifier = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new DomainValidationException(nameof(kind), "An ingestion subject kind is required.");
        }

        var trimmedKind = kind.Trim();

        if (trimmedKind.Length > MaxKindLength)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A subject kind may not exceed {MaxKindLength} characters.");
        }

        string? trimmedIdentifier = null;

        if (!string.IsNullOrWhiteSpace(identifier))
        {
            trimmedIdentifier = identifier.Trim();

            if (trimmedIdentifier.Length > MaxIdentifierLength)
            {
                throw new DomainValidationException(
                    nameof(identifier),
                    $"A subject identifier may not exceed {MaxIdentifierLength} characters.");
            }
        }

        return new IngestionSubject(trimmedKind, trimmedIdentifier);
    }

    /// <summary>Everything the source publishes in the requested category and window.</summary>
    public static IngestionSubject Sweep(string kind) => Create(kind);

    public override string ToString() => Identifier is null ? Kind : $"{Kind}:{Identifier}";
}
