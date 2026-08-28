using System.Globalization;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Autonomy;

/// <summary>Why a capability may not be promoted to unattended execution.</summary>
/// <remarks>
/// <see cref="None"/> is zero and is the only value that means "may be promoted". Everything else is
/// a refusal, so a default-initialised or badly deserialised assessment refuses.
/// </remarks>
public enum PromotionRefusal
{
    /// <summary>Justified. The only non-refusal.</summary>
    None = 0,

    /// <summary>There is no measured performance report to argue from at all.</summary>
    NoValidationReport = 1,

    /// <summary>The report exists and establishes nothing. Not the same as establishing equality.</summary>
    PerformanceNotEstablished = 2,

    /// <summary>The report was measured, and the system did not beat the naive benchmark.</summary>
    NoBetterThanBenchmark = 3,

    /// <summary>Too few scored predictions for any rate to mean anything.</summary>
    SampleTooSmall = 4,

    /// <summary>The hit rate is below the declared floor, or could not be measured at all.</summary>
    HitRateBelowFloor = 5,

    /// <summary>The stated probabilities do not correspond to what happened.</summary>
    PoorlyCalibrated = 6,

    /// <summary>The shadow measurements do not show that acting more often would have been right.</summary>
    ShadowEvidenceAbsent = 7,

    /// <summary>The report is older than the declared freshness window.</summary>
    EvidenceStale = 8,

    /// <summary>The capability is one that may never run unattended, whatever the evidence says.</summary>
    CapabilityMayNeverBePromoted = 9,
}

/// <summary>
/// The thresholds a capability must clear before anybody may grant it unattended execution.
/// </summary>
/// <remarks>
/// <para>
/// Declared once, in code, and deliberately not configurable. Every other ceiling in this platform
/// lives in configuration so that an operator can tighten it during an incident; this one is the
/// opposite case. It is the bar for <em>widening</em> what the platform may do without anybody
/// watching, and a bar that can be lowered from a settings file is not a bar. Changing these numbers
/// is a code change somebody reviews.
/// </para>
/// <para>
/// The numbers themselves are conservative and arbitrary in the way any threshold is. What is not
/// arbitrary is that each of them must be <em>measured</em>: a metric that could not be computed
/// fails its check rather than passing it, which is why <see cref="PromotionAssessment.Evaluate"/>
/// reads <c>IsMeasured</c> before it reads any value.
/// </para>
/// </remarks>
public sealed record PromotionCriteria
{
    private PromotionCriteria(
        int minimumScoredPredictions,
        decimal minimumHitRate,
        decimal maximumBrierScore,
        decimal minimumExcessReturn,
        int minimumShadowDivergences,
        decimal minimumShadowDivergenceHitRate,
        TimeSpan maximumEvidenceAge)
    {
        MinimumScoredPredictions = minimumScoredPredictions;
        MinimumHitRate = minimumHitRate;
        MaximumBrierScore = maximumBrierScore;
        MinimumExcessReturn = minimumExcessReturn;
        MinimumShadowDivergences = minimumShadowDivergences;
        MinimumShadowDivergenceHitRate = minimumShadowDivergenceHitRate;
        MaximumEvidenceAge = maximumEvidenceAge;
    }

    /// <summary>Scored predictions the report must contain.</summary>
    public int MinimumScoredPredictions { get; }

    /// <summary>The share of calls to act that must have been right.</summary>
    public decimal MinimumHitRate { get; }

    /// <summary>The worst Brier score accepted. 0.25 is what always saying fifty per cent scores.</summary>
    public decimal MaximumBrierScore { get; }

    /// <summary>How far the system must have beaten buying and holding the index.</summary>
    public decimal MinimumExcessReturn { get; }

    /// <summary>
    /// Occasions a higher autonomy level would have acted on and the platform did not, with known
    /// outcomes. Without these, promotion is a guess about a decision nobody has watched being made.
    /// </summary>
    public int MinimumShadowDivergences { get; }

    /// <summary>The share of those extra actions that must have turned out right.</summary>
    public decimal MinimumShadowDivergenceHitRate { get; }

    /// <summary>How old the report may be. Evidence about a market ages.</summary>
    public TimeSpan MaximumEvidenceAge { get; }

    /// <summary>
    /// The standing bar. Deliberately hard to clear, because the thing it gates is the platform
    /// spending money while nobody is looking.
    /// </summary>
    public static PromotionCriteria Standard { get; } =
        new(
            minimumScoredPredictions: 100,
            minimumHitRate: 0.60m,
            maximumBrierScore: 0.20m,
            minimumExcessReturn: 0.00m,
            minimumShadowDivergences: 30,
            minimumShadowDivergenceHitRate: 0.60m,
            maximumEvidenceAge: TimeSpan.FromDays(90));

