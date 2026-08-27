using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// One named quantity a calculation was performed on, together with the evidence for it.
/// </summary>
/// <remarks>
/// <para>
/// The value and its evidence arrive as a single <see cref="Claim{T}"/> rather than as separate
/// arguments, so there is no way to record a number alongside provenance that belongs to a
/// different number. That mismatch is undetectable afterwards, which is why the type refuses to
/// make it representable.
/// </para>
/// <para>
/// <strong>Judgements are refused.</strong> An input may be an observed fact or another
/// calculation; it may not be an AI interpretation or a prediction. A deterministic metric computed
/// partly from a model's opinion is not deterministic, and downstream it would be indistinguishable
/// from one that was measured.
/// </para>
/// </remarks>
public sealed record CalculationInput
{
    public const int MaxNameLength = 60;

    private CalculationInput(string name, Claim<decimal> evidence, UnitOfMeasure unit)
    {
        Name = name;
        Evidence = evidence;
        Unit = unit;
    }

    /// <summary>The role this quantity played, as the formula names it - "revenue", "priorRevenue".</summary>
    public string Name { get; }

    public Claim<decimal> Evidence { get; }

    public UnitOfMeasure Unit { get; }

    public decimal Value => Evidence.Value;

    public Provenance Provenance => Evidence.Provenance;

    public ClaimId EvidenceId => Evidence.Id;

    public static CalculationInput Create(string name, Claim<decimal> evidence, UnitOfMeasure unit)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(
                nameof(name),
                "An input must say what part it played in the formula. An unnamed number cannot be " +
                "matched to the term it stood for.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"An input name may not exceed {MaxNameLength} characters.");
        }

        if (!Enum.IsDefined(unit) || unit == UnitOfMeasure.Unknown)
        {
            throw new DomainValidationException(
                nameof(unit),
                $"'{unit}' is not a unit an input may carry. A term whose unit is unknown makes the " +
                "result's unit unknown too.");
        }

        if (evidence.IsJudgement)
        {
            throw new DomainRuleViolationException(
                "CalculationInput.EvidenceIsJudgement",
                $"An input to a deterministic calculation may not be a {evidence.Kind}. A metric " +
                "computed partly from a judgement is not deterministic, and nothing downstream " +
                "would be able to tell it apart from one that was measured.");
        }

        return new CalculationInput(trimmed, evidence, unit);
    }

    public override string ToString() => $"{Name} = {Value}";
}
