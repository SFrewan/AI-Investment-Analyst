using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// One reported figure divided by another.
/// </summary>
/// <remarks>
/// <para>
/// Margins, liquidity ratios, leverage, returns and per-share figures are all this one shape, so
/// they are one class configured seventeen ways rather than seventeen near-identical classes. The
/// formula each instance computes is stated in its own <see cref="Formula"/>, so a stored result
/// still explains itself.
/// </para>
/// <para>
/// Nothing here is specific to finance. A delivery-on-time rate or a bed-occupancy ratio is the
/// same calculation, which is the point.
/// </para>
/// </remarks>
public sealed class RatioMetricCalculator : IMetricCalculator<ReportedFigures>
{
    public RatioMetricCalculator(
        MetricId metric,
        SourceId calculatorId,
        CalculationVersion version,
        UnitOfMeasure unit,
        string numeratorAttribute,
        string denominatorAttribute,
        string formula)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(calculatorId);
        ArgumentNullException.ThrowIfNull(version);

        if (unit is not (UnitOfMeasure.Ratio or UnitOfMeasure.Money))
        {
            throw new DomainValidationException(
                nameof(unit),
                $"A division produces a dimensionless ratio, or money when money is divided by a " +
                $"count. '{unit}' is neither.");
        }

        if (string.IsNullOrWhiteSpace(numeratorAttribute) || string.IsNullOrWhiteSpace(denominatorAttribute))
        {
            throw new DomainValidationException(
                nameof(numeratorAttribute),
                "Both terms of a ratio must name the figure they read.");
        }

        if (string.Equals(numeratorAttribute, denominatorAttribute, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                nameof(denominatorAttribute),
                $"'{numeratorAttribute}' divided by itself is 1, whatever it measures.");
        }

        if (string.IsNullOrWhiteSpace(formula))
        {
            throw new DomainValidationException(nameof(formula), "A ratio must state its formula.");
        }

        Metric = metric;
        CalculatorId = calculatorId;
        Version = version;
        Unit = unit;
        NumeratorAttribute = numeratorAttribute.Trim().ToLowerInvariant();
        DenominatorAttribute = denominatorAttribute.Trim().ToLowerInvariant();
        Formula = formula.Trim();
    }

    public MetricId Metric { get; }

    public SourceId CalculatorId { get; }

    public CalculationVersion Version { get; }

    public UnitOfMeasure Unit { get; }

    public string NumeratorAttribute { get; }

    public string DenominatorAttribute { get; }

    public string Formula { get; }

    public CalculationOutcome Calculate(CalculationContext context, ReportedFigures inputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputs);

        CalculationGuards.EnsureSubject(Metric, context, inputs.Subject);

        if (!inputs.TryFind(NumeratorAttribute, out var numerator))
        {
            return CalculationGuards.MissingFigure(Metric, NumeratorAttribute);
        }

        if (!inputs.TryFind(DenominatorAttribute, out var denominator))
        {
            return CalculationGuards.MissingFigure(Metric, DenominatorAttribute);
        }

        var mismatch = CheckUnits(numerator, denominator);

        if (mismatch is not null)
        {
            return mismatch;
        }

        var terms = new[]
        {
            CalculationInput.Create(NumeratorAttribute, numerator.Evidence, numerator.Unit),
            CalculationInput.Create(DenominatorAttribute, denominator.Evidence, denominator.Unit),
        };

        var lookAhead = CalculationGuards.RefuseIfOutsideCutoff(Metric, context, terms);

        if (lookAhead is not null)
        {
            return lookAhead;
        }

        if (denominator.Value == 0m)
        {
            return CalculationGuards.Undefined(
                Metric,
                $"'{DenominatorAttribute}' was zero for this period, so the ratio is undefined. " +
                "Reporting zero or infinity here would be a number nobody could act on.");
        }

        var amount = numerator.Value / denominator.Value;

        var value = Unit == UnitOfMeasure.Money
            ? MetricValue.Money(amount, inputs.Currency)
            : MetricValue.Create(amount, Unit);

        return CalculationOutcome.Computed(
            MetricResult.Create(
                context,
                Metric,
                value,
                Formula,
                CalculatorId,
                Version,
                inputs.PeriodEndUtc,
                terms));
    }

    /// <summary>
    /// Money over money is dimensionless; money over a count is money per unit. Anything else is a
    /// question the formula was not written to answer.
    /// </summary>
    private CalculationOutcome? CheckUnits(ReportedFigure numerator, ReportedFigure denominator)
    {
        if (Unit == UnitOfMeasure.Ratio && numerator.Unit != denominator.Unit)
        {
            return CalculationGuards.UnitMismatch(
                Metric,
                $"A dimensionless ratio needs matching units, but '{NumeratorAttribute}' is " +
                $"{numerator.Unit} and '{DenominatorAttribute}' is {denominator.Unit}.");
        }

        if (Unit == UnitOfMeasure.Money &&
            (numerator.Unit != UnitOfMeasure.Money || denominator.Unit != UnitOfMeasure.Count))
        {
            return CalculationGuards.UnitMismatch(
                Metric,
                $"A per-unit money figure needs money over a count, but '{NumeratorAttribute}' is " +
                $"{numerator.Unit} and '{DenominatorAttribute}' is {denominator.Unit}.");
        }

        return null;
    }

    public override string ToString() => $"{Metric} ({Version}): {Formula}";
}
