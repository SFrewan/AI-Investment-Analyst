using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>
/// The bar a detection strategy must clear before its opportunities may compete for an action.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately lower than <c>PromotionCriteria</c> and asking different questions. Promotion is
/// about whether the platform may act without a person; admission is about whether a strategy's
/// numbers mean anything at all. A strategy can be admitted to the ranked pool and still be nowhere
/// near warranting autonomy - that is the normal case, and collapsing the two bars into one would
/// mean either admitting unmeasured strategies or refusing to rank anything until it was ready for
/// autonomy.
/// </para>
/// <para>
/// The trial budget is the multiple-comparisons control. Measuring twenty strategy variants against
/// one year of one dataset and keeping the best is a search, and the winner of a search is expected
/// to look good by chance. Nothing in the arithmetic of a single Brier score can see that, so the
/// count is declared, carried, and refused past a budget.
/// </para>
/// </remarks>
public sealed record AdmissionCriteria
{
    private AdmissionCriteria(
        int minimumResolvedPredictions,
        decimal maximumBrierScore,
        int maximumTrialsPerSearchFamily,
        TimeSpan maximumEvidenceAge,
        decimal power,
        decimal significance)
    {
        Power = power;
        Significance = significance;
        MinimumResolvedPredictions = minimumResolvedPredictions;
        MaximumBrierScore = maximumBrierScore;
        MaximumTrialsPerSearchFamily = maximumTrialsPerSearchFamily;
        MaximumEvidenceAge = maximumEvidenceAge;
    }

    /// <summary>
    /// The floor on how many predictions must have resolved before the score is read at all.
    /// </summary>
    /// <remarks>
    /// A floor rather than the whole answer. <see cref="SampleRequirement"/> derives a
    /// strategy-specific number from what that strategy claims, and applies whichever is larger. So
    /// this value is the least any strategy can be asked for, and several will be asked for more.
    /// </remarks>
    public int MinimumResolvedPredictions { get; }

    /// <summary>The chance of detecting the claimed effect when it is real.</summary>
    public decimal Power { get; }

    /// <summary>The one-sided significance the derived sample size is sized against.</summary>
    public decimal Significance { get; }

    /// <summary>
    /// The calibration ceiling. Note that 0.25 is what always saying fifty per cent scores, so any
    /// ceiling at or above it admits a strategy that carries no information.
    /// </summary>
    public decimal MaximumBrierScore { get; }

    /// <summary>
    /// How many variants may be measured within one search family.
    /// </summary>
    /// <remarks>
    /// Counted per family rather than per evidence base, which is the distinction the count could
    /// not previously make: twenty variants swept over one dataset is a search, and four
    /// independent hypotheses that happen to use the same dataset is not. Proliferation across
    /// families is controlled separately, by tightening the significance the sample size is derived
    /// at, so nothing escapes by opening a new family - it just pays in sample instead of being
    /// refused outright.
    /// </remarks>
    public int MaximumTrialsPerSearchFamily { get; }

    /// <summary>How old the measurement may be before it stops describing the present.</summary>
    public TimeSpan MaximumEvidenceAge { get; }

    /// <summary>
    /// The shipped bar: a hundred resolved predictions, a Brier at or under 0.20, at most five
    /// variants against one evidence base, and evidence no older than ninety days.
    /// </summary>
    /// <remarks>
    /// The hundred and the 0.20 are deliberately the same numbers the promotion bar uses, so that a
    /// strategy admitted to ranking is measured on the scale it will eventually be promoted on. The
    /// trial budget of five is a judgement rather than a derivation, and is the number this platform
    /// should revisit first if it ever finds itself refusing a strategy it believes in.
    /// </remarks>
    public static AdmissionCriteria Standard { get; } =
        new(
            minimumResolvedPredictions: 100,
            maximumBrierScore: 0.20m,
            maximumTrialsPerSearchFamily: 5,
            maximumEvidenceAge: TimeSpan.FromDays(90),
            power: SampleRequirement.DefaultPower,
            significance: SampleRequirement.DefaultSignificance);

    public static AdmissionCriteria Create(
        int minimumResolvedPredictions,
        decimal maximumBrierScore,
        int maximumTrialsPerSearchFamily,
        TimeSpan maximumEvidenceAge,
        decimal power = SampleRequirement.DefaultPower,
        decimal significance = SampleRequirement.DefaultSignificance)
    {
        if (power is <= 0m or >= 1m)
        {
            throw new DomainValidationException(
                nameof(power),
                $"Power is a probability strictly inside (0,1); {power} is not one. Certainty of " +
                "detection would demand an infinite sample.");
        }

        if (significance is <= 0m or >= 1m)
        {
            throw new DomainValidationException(
                nameof(significance),
                $"Significance is a probability strictly inside (0,1); {significance} is not one.");
        }

        if (minimumResolvedPredictions < 1)
        {
            throw new DomainValidationException(
                nameof(minimumResolvedPredictions),
                "A bar that accepts a sample of nothing is not a bar. Every strategy would clear it " +
                "on the day it was written.");
        }

        if (maximumBrierScore is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(maximumBrierScore),
                $"A Brier score lies between zero and one; {maximumBrierScore} as a ceiling either " +
                "refuses everything or accepts everything.");
        }

        if (maximumTrialsPerSearchFamily < 1)
        {
            throw new DomainValidationException(
                nameof(maximumTrialsPerSearchFamily),
                "A trial budget of zero refuses the first strategy ever measured, which is the one " +
                "case where searching is not a problem.");
        }

        if (maximumEvidenceAge <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(maximumEvidenceAge),
                "Evidence must be allowed to be at least a moment old.");
        }

        return new AdmissionCriteria(
            minimumResolvedPredictions,
            maximumBrierScore,
            maximumTrialsPerSearchFamily,
            maximumEvidenceAge,
            power,
            significance);
    }
}
