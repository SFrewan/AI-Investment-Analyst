using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// How much one figure changed between two periods, as a proportion of the earlier one.
/// </summary>
/// <remarks>
/// <para>
/// The denominator is the absolute value of the prior figure. Without it, a company moving from a
/// loss of 100 to a loss of 50 shows growth of -0.5, which reads as deterioration and is the
/// opposite of what happened.
/// </para>
/// <para>
/// A growth figure is still a measurement, not a forecast. It says what changed; it says nothing
/// about what happens next, and nothing here should be read as though it did.
/// </para>
/// </remarks>
public sealed class GrowthMetricCalculator : IMetricCalculator<FigureComparison>
{
    public const string CurrentTerm = "current";
    public const string PriorTerm = "prior";

    public GrowthMetricCalculator(
        MetricId metric,
        SourceId calculatorId,
        CalculationVersion version,
        string attribute,
        string formula)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(calculatorId);
        ArgumentNullException.ThrowIfNull(version);

        if (string.IsNullOrWhiteSpace(attribute))
        {
            throw new DomainValidationException(
                nameof(attribute),
                "A growth measure must name the figure whose change it reports.");
        }

        if (string.IsNullOrWhiteSpace(formula))
        {
            throw new DomainValidationException(nameof(formula), "A growth measure must state its formula.");
        }

        Metric = metric;
        CalculatorId = calculatorId;
        Version = version;
        Attribute = attribute.Trim().ToLowerInvariant();
        Formula = formula.Trim();
    }

    public MetricId Metric { get; }

    public SourceId CalculatorId { get; }

    public CalculationVersion Version { get; }

    /// <summary>Growth is always a proportion, whatever the underlying figure is measured in.</summary>
    public UnitOfMeasure Unit => UnitOfMeasure.Ratio;

    public string Attribute { get; }

    public string Formula { get; }

    public CalculationOutcome Calculate(CalculationContext context, FigureComparison inputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputs);

        CalculationGuards.EnsureSubject(Metric, context, inputs.Current.Subject);

        if (!inputs.Current.TryFind(Attribute, out var current))
        {
            return CalculationGuards.MissingFigure(Metric, Attribute);
        }

        if (!inputs.Prior.TryFind(Attribute, out var prior))
        {
            return CalculationOutcome.InsufficientData(
                Metric,
                InsufficientDataReason.NotEnoughHistory,
                $"'{Attribute}' is reported for {inputs.Current.PeriodEndUtc:yyyy-MM-dd} but not for " +
                $"{inputs.Prior.PeriodEndUtc:yyyy-MM-dd}, so there is nothing to compare against.");
        }

        if (current.Unit != prior.Unit)
        {
            return CalculationGuards.UnitMismatch(
                Metric,
                $"'{Attribute}' is {current.Unit} in the current period and {prior.Unit} in the " +
                "prior one, so the change cannot be expressed as a proportion.");
        }

        var terms = new[]
        {
            CalculationInput.Create(CurrentTerm, current.Evidence, current.Unit),
            CalculationInput.Create(PriorTerm, prior.Evidence, prior.Unit),
        };

        var lookAhead = CalculationGuards.RefuseIfOutsideCutoff(Metric, context, terms);

        if (lookAhead is not null)
        {
            return lookAhead;
        }

        if (prior.Value == 0m)
        {
            return CalculationGuards.Undefined(
                Metric,
                $"'{Attribute}' was zero in the prior period, so growth from it is undefined. " +
                "Any percentage reported here would be an artefact of the divisor.");
        }

        var amount = (current.Value - prior.Value) / Math.Abs(prior.Value);

        return CalculationOutcome.Computed(
            MetricResult.Create(
                context,
                Metric,
                MetricValue.Ratio(amount),
                Formula,
                CalculatorId,
                Version,
                inputs.Current.PeriodEndUtc,
                terms));
    }

    public override string ToString() => $"{Metric} ({Version}): {Formula}";
}