    public static PromotionCriteria Create(
        int minimumScoredPredictions,
        decimal minimumHitRate,
        decimal maximumBrierScore,
        decimal minimumExcessReturn,
        int minimumShadowDivergences,
        decimal minimumShadowDivergenceHitRate,
        TimeSpan maximumEvidenceAge)
    {
        if (minimumScoredPredictions < 1 || minimumShadowDivergences < 1)
        {
            throw new DomainValidationException(
                nameof(minimumScoredPredictions),
                "A promotion bar that accepts a sample of nothing is not a bar.");
        }

        if (minimumHitRate is < 0m or > 1m || minimumShadowDivergenceHitRate is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(minimumHitRate),
                "A required hit rate must be a share between zero and one.");
        }

        if (maximumBrierScore is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(maximumBrierScore),
                "A Brier score lies between zero and one. A ceiling outside that range accepts anything.");
        }

        if (maximumEvidenceAge <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(maximumEvidenceAge),
                "Evidence must be allowed to be at least a moment old, and must not be allowed to be " +
                "any age at all.");
        }

        return new PromotionCriteria(
            minimumScoredPredictions,
            minimumHitRate,
            maximumBrierScore,
            minimumExcessReturn,
            minimumShadowDivergences,
            minimumShadowDivergenceHitRate,
            maximumEvidenceAge);
    }
}

/// <summary>
/// Whether the measured evidence justifies letting one capability act unattended.
/// </summary>
/// <remarks>
/// <para>
/// The gate between Phase 7 and Phase 8, and a pure function of a report and a set of thresholds. It
/// produces a value, not an effect: nothing here promotes anything, and the only thing that can be
/// built from a justified assessment is a <see cref="PromotionWarrant"/>, which a human still has to
/// issue.
/// </para>
/// <para>
/// <strong>Every check reads availability before it reads a value.</strong> A metric that could not
/// be measured fails - it does not pass, and it does not get skipped. That is the difference between
/// "we looked and it was good enough" and "we could not tell", and the second must never be recorded
/// as the first. It is also why an empty report produces a long list of refusals rather than a short
/// one: each absent metric is its own reason.
/// </para>
/// </remarks>
public sealed record PromotionAssessment
{
    private PromotionAssessment(
        Capability capability,
        AutonomyMode proposedMode,
        Guid? validationRunId,
        string? benchmarkFingerprint,
        DateTime assessedAtUtc,
        IReadOnlyList<PromotionRefusal> refusals,
        IReadOnlyList<string> reasons)
    {
        Capability = capability;
        ProposedMode = proposedMode;
        ValidationRunId = validationRunId;
        BenchmarkFingerprint = benchmarkFingerprint;
        AssessedAtUtc = assessedAtUtc;
        Refusals = refusals;
        Reasons = reasons;
    }

    public Capability Capability { get; }

    /// <summary>The mode the assessment was made for. Never above <see cref="MaximumPromotableMode"/>.</summary>
    public AutonomyMode ProposedMode { get; }

    /// <summary>The report this was argued from, when there was one.</summary>
    public Guid? ValidationRunId { get; }

    /// <summary>The benchmark that report used, so the argument can be checked against the same one.</summary>
    public string? BenchmarkFingerprint { get; }

    public DateTime AssessedAtUtc { get; }

    public IReadOnlyList<PromotionRefusal> Refusals { get; }

    /// <summary>One sentence per refusal, in the terms of the evidence rather than of the code.</summary>
    public IReadOnlyList<string> Reasons { get; }

    /// <summary>True only when nothing refused. The single thing a warrant may be built from.</summary>
    public bool IsJustified => Refusals.Count == 0;

    /// <summary>
    /// The highest mode any evidence may ever justify.
    /// </summary>
    /// <remarks>
    /// <see cref="AutonomyMode.ContinuousBounded"/> is deliberately unreachable. It describes a
    /// platform that decides for itself when to act as well as what to do, and no measurement of past
    /// decisions is evidence about that. Reaching it would be a separate architectural decision, not a
    /// better report.
    /// </remarks>
    public static AutonomyMode MaximumPromotableMode => AutonomyMode.AutoExecuteBounded;

