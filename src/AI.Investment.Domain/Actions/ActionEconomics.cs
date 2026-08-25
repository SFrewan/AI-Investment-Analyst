using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// What an action costs and what it puts at risk, plus whether it can be undone.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EstimatedCost"/> is what performing the action spends - a provider fee, an LLM
/// call, a commission. <see cref="EstimatedExposure"/> is what it puts at risk - the capital
/// committed. They are different questions and a single "amount" field would conflate them: a
/// $2 API call and a $2 position are not comparable risks.
/// </para>
/// <para>
/// <see cref="Reversibility"/> sits here rather than being inferred, because it is a property of
/// the action, not of its price, and it is the primary input to risk classification.
/// </para>
/// </remarks>
public sealed record ActionEconomics
{
    private ActionEconomics(Money estimatedCost, Money estimatedExposure, ReversibilityClass reversibility)
    {
        EstimatedCost = estimatedCost;
        EstimatedExposure = estimatedExposure;
        Reversibility = reversibility;
    }

    public Money EstimatedCost { get; }

    public Money EstimatedExposure { get; }

    public ReversibilityClass Reversibility { get; }

    /// <summary>True when the action neither spends nor risks money.</summary>
    public bool HasNoFinancialEffect => EstimatedCost.IsZero && EstimatedExposure.IsZero;

    /// <remarks>
    /// An unrecognised <paramref name="reversibility"/> is deliberately NOT rejected here. It is
    /// instead treated as maximally dangerous by <see cref="RiskTierCalculator"/>. Rejecting it
    /// at construction would move the fail-closed decision into a validation exception that a
    /// caller might catch, and the safe handling of an unknown value belongs with the component
    /// that decides what it means.
    /// </remarks>
    public static ActionEconomics Create(
        Money estimatedCost,
        Money estimatedExposure,
        ReversibilityClass reversibility)
    {
        ArgumentNullException.ThrowIfNull(estimatedCost);
        ArgumentNullException.ThrowIfNull(estimatedExposure);

        if (estimatedCost.IsNegative)
        {
            throw new DomainValidationException(
                nameof(estimatedCost),
                "An estimated cost may not be negative. An action that earns money still costs what it costs; " +
                "the gain belongs in the opportunity's economics, not here.");
        }

        if (estimatedExposure.IsNegative)
        {
            throw new DomainValidationException(
                nameof(estimatedExposure),
                "Estimated exposure may not be negative. Exposure is a magnitude at risk.");
        }

        return new ActionEconomics(estimatedCost, estimatedExposure, reversibility);
    }

    /// <summary>
    /// Economics for an action that spends nothing, risks nothing and can be undone - the
    /// overwhelming majority of actions in a research platform, including every action in
    /// Phase 1.
    /// </summary>
    public static ActionEconomics NoFinancialEffect(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new ActionEconomics(Money.Zero(currency), Money.Zero(currency), ReversibilityClass.Reversible);
    }

    /// <summary>Convenience overload of <see cref="NoFinancialEffect(Currency)"/> in USD.</summary>
    public static ActionEconomics NoFinancialEffect() =>
        new(Money.ZeroUsd, Money.ZeroUsd, ReversibilityClass.Reversible);

    public override string ToString() =>
        $"cost={EstimatedCost}, exposure={EstimatedExposure}, {Reversibility}";
}
