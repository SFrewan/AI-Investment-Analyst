using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// A completed measurement, and everything needed to explain or reproduce it.
/// </summary>
/// <remarks>
/// <para>
/// The number is the least of what this carries. A result that cannot say which inputs it used,
/// which formula combined them, which evidence stands behind each one, which period it describes
/// and which revision of the code produced it is a figure nobody can check - and an unbacked figure
/// is the thing this platform exists not to produce.
/// </para>
/// <para>
/// <strong>Look-ahead is refused at construction.</strong> Every input's evidence must have been
/// published by the context's knowledge cutoff. Enforcing it here rather than in each calculator
/// means a new metric cannot quietly acquire the defect by forgetting to check, which is how
/// look-ahead normally enters a system.
/// </para>
/// <para>
/// A class rather than a record: it holds collections, and record equality would compare those by
/// reference while presenting itself as value equality.
/// </para>
/// </remarks>
public sealed class MetricResult
{
    public const int MaxFormulaLength = 400;
    public const int MaxCaveatLength = 500;
    public const int MaxCaveats = 20;

    private readonly List<CalculationInput> _inputs;
    private readonly List<string> _caveats;

    private MetricResult(
        MetricId metric,
        IngestionSubject subject,
        MetricValue value,
        string formula,
        SourceId calculatorId,
        CalculationVersion version,
        DateTime asOfUtc,
        DateTime calculatedAtUtc,
        KnowledgeCutoff cutoff,
        List<CalculationInput> inputs,
        List<string> caveats)
    {
        Metric = metric;
        Subject = subject;
        Value = value;
        Formula = formula;
        CalculatorId = calculatorId;
        Version = version;
        AsOfUtc = asOfUtc;
        CalculatedAtUtc = calculatedAtUtc;
        Cutoff = cutoff;
        _inputs = inputs;
        _caveats = caveats;
    }

    public MetricId Metric { get; }

    public IngestionSubject Subject { get; }

    public MetricValue Value { get; }

    /// <summary>How the inputs were combined, in terms a reader can check against the code.</summary>
    public string Formula { get; }

    /// <summary>Which calculator produced this, so the result is attributable and reproducible.</summary>
    public SourceId CalculatorId { get; }

    public CalculationVersion Version { get; }

    /// <summary>The instant or period-end the measurement describes.</summary>
    public DateTime AsOfUtc { get; }

    /// <summary>When the measuring happened, which in a backtest is long after <see cref="AsOfUtc"/>.</summary>
    public DateTime CalculatedAtUtc { get; }

    /// <summary>What the platform was permitted to know when this was computed.</summary>
    public KnowledgeCutoff Cutoff { get; }

    public IReadOnlyList<CalculationInput> Inputs => _inputs;

    public IReadOnlyList<string> Caveats => _caveats;

    /// <summary>
    /// When the last piece of evidence underneath this measurement became public.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This, not <see cref="CalculatedAtUtc"/>, is when the measurement became <em>knowable</em>. A
    /// derived figure was available to anyone the moment its slowest input was; the arithmetic
    /// happening later is an artefact of when this platform got round to it.
    /// </para>
    /// <para>
    /// The distinction is load-bearing for chained calculations. Free cash flow feeding its own
    /// margin is a calculation standing on a calculation, and if the inner one were stamped with the
    /// wall-clock time it was computed, replaying 2021 today would reject it as evidence from the
    /// future - so nothing derived could ever be backtested.
    /// </para>
    /// </remarks>
    public DateTime EvidenceAvailableAtUtc => _inputs.Max(input => input.Provenance.PublishedAtUtc);