    /// <summary>
    /// Assesses one capability against one report. Pure, total, and fail-closed on every absence.
    /// </summary>
    public static PromotionAssessment Evaluate(
        Capability capability,
        AutonomyMode proposedMode,
        ValidationReport? report,
        PromotionCriteria criteria,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var refusals = new List<PromotionRefusal>();
        var reasons = new List<string>();

        void Refuse(PromotionRefusal refusal, string reason)
        {
            refusals.Add(refusal);
            reasons.Add(reason);
        }

        if (proposedMode > MaximumPromotableMode ||
            AutonomyGrant.IsSafetyAdministration(capability) ||
            capability == Capability.FinancialExecution)
        {
            Refuse(
                PromotionRefusal.CapabilityMayNeverBePromoted,
                $"'{capability}' at {proposedMode} is outside what any evidence may justify. " +
                "Financial execution has no execution plane, the safety capabilities administer the " +
                "system that would be doing the promoting, and ContinuousBounded is a different " +
                "architecture rather than a better report.");
        }

        if (report is null)
        {
            Refuse(
                PromotionRefusal.NoValidationReport,
                "there is no measured performance report to argue from. Promotion is a claim about " +
                "measured behaviour, and no measurement exists.");

            return new PromotionAssessment(
                capability, proposedMode, null, null, nowUtc, refusals, reasons);
        }

        if (nowUtc - report.GeneratedAtUtc > criteria.MaximumEvidenceAge)
        {
            Refuse(
                PromotionRefusal.EvidenceStale,
                Invariant($"the report was generated {report.GeneratedAtUtc:O}, more than ") +
                Invariant($"{criteria.MaximumEvidenceAge} ago. Evidence about a market ages."));
        }

        switch (report.Verdict)
        {
            case ValidationVerdict.BetterThanBenchmark:
                break;

            case ValidationVerdict.NotEstablished:
            case ValidationVerdict.Unknown:
            case ValidationVerdict.RefusedForIntegrity:
                Refuse(
                    PromotionRefusal.PerformanceNotEstablished,
                    $"the report's verdict is {report.Verdict}. A system that has not been measured " +
                    "is not a system that was measured and found adequate, and the two must not be " +
                    "recorded as the same thing.");
                break;

            case ValidationVerdict.NoBetterThanBenchmark:
            case ValidationVerdict.WorseThanBenchmark:
            default:
                Refuse(
                    PromotionRefusal.NoBetterThanBenchmark,
                    $"the report's verdict is {report.Verdict}. Buying the index requires no analysis " +
                    "and no autonomy, so a platform that does not beat it has no case for either.");
                break;
        }

        if (report.Matrix.Scored < criteria.MinimumScoredPredictions)
        {
            Refuse(
                PromotionRefusal.SampleTooSmall,
                Invariant($"{report.Matrix.Scored} predictions were scored against a required ") +
                Invariant($"minimum of {criteria.MinimumScoredPredictions}."));
        }

        Require(
            report.Matrix.HitRate,
            value => value >= criteria.MinimumHitRate,
            PromotionRefusal.HitRateBelowFloor,
            Invariant($"the hit rate must be at least {criteria.MinimumHitRate:P0}"));

        Require(
            report.Calibration.BrierScore,
            value => value <= criteria.MaximumBrierScore,
            PromotionRefusal.PoorlyCalibrated,
            Invariant($"the Brier score must be no worse than {criteria.MaximumBrierScore:0.00}"));

        Require(
            report.ExcessReturn,
            value => value >= criteria.MinimumExcessReturn,
            PromotionRefusal.NoBetterThanBenchmark,
            Invariant($"the excess return must be at least {criteria.MinimumExcessReturn:P2}"),
            unmeasured: PromotionRefusal.PerformanceNotEstablished);

        if (report.Shadow.ShadowWouldHaveExecutedAndActualDidNot < criteria.MinimumShadowDivergences)
        {
            Refuse(
                PromotionRefusal.ShadowEvidenceAbsent,
                Invariant($"a higher autonomy level would have acted on ") +
                Invariant($"{report.Shadow.ShadowWouldHaveExecutedAndActualDidNot} occasions the ") +
                Invariant($"platform declined, against a required {criteria.MinimumShadowDivergences}. ") +
                "Promotion without them is a guess about decisions nobody has watched being made.");
        }

        Require(
            report.Shadow.DivergenceHitRate,
            value => value >= criteria.MinimumShadowDivergenceHitRate,
            PromotionRefusal.ShadowEvidenceAbsent,
            Invariant($"the extra actions a higher level would have taken must have been right at ") +
                Invariant($"least {criteria.MinimumShadowDivergenceHitRate:P0} of the time"));

        return new PromotionAssessment(
            capability,
            proposedMode,
            report.RunId,
            report.Benchmark.Fingerprint,
            nowUtc,
            refusals,
            reasons);

        // A metric that could not be measured and a metric that was measured and fell short are
        // different findings, and they are refused under different names. Collapsing them would make
        // "we could not tell" read as "we looked and it was not good enough", which is the same
        // mistake the validation report refuses to make one layer down.
        void Require(
            Measurement measurement,
            Func<decimal, bool> clears,
            PromotionRefusal refusal,
            string requirement,
            PromotionRefusal? unmeasured = null)
        {
            if (!measurement.IsMeasured)
            {
                Refuse(
                    unmeasured ?? refusal,
                    $"{requirement}, and it could not be measured: {measurement.Explanation}");

                return;
            }

            if (!clears(measurement.Value!.Value))
            {
                Refuse(refusal, Invariant($"{requirement}, and it was {measurement.Value!.Value:P2}."));
            }
        }
    }

    public override string ToString() =>
        IsJustified
            ? $"promotion of {Capability} to {ProposedMode} is justified"
            : $"promotion of {Capability} to {ProposedMode} is NOT justified ({Refusals.Count} reasons)";

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
