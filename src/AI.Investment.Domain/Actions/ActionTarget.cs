using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// What an action acts upon: the kind of thing, and which one.
/// </summary>
/// <remarks>
/// Two loose strings rather than a typed reference, because an action may target a company, a
/// security, an opportunity, a capital account or an approval, and coupling this type to every
/// one of them would invert the dependency the seam exists to keep clean. Both parts are
/// validated and normalised so the audit trail stays queryable.
/// <para>
/// <see cref="Identifier"/> is optional: an action that creates something has no target
/// identity until it succeeds.
/// </para>
/// </remarks>
public sealed record ActionTarget
{
    public const int MaxKindLength = 60;
    public const int MaxIdentifierLength = 200;

    private ActionTarget(string kind, string? identifier)
    {
        Kind = kind;
        Identifier = identifier;
    }

    /// <summary>The type of thing acted upon, for example "Company".</summary>
    public string Kind { get; }

    /// <summary>Which one, when it already exists. Null for creation.</summary>
    public string? Identifier { get; }

    public static ActionTarget Create(string kind, string? identifier = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new DomainValidationException(nameof(kind), "An action target kind is required.");
        }

        var trimmedKind = kind.Trim();

        if (trimmedKind.Length > MaxKindLength)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A target kind may not exceed {MaxKindLength} characters.");
        }

        string? trimmedIdentifier = null;

        if (!string.IsNullOrWhiteSpace(identifier))
        {
            trimmedIdentifier = identifier.Trim();

            if (trimmedIdentifier.Length > MaxIdentifierLength)
            {
                throw new DomainValidationException(
                    nameof(identifier),
                    $"A target identifier may not exceed {MaxIdentifierLength} characters.");
            }
        }

        return new ActionTarget(trimmedKind, trimmedIdentifier);
    }

    public override string ToString() => Identifier is null ? Kind : $"{Kind}:{Identifier}";
}