    public static MetricResult Create(
        CalculationContext context,
        MetricId metric,
        MetricValue value,
        string formula,
        SourceId calculatorId,
        CalculationVersion version,
        DateTime asOfUtc,
        IEnumerable<CalculationInput> inputs,
        IEnumerable<string>? caveats = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(calculatorId);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(inputs);

        var validatedFormula = ValidateFormula(formula);

        DateRange.EnsureUtc(asOfUtc, nameof(asOfUtc));

        if (asOfUtc > context.Cutoff.AsOfUtc)
        {
            throw new DomainRuleViolationException(
                "MetricResult.PeriodBeyondCutoff",
                $"A measurement describing {asOfUtc:O} cannot be produced under a knowledge cutoff " +
                $"of {context.Cutoff.AsOfUtc:O}. The period had not happened yet as far as this " +
                "calculation was allowed to know.");
        }

        var materialised = inputs.ToList();

        if (materialised.Count == 0)
        {
            throw new DomainRuleViolationException(
                "MetricResult.InputsRequired",
                "A measurement must record the inputs it was derived from. A number with no stated " +
                "inputs cannot be reproduced, explained or corrected.");
        }

        EnsureNoNullInputs(materialised);
        EnsureDistinctNames(materialised);
        EnsureWithinCutoff(materialised, context.Cutoff);

        return new MetricResult(
            metric,
            context.Subject,
            value,
            validatedFormula,
            calculatorId,
            version,
            asOfUtc,
            context.CalculatedAtUtc,
            context.Cutoff,
            materialised,
            NormaliseCaveats(caveats));
    }

    /// <summary>
    /// Expresses this measurement in the platform's epistemic vocabulary.
    /// </summary>
    /// <remarks>
    /// A <see cref="Enums.ClaimKind.Calculation"/>, never a fact: the number was derived, and the
    /// claims it derives from are named so a reader can walk back to the filings underneath. That
    /// walk is what separates a measurement from an assertion.
    /// </remarks>
    public Claim<decimal> ToClaim() =>
        Claims.Calculation(
            Value.Amount,
            Provenance.Create(CalculatorId, AsOfUtc, EvidenceAvailableAtUtc, CalculatedAtUtc),
            _inputs.Select(input => input.EvidenceId),
            _caveats);

    public override string ToString() => $"{Metric} for {Subject} = {Value} ({Version})";

    private static string ValidateFormula(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            throw new DomainValidationException(
                nameof(formula),
                "A measurement must state how its inputs were combined. Without the formula the " +
                "inputs and the result are two unrelated sets of numbers.");
        }

        var trimmed = formula.Trim();

        if (trimmed.Length > MaxFormulaLength)
        {
            throw new DomainValidationException(
                nameof(formula),
                $"A formula description may not exceed {MaxFormulaLength} characters.");
        }

        return trimmed;
    }

    private static void EnsureNoNullInputs(List<CalculationInput> inputs)
    {
        if (inputs.Any(input => input is null))
        {
            throw new DomainValidationException(
                nameof(inputs),
                "An input to a measurement may not be null.");
        }
    }

    private static void EnsureDistinctNames(List<CalculationInput> inputs)
    {
        var duplicates = inputs
            .GroupBy(input => input.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new DomainRuleViolationException(
                "MetricResult.DuplicateInputName",
                $"Each term in a formula must appear once. Repeated: {string.Join(", ", duplicates)}. " +
                "Two inputs under one name make it impossible to say which the formula used.");
        }
    }

    private static void EnsureWithinCutoff(List<CalculationInput> inputs, KnowledgeCutoff cutoff)
    {
        var lookAhead = inputs
            .Where(input => !cutoff.Admits(input.Provenance))
            .Select(input => $"{input.Name} (published {input.Provenance.PublishedAtUtc:O})")
            .ToList();

        if (lookAhead.Count > 0)
        {
            throw new DomainRuleViolationException(
                "MetricResult.LookAhead",
                $"These inputs were not published by the knowledge cutoff of {cutoff.AsOfUtc:O}: " +
                $"{string.Join(", ", lookAhead)}. A measurement built on information that did not " +
                "exist yet is the defect that makes a backtest look better than reality.");
        }
    }

    private static List<string> NormaliseCaveats(IEnumerable<string>? caveats)
    {
        var normalised = new List<string>();

        if (caveats is null)
        {
            return normalised;
        }

        foreach (var caveat in caveats)
        {
            if (string.IsNullOrWhiteSpace(caveat))
            {
                continue;
            }

            var trimmed = caveat.Trim();

            normalised.Add(trimmed.Length <= MaxCaveatLength ? trimmed : trimmed[..MaxCaveatLength]);

            if (normalised.Count == MaxCaveats)
            {
                break;
            }
        }

        return normalised;
    }
}
