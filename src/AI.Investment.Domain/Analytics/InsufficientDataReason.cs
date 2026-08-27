namespace AI.Investment.Domain.Analytics;

/// <summary>
/// Why a calculation declined to produce a number.
/// </summary>
/// <remarks>
/// Refusing is a first-class result, not an error. A metric that cannot be computed from the
/// evidence available must say so; the alternative - returning zero, or null, or the previous
/// period's figure - produces a number that reads exactly like a measurement and is not one.
/// </remarks>
public enum InsufficientDataReason
{
    /// <summary>Not a refusal; present so <c>default</c> names a case.</summary>
    None = 0,

    /// <summary>A term the formula requires was not available at all.</summary>
    MissingInput = 1,

    /// <summary>Fewer historical periods than the formula needs.</summary>
    NotEnoughHistory = 2,

    /// <summary>The formula is undefined for these inputs - a zero denominator, most often.</summary>
    UndefinedResult = 3,

    /// <summary>Inputs that must share a unit or currency did not.</summary>
    UnitMismatch = 4,

    /// <summary>The evidence needed exists but was not published by the knowledge cutoff.</summary>
    OutsideKnowledgeCutoff = 5,

    /// <summary>Sources disagree, and the formula has no rule for choosing between them.</summary>
    ConflictingEvidence = 6,
}
