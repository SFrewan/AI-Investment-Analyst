using System.ComponentModel.DataAnnotations;
using AI.Investment.Domain.Opportunities.Admission;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// The bar a detection strategy must clear before its opportunities may compete for an action.
/// </summary>
/// <remarks>
/// The defaults are <see cref="AdmissionCriteria.Standard"/>, restated here so that an operator
/// reading configuration sees the numbers rather than a name. Loosening them is a decision with
/// consequences an audit should be able to find, which is why they are settings rather than
/// constants - and why the ceiling is validated against 0.25, the score a coin flip earns.
/// </remarks>
public sealed class AdmissionOptions
{
    public const string SectionName = "Admission";

    /// <summary>How many predictions must have resolved before the score is read at all.</summary>
    [Range(1, 1000000)]
    public int MinimumResolvedPredictions { get; init; } = 100;

    /// <summary>The calibration ceiling. Anything at or above 0.25 admits a coin flip.</summary>
    [Range(0, 1)]
    public decimal MaximumBrierScore { get; init; } = 0.20m;

    /// <summary>How many variants may be measured within one search family.</summary>
    [Range(1, 1000)]
    public int MaximumTrialsPerSearchFamily { get; init; } = 5;

    /// <summary>How old a measurement may be before it stops describing the present.</summary>
    public TimeSpan MaximumEvidenceAge { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// The chance of detecting a strategy's claimed effect when it is real.
    /// </summary>
    /// <remarks>
    /// Feeds the per-strategy sample size the gate derives. Raising it demands more resolved
    /// predictions; lowering it demands fewer, but never below
    /// <see cref="MinimumResolvedPredictions"/>, which is a floor rather than a starting point.
    /// </remarks>
    public decimal Power { get; init; } = SampleRequirement.DefaultPower;

    /// <summary>The one-sided significance the derived sample size is computed against.</summary>
    public decimal Significance { get; init; } = SampleRequirement.DefaultSignificance;

    /// <summary>
    /// Names the dataset strategies are currently scored against.
    /// </summary>
    /// <remarks>
    /// It exists so that a strategy's declaration can say which data it was fitted on and the gate
    /// can compare the two strings. Change it whenever the evidence base changes - a fingerprint
    /// that never moves would let an in-sample score pass for an out-of-sample one forever.
    /// </remarks>
    [Required]
    public string EvidenceBaseFingerprint { get; init; } = "us-large-cap-20x250-2025-09-to-2026-08";

    public AdmissionCriteria ToCriteria() =>
        AdmissionCriteria.Create(
            MinimumResolvedPredictions,
            MaximumBrierScore,
            MaximumTrialsPerSearchFamily,
            MaximumEvidenceAge,
            Power,
            Significance);
}
