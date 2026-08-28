using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Watching;

/// <summary>What a watch is watching: a kind, and optionally one identifier of that kind.</summary>
/// <remarks>
/// Deliberately a separate type from <c>ActionTarget</c> despite the identical shape. They are
/// compared against different things and change for different reasons - one names what an action
/// will affect, the other names what an observation is about - and collapsing them would couple the
/// watch model to the action model for the sake of five lines.
/// </remarks>
public sealed record WatchTarget
{
    public const int MaxKindLength = 60;

    public const int MaxIdentifierLength = 200;

    private WatchTarget(string kind, string? identifier)
    {
        Kind = kind;
        Identifier = identifier;
    }

    /// <summary>Security, Sector, Opportunity, Portfolio, Supplier, and so on.</summary>
    public string Kind { get; }

    /// <summary>The specific thing, or null when the watch covers every instance of the kind.</summary>
    public string? Identifier { get; }

    public static WatchTarget Create(string kind, string? identifier = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new DomainValidationException(nameof(kind), "A watch target requires a kind.");
        }

        var trimmedKind = kind.Trim();

        if (trimmedKind.Length > MaxKindLength)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A watch target kind may not exceed {MaxKindLength} characters.");
        }

        string? trimmedIdentifier = null;

        if (!string.IsNullOrWhiteSpace(identifier))
        {
            trimmedIdentifier = identifier.Trim();

            if (trimmedIdentifier.Length > MaxIdentifierLength)
            {
                throw new DomainValidationException(
                    nameof(identifier),
                    $"A watch target identifier may not exceed {MaxIdentifierLength} characters.");
            }
        }

        return new WatchTarget(trimmedKind, trimmedIdentifier);
    }

    /// <summary>
    /// True when <paramref name="other"/> falls inside this target.
    /// </summary>
    /// <remarks>
    /// A target with no identifier covers every instance of its kind; one with an identifier covers
    /// only that instance. Matching is ordinal: "AAPL" and "aapl" are not assumed to be the same
    /// security, because deciding that they are is a reference-data judgement and this is a
    /// comparison.
    /// </remarks>
    public bool Covers(WatchTarget other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(Kind, other.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        return Identifier is null || string.Equals(Identifier, other.Identifier, StringComparison.Ordinal);
    }

    public override string ToString() =>
        Identifier is null ? Kind : $"{Kind}:{Identifier}";
}
