using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Limits;

/// <summary>One limit that would be exceeded, and by what.</summary>
/// <remarks>
/// The engine reports every breach it finds rather than the first, because a proposal stopped by
/// three ceilings at once needs a different response from one stopped by a single marginal
/// overshoot, and only the full list distinguishes them.
/// </remarks>
public sealed record LimitBreach
{
    private LimitBreach(LimitKind kind, string explanation)
    {
        Kind = kind;
        Explanation = explanation;
    }

    public LimitKind Kind { get; }

    /// <summary>What was proposed, what the ceiling is, and therefore why this was refused.</summary>
    public string Explanation { get; }

    public static LimitBreach Create(LimitKind kind, string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation))
        {
            throw new DomainValidationException(
                nameof(explanation),
                "A breach must say what was exceeded and by how much. 'Limit exceeded' tells an " +
                "operator nothing they can act on.");
        }

        return new LimitBreach(kind, explanation.Trim());
    }

    public override string ToString() => $"{Kind}: {Explanation}";
}
