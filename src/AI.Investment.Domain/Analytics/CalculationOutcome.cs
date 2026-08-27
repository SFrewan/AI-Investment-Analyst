using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// What a calculator returns: a measurement, or a stated reason there is none.
/// </summary>
/// <remarks>
/// <para>
/// Every calculator can decline. The alternative shapes all lie: returning zero produces a figure
/// that reads like a measurement, returning null pushes the decision to a caller that has less
/// information than the calculator did, and throwing turns an ordinary condition - a company that
/// has not reported yet - into an error.
/// </para>
/// <para>
/// A refusal is as auditable as a result. It names the metric it concerns, why the number could not
/// be produced, and enough detail for a reader to tell whether the gap is in the data or in the
/// formula.
/// </para>
/// </remarks>
public sealed class CalculationOutcome
{
    public const int MaxExplanationLength = 400;

    private CalculationOutcome(
        MetricId metric,
        MetricResult? result,
        InsufficientDataReason reason,
        string? explanation)
    {
        Metric = metric;
        Result = result;
        Reason = reason;
        Explanation = explanation;
    }

    public MetricId Metric { get; }

    public bool IsComputed => Result is not null;

    public MetricResult? Result { get; }

    /// <summary>
    /// <see cref="InsufficientDataReason.None"/> when a measurement was produced.
    /// </summary>
    public InsufficientDataReason Reason { get; }

    /// <summary>Present only on a refusal, and never blank when present.</summary>
    public string? Explanation { get; }

    public static CalculationOutcome Computed(MetricResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CalculationOutcome(result.Metric, result, InsufficientDataReason.None, null);
    }

    public static CalculationOutcome InsufficientData(
        MetricId metric,
        InsufficientDataReason reason,
        string explanation)
    {
        ArgumentNullException.ThrowIfNull(metric);

        if (!Enum.IsDefined(reason) || reason == InsufficientDataReason.None)
        {
            throw new DomainValidationException(
                nameof(reason),
                $"A refusal must state why. '{reason}' says only that a number is absent, which the " +
                "caller could already see.");
        }

        if (string.IsNullOrWhiteSpace(explanation))
        {
            throw new DomainValidationException(
                nameof(explanation),
                "A refusal must explain itself well enough for a reader to tell whether the gap is " +
                "in the data or in the formula.");
        }

        var trimmed = explanation.Trim();

        if (trimmed.Length > MaxExplanationLength)
        {
            trimmed = trimmed[..MaxExplanationLength];
        }

        return new CalculationOutcome(metric, null, reason, trimmed);
    }

    /// <summary>
    /// The measurement, for callers that have already established there is one.
    /// </summary>
    public MetricResult RequireResult() =>
        Result ?? throw new DomainRuleViolationException(
            "CalculationOutcome.NoResult",
            $"{Metric} produced no measurement ({Reason}): {Explanation} " +
            "Reading a result here would invent the number the calculator declined to state.");

    public override string ToString() =>
        IsComputed ? Result!.ToString() : $"{Metric}: no measurement ({Reason})";
}
