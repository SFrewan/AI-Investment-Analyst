using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// What an opportunity is expected to cost, return and require - with the derived figures derived.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The firm rule of this type: profit, margin and risk-adjusted return are outputs, never
/// inputs.</strong> There is no constructor that accepts them. An agent may supply an estimated
/// sale price as a claim, with provenance and a stated confidence; the arithmetic on top of it
/// belongs to the system. Without that separation the one number a reader acts on is the one number
/// nobody computed.
/// </para>
/// <para>
/// Every amount must be in the same currency. Mixing them would produce a profit figure that is not
/// a quantity of anything, and a comparison between opportunities that silently depends on an
/// exchange rate nobody recorded.
/// </para>
/// <para>
/// <see cref="RiskAdjustedReturn"/> is deliberately the simplest defensible definition - expected
/// profit weighted by the type's own stated probability of success. A more elaborate risk model is
/// a per-type concern belonging to <see cref="IEconomicsCalculator"/>, and burying one here would
/// make every type inherit assumptions made for equities.
/// </para>
/// </remarks>
public sealed record OpportunityEconomics
{
    private OpportunityEconomics(
        Money estimatedCost,
        Money estimatedRevenue,
        Money estimatedProfit,
        Percentage margin,
        Money requiredCapital,
        Percentage successProbability,
        Money riskAdjustedReturn,
        DateRange timeHorizon)
    {
        EstimatedCost = estimatedCost;
        EstimatedRevenue = estimatedRevenue;
        EstimatedProfit = estimatedProfit;
        Margin = margin;
        RequiredCapital = requiredCapital;
        SuccessProbability = successProbability;
        RiskAdjustedReturn = riskAdjustedReturn;
        TimeHorizon = timeHorizon;
    }

    public Money EstimatedCost { get; }

    public Money EstimatedRevenue { get; }

    /// <summary>Calculated: revenue less cost. Never supplied.</summary>
    public Money EstimatedProfit { get; }

    /// <summary>Calculated: profit over revenue, or zero when there is no revenue to divide by.</summary>
    public Percentage Margin { get; }

    /// <summary>What must be committed for the whole horizon, which is not the same as the cost.</summary>
    public Money RequiredCapital { get; }

    /// <summary>The type's own deterministic estimate that this works out.</summary>
    public Percentage SuccessProbability { get; }

    /// <summary>Calculated: expected profit weighted by the stated probability.</summary>
    public Money RiskAdjustedReturn { get; }

    public DateRange TimeHorizon { get; }

    public Currency Currency => EstimatedCost.Currency;

    /// <summary>True when the opportunity requires no capital and moves no money.</summary>
    public bool HasNoFinancialEffect =>
        EstimatedCost.IsZero && EstimatedRevenue.IsZero && RequiredCapital.IsZero;

    /// <summary>
    /// The only way to build an economics block. Derived figures are computed here.
    /// </summary>
    public static OpportunityEconomics Create(
        Money estimatedCost,
        Money estimatedRevenue,
        Money requiredCapital,
        Percentage successProbability,
        DateRange timeHorizon)
    {
        ArgumentNullException.ThrowIfNull(estimatedCost);
        ArgumentNullException.ThrowIfNull(estimatedRevenue);
        ArgumentNullException.ThrowIfNull(requiredCapital);
        ArgumentNullException.ThrowIfNull(successProbability);
        ArgumentNullException.ThrowIfNull(timeHorizon);

        EnsureNotNegative(estimatedCost, nameof(estimatedCost));
        EnsureNotNegative(estimatedRevenue, nameof(estimatedRevenue));
        EnsureNotNegative(requiredCapital, nameof(requiredCapital));

        EnsureSameCurrency(estimatedCost, estimatedRevenue, nameof(estimatedRevenue));
        EnsureSameCurrency(estimatedCost, requiredCapital, nameof(requiredCapital));

        if (successProbability.Ratio is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(successProbability),
                "A probability of success must be between 0 and 1. A value outside that range is not " +
                "a probability, and weighting a return by it produces a number with no meaning.");
        }

        var profit = estimatedRevenue.Subtract(estimatedCost);
        var margin = ComputeMargin(profit, estimatedRevenue);

        return new OpportunityEconomics(
            estimatedCost,
            estimatedRevenue,
            profit,
            margin,
            requiredCapital,
            successProbability,
            profit.MultiplyBy(successProbability.Ratio),
            timeHorizon);
    }

    /// <summary>An opportunity that costs nothing and commits nothing - research, monitoring.</summary>
    public static OpportunityEconomics NoFinancialEffect(Currency currency, DateRange timeHorizon) =>
        Create(
            Money.Zero(currency),
            Money.Zero(currency),
            Money.Zero(currency),
            Percentage.Zero,
            timeHorizon);

    public override string ToString() =>
        $"cost {EstimatedCost}, revenue {EstimatedRevenue}, profit {EstimatedProfit} " +
        $"({Margin}), capital {RequiredCapital}, risk-adjusted {RiskAdjustedReturn}";

    /// <summary>
    /// Profit over revenue, guarded at both ends.
    /// </summary>
    /// <remarks>
    /// Two cases that would otherwise produce nonsense. No revenue means there is nothing to divide
    /// by, and reporting zero is honest: the opportunity has a cost and no return, which the profit
    /// figure already says. A ratio beyond what a <see cref="Percentage"/> can represent means the
    /// cost exceeds the revenue more than a hundredfold, which in practice is a unit or sign error
    /// upstream - and a wrong number that constructs is worse than one that refuses, because it
    /// travels.
    /// </remarks>
    private static Percentage ComputeMargin(Money profit, Money revenue)
    {
        if (revenue.IsZero)
        {
            return Percentage.Zero;
        }

        var ratio = profit.Amount / revenue.Amount;

        if (Math.Abs(ratio) > Percentage.MaxAbsoluteRatio)
        {
            throw new DomainRuleViolationException(
                "OpportunityEconomics.ImplausibleMargin",
                $"Cost exceeds revenue by more than a hundredfold, giving a margin of {ratio:0.##} " +
                "that no percentage can represent. That is a unit or sign error upstream rather than " +
                "an opportunity worth recording.");
        }

        return Percentage.FromRatio(ratio);
    }

    private static void EnsureNotNegative(Money amount, string parameterName)
    {
        if (amount.IsNegative)
        {
            throw new DomainValidationException(
                parameterName,
                "An economics input may not be negative. A negative cost or revenue is a sign error " +
                "somewhere upstream, and it would flow straight into the profit figure.");
        }
    }

    private static void EnsureSameCurrency(Money reference, Money other, string parameterName)
    {
        if (reference.Currency != other.Currency)
        {
            throw new DomainValidationException(
                parameterName,
                $"Every amount in an economics block must share a currency. Received " +
                $"{other.Currency} against {reference.Currency}.");
        }
    }
}
