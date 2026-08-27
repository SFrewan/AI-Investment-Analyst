using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics.Scoring;

/// <summary>
/// A score, declared as data: which measurements it combines, how each is scaled, how much each
/// counts, and how much of it must be present before a number may be reported at all.
/// </summary>
/// <remarks>
/// <para>
/// Declarative rather than coded, because a score is a judgement about what matters and judgements
/// change. Holding the weights and ranges in a versioned specification means a stored score can be
/// re-derived years later from the specification it names, and that changing the weights produces a
/// new version rather than silently altering the meaning of every score already stored.
/// </para>
/// <para>
/// <see cref="MinimumCoverage"/> is the honest answer to a missing component. Refusing outright
/// makes one absent line item destroy a score that four other measurements support; renormalising
/// silently over whatever happens to be present reports a confident number built on half the
/// evidence. Stating a floor, and recording the coverage actually achieved, does neither.
/// </para>
/// </remarks>
public sealed class ScoringSpecification
{
    public const int MaxDescriptionLength = 400;

    private readonly List<ScoreComponent> _components;

    private ScoringSpecification(
        MetricId score,
        CalculationVersion version,
        List<ScoreComponent> components,
        decimal minimumCoverage,
        string description)
    {
        Score = score;
        Version = version;
        _components = components;
        MinimumCoverage = minimumCoverage;
        Description = description;
    }

    /// <summary>What this score is called. A score is a measurement, so it is named like one.</summary>
    public MetricId Score { get; }

    public CalculationVersion Version { get; }

    public IReadOnlyList<ScoreComponent> Components => _components;

    /// <summary>The proportion of total weight that must be present before a score is reported.</summary>
    public decimal MinimumCoverage { get; }

    public string Description { get; }

    public decimal TotalWeight => _components.Sum(component => component.Weight);

    public static ScoringSpecification Create(
        MetricId score,
        CalculationVersion version,
        IEnumerable<ScoreComponent> components,
        decimal minimumCoverage,
        string description)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(components);

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new DomainValidationException(
                nameof(description),
                "A score must say what it claims to measure. A weighted average of four ratios with " +
                "no stated meaning is a number nobody can argue with, which is the problem.");
        }

        if (minimumCoverage is <= 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(minimumCoverage),
                $"Required coverage must be greater than 0 and at most 1. Received {minimumCoverage}. " +
                "A floor of zero would let a score be reported from no evidence at all.");
        }

        var list = components.ToList();

        if (list.Count == 0)
        {
            throw new DomainValidationException(
                nameof(components),
                "A score with no components measures nothing.");
        }

        if (list.Any(component => component is null))
        {
            throw new DomainValidationException(nameof(components), "A component may not be null.");
        }

        var duplicates = list
            .GroupBy(component => component.Metric.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new DomainValidationException(
                nameof(components),
                $"A metric may count once in a score. Repeated: {string.Join(", ", duplicates)}. " +
                "Listing one twice weights it by stealth.");
        }

        var trimmed = description.Trim();

        return new ScoringSpecification(
            score,
            version,
            list,
            minimumCoverage,
            trimmed.Length <= MaxDescriptionLength ? trimmed : trimmed[..MaxDescriptionLength]);
    }

    public override string ToString() => $"{Score} ({Version}), {_components.Count} components";
}
