using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// Reported figures combined with signs: free cash flow, EBITDA, net debt.
/// </summary>
/// <remarks>
/// Every term must share the result's unit. Adding money to a share count produces a number, and a
/// number produced that way is worse than no number, because it looks like the others.
/// </remarks>
public sealed class SumMetricCalculator : IMetricCalculator<ReportedFigures>
{
    public const int MinimumTerms = 2;

    private readonly List<SumTerm> _terms;

    public SumMetricCalculator(
        MetricId metric,
        SourceId calculatorId,
        CalculationVersion version,
        UnitOfMeasure unit,
        IEnumerable<SumTerm> terms,
        string formula)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(calculatorId);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(terms);

        if (unit is not (UnitOfMeasure.Money or UnitOfMeasure.Count))
        {
            throw new DomainValidationException(
                nameof(unit),
                $"Only quantities of the same kind may be added. '{unit}' is not one of them.");
        }

        if (string.IsNullOrWhiteSpace(formula))
        {
            throw new DomainValidationException(nameof(formula), "A sum must state its formula.");
        }

        _terms = terms.ToList();

        if (_terms.Count < MinimumTerms)
        {
            throw new DomainValidationException(
                nameof(terms),
                $"A sum needs at least {MinimumTerms} terms. One term is a rename, and presenting a " +
                "renamed figure as a calculation misstates where the number came from.");
        }

        if (_terms.Any(term => term is null))
        {
            throw new DomainValidationException(nameof(terms), "A term may not be null.");
        }

        if (_terms.Select(term => term.Attribute).Distinct(StringComparer.Ordinal).Count() != _terms.Count)
        {
            throw new DomainValidationException(
                nameof(terms),
                "Each figure may appear once in a sum; repeating one hides a doubled coefficient.");
        }

        Metric = metric;
        CalculatorId = calculatorId;
        Version = version;
        Unit = unit;
        Formula = formula.Trim();
    }

    public MetricId Metric { get; }

    public SourceId CalculatorId { get; }

    public CalculationVersion Version { get; }

    public UnitOfMeasure Unit { get; }

    public IReadOnlyList<SumTerm> Terms => _terms;

    public string Formula { get; }

    public CalculationOutcome Calculate(CalculationContext context, ReportedFigures inputs)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputs);

        CalculationGuards.EnsureSubject(Metric, context, inputs.Subject);

        var found = new List<ReportedFigure>(_terms.Count);

        foreach (var term in _terms)
        {
            if (!inputs.TryFind(term.Attribute, out var figure))
            {
                return CalculationGuards.MissingFigure(Metric, term.Attribute);
            }

            if (figure.Unit != Unit)
            {
                return CalculationGuards.UnitMismatch(
                    Metric,
                    $"'{term.Attribute}' is {figure.Unit}, but this sum is in {Unit}.");
            }

            found.Add(figure);
        }

        var terms = found
            .Select(figure => CalculationInput.Create(figure.Attribute, figure.Evidence, figure.Unit))
            .ToList();

        var lookAhead = CalculationGuards.RefuseIfOutsideCutoff(Metric, context, terms);

        if (lookAhead is not null)
        {
            return lookAhead;
        }

        var amount = 0m;

        for (var i = 0; i < _terms.Count; i++)
        {
            amount += _terms[i].Coefficient * found[i].Value;
        }

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

    public override string ToString() => $"{Metric} ({Version}): {Formula}";
}
