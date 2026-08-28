using System.Globalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// A number an agent states, together with the claim it says the number came from.
/// </summary>
/// <remarks>
/// <para>
/// Structured rather than parsed out of prose. An agent that must place every figure in a named
/// field, with a citation, cannot produce a number that has no provenance without the omission
/// being visible in the shape of its answer - which is a far stronger position than trying to
/// recover intent from a sentence afterwards.
/// </para>
/// <para>
/// <see cref="CitedClaimId"/> is optional but strongly preferred. When present the figure is
/// checked against <em>that</em> claim; when absent it may match any admissible claim in the
/// bundle, which is weaker because a plausible number can collide with an unrelated one.
/// </para>
/// </remarks>
public sealed record AssertedFigure
{
    public const int MaxNameLength = 120;

    private AssertedFigure(
        string name,
        decimal value,
        ClaimId? citedClaimId,
        bool isPercentage,
        string? citedLabel)
    {
        Name = name;
        Value = value;
        CitedClaimId = citedClaimId;
        IsPercentage = isPercentage;
        CitedLabel = citedLabel;
    }

    /// <summary>What the figure is - <c>net-margin</c>, <c>revenue</c>. Names the field, not the value.</summary>
    public string Name { get; }

    /// <summary>The number as the agent stated it.</summary>
    public decimal Value { get; }

    /// <summary>The claim the agent says this came from, when it named one.</summary>
    public ClaimId? CitedClaimId { get; }

    /// <summary>
    /// True when the agent expressed the figure in percentage points, so that <c>18.4</c> should be
    /// compared against a claimed ratio of <c>0.184</c> as well as against <c>18.4</c>.
    /// </summary>
    public bool IsPercentage { get; }

    /// <summary>
    /// The label the agent wrote, when it cited one that could not be resolved.
    /// </summary>
    /// <remarks>
    /// Kept so that "cited nothing" and "cited something that does not exist" stay different
    /// findings. The first is weak; the second means the agent invented a reference, which is a
    /// stronger signal about the answer than any figure in it.
    /// </remarks>
    public string? CitedLabel { get; }

    public static AssertedFigure Create(
        string name,
        decimal value,
        ClaimId? citedClaimId = null,
        bool isPercentage = false,
        string? citedLabel = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(
                nameof(name),
                "An asserted figure must be named. An unnamed number cannot be checked against " +
                "anything, because nothing says what it was supposed to be.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"An asserted figure's name may not exceed {MaxNameLength} characters.");
        }

        return new AssertedFigure(
            trimmed,
            value,
            citedClaimId,
            isPercentage,
            string.IsNullOrWhiteSpace(citedLabel) ? null : citedLabel.Trim());
    }

    /// <summary>
    /// The values this figure could legitimately equal in the bundle.
    /// </summary>
    /// <remarks>
    /// A percentage is quoted in points and stored as a ratio, so both readings are admissible.
    /// Returned as a concrete list: this is read in a tight loop by the validator and the interface
    /// bought nothing (CA1859).
    /// </remarks>
    public List<decimal> Candidates() =>
        IsPercentage ? [Value, Value / 100m] : [Value];

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}={Value}{(IsPercentage ? "%" : string.Empty)}");
}
