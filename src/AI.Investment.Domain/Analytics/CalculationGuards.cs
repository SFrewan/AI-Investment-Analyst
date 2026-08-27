using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// The checks every calculator owes its caller, written once.
/// </summary>
/// <remarks>
/// Duplicated per calculator these would drift, and the one that drifted would be the one that
/// silently admitted evidence from after its cutoff.
/// </remarks>
internal static class CalculationGuards
{
    /// <summary>
    /// A calculation about one subject may not be handed another's figures.
    /// </summary>
    /// <remarks>
    /// Throws rather than refusing: mismatched subjects are a wiring mistake in the caller, not a
    /// gap in the data, and reporting it as "insufficient data" would hide a bug behind a shrug.
    /// </remarks>
    internal static void EnsureSubject(MetricId metric, CalculationContext context, IngestionSubject subject)
    {
        if (!context.Subject.Equals(subject))
        {
            throw new DomainRuleViolationException(
                "Calculation.SubjectMismatch",
                $"{metric} was asked to measure {context.Subject} from figures belonging to " +
                $"{subject}.");
        }
    }

    /// <summary>
    /// Refuses when any input rests on evidence that was not public at the cutoff.
    /// </summary>
    /// <remarks>
    /// <see cref="MetricResult"/> refuses this too, by throwing. Checking here first turns a
    /// structural guard into an ordinary, explainable refusal - which is what a caller replaying
    /// history needs, because meeting evidence it is not yet allowed to see is normal there.
    /// </remarks>
    internal static CalculationOutcome? RefuseIfOutsideCutoff(
        MetricId metric,
        CalculationContext context,
        IReadOnlyList<CalculationInput> inputs)
    {
        var late = inputs
            .Where(input => !context.Admits(input.Provenance))
            .Select(input => input.Name)
            .ToList();

        if (late.Count == 0)
        {
            return null;
        }

        return CalculationOutcome.InsufficientData(
            metric,
            InsufficientDataReason.OutsideKnowledgeCutoff,
            $"Not published by {context.Cutoff.AsOfUtc:O}: {string.Join(", ", late)}.");
    }

    internal static CalculationOutcome MissingFigure(MetricId metric, string attribute) =>
        CalculationOutcome.InsufficientData(
            metric,
            InsufficientDataReason.MissingInput,
            $"'{attribute}' was not reported for this period.");

    internal static CalculationOutcome UnitMismatch(MetricId metric, string detail) =>
        CalculationOutcome.InsufficientData(metric, InsufficientDataReason.UnitMismatch, detail);

    internal static CalculationOutcome Undefined(MetricId metric, string detail) =>
        CalculationOutcome.InsufficientData(metric, InsufficientDataReason.UndefinedResult, detail);
}
