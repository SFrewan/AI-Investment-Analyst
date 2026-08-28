using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Autonomy;

/// <summary>
/// The gate between measured evidence and unattended execution.
/// </summary>
/// <remarks>
/// Almost every test here is about a refusal, and that is the right proportion. The permitted path is
/// one case; the ways a system quietly ends up acting on its own without the evidence to justify it
/// are many, and each of them has to be closed by something a reader can point at.
/// </remarks>
public sealed class PromotionGateTests
{
    private static readonly DateTime Now = JustifiedEvidence.Now;

    // ---- the assessment ------------------------------------------------------------------------

    /// <summary>
    /// The state the platform is actually in: no report, so no promotion, and a reason that says so.
    /// </summary>
    [Fact]
    public void No_validation_report_is_not_justification()
    {
        var assessment = PromotionAssessment.Evaluate(
            Capability.SimulatedExecution,
            AutonomyMode.AutoExecuteBounded,
            null,
            PromotionCriteria.Standard,
            Now);

        Assert.False(assessment.IsJustified);
        Assert.Contains(PromotionRefusal.NoValidationReport, assessment.Refusals);
        Assert.Contains(assessment.Reasons, reason =>
            reason.Contains("no measurement exists", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Not established" and "no better than the benchmark" are different findings, and neither
    /// promotes - but they must not be recorded as the same refusal.
    /// </summary>
    [Fact]
    public void An_unmeasured_report_refuses_differently_from_a_measured_one_that_lost()
    {
        var empty = Assess(Empty(ValidationVerdict.NotEstablished));

        // Nothing was measured, so nothing lost to the benchmark. Both the verdict and the
        // unmeasurable excess return refuse under "not established" rather than under "no better".
        Assert.Contains(PromotionRefusal.PerformanceNotEstablished, empty.Refusals);
        Assert.DoesNotContain(PromotionRefusal.NoBetterThanBenchmark, empty.Refusals);
    }

    /// <summary>Every unmeasured metric is its own refusal rather than a skipped check.</summary>
    [Fact]
    public void A_metric_that_could_not_be_measured_fails_rather_than_passes()
    {
        var assessment = Assess(Empty(ValidationVerdict.NotEstablished));

        Assert.False(assessment.IsJustified);
        Assert.Contains(PromotionRefusal.SampleTooSmall, assessment.Refusals);
        Assert.Contains(PromotionRefusal.HitRateBelowFloor, assessment.Refusals);
        Assert.Contains(PromotionRefusal.PoorlyCalibrated, assessment.Refusals);
        Assert.Contains(PromotionRefusal.ShadowEvidenceAbsent, assessment.Refusals);

        Assert.Contains(assessment.Reasons, reason =>
            reason.Contains("could not be measured", StringComparison.Ordinal));
    }

    [Fact]
    public void Evidence_older_than_the_freshness_window_no_longer_justifies_anything()
    {
        var stale = JustifiedEvidence.Report(Now.AddDays(-200));

        var assessment = PromotionAssessment.Evaluate(
            Capability.SimulatedExecution,
            AutonomyMode.AutoExecuteBounded,
            stale,
            PromotionCriteria.Standard,
            Now);

        Assert.False(assessment.IsJustified);
        Assert.Contains(PromotionRefusal.EvidenceStale, assessment.Refusals);
    }

    /// <summary>
    /// Autonomy is per capability. Evidence about one says nothing about another, and three of them
    /// may never be promoted at all.
    /// </summary>
    [Theory]
    [InlineData(Capability.FinancialExecution)]
    [InlineData(Capability.AutonomyAdministration)]
    [InlineData(Capability.PolicyAdministration)]
    [InlineData(Capability.ApprovalAdministration)]
    public void Some_capabilities_may_never_be_promoted_however_good_the_evidence(Capability capability)
    {
        var assessment = PromotionAssessment.Evaluate(
            capability,
            AutonomyMode.AutoExecuteBounded,
            JustifiedEvidence.Report(),
            PromotionCriteria.Standard,
            Now);

        Assert.False(assessment.IsJustified);
        Assert.Contains(PromotionRefusal.CapabilityMayNeverBePromoted, assessment.Refusals);
    }

    /// <summary>
    /// The top of the ladder is unreachable by evidence. Continuous autonomy is a different
    /// architecture, not a better report.
    /// </summary>
    [Fact]
    public void No_evidence_justifies_continuous_autonomy()
    {
        var assessment = PromotionAssessment.Evaluate(
            Capability.SimulatedExecution,
            AutonomyMode.ContinuousBounded,
            JustifiedEvidence.Report(),
            PromotionCriteria.Standard,
            Now);

        Assert.False(assessment.IsJustified);
        Assert.Contains(PromotionRefusal.CapabilityMayNeverBePromoted, assessment.Refusals);
        Assert.Equal(AutonomyMode.AutoExecuteBounded, PromotionAssessment.MaximumPromotableMode);
    }

    /// <summary>The permitted case, so that the gate is known to be passable at all.</summary>
    [Fact]
    public void Evidence_that_clears_every_criterion_justifies_promotion()
    {
        var assessment = JustifiedEvidence.Assessment();

        Assert.True(assessment.IsJustified, string.Join(" | ", assessment.Reasons));
        Assert.Empty(assessment.Refusals);
        Assert.NotNull(assessment.ValidationRunId);
        Assert.False(string.IsNullOrWhiteSpace(assessment.BenchmarkFingerprint));
    }

    // ---- the warrant ----------------------------------------------------------------------------

    /// <summary>
    /// The structural refusal the whole phase rests on: no warrant from unjustified evidence.
    /// </summary>
    [Fact]
    public void A_warrant_cannot_be_issued_from_an_assessment_that_refused()
    {
        var unjustified = PromotionAssessment.Evaluate(
            Capability.SimulatedExecution,
            AutonomyMode.AutoExecuteBounded,
            null,
            PromotionCriteria.Standard,
            Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            PromotionWarrant.Issue(
                unjustified,
                null,
                "Test",
                RiskTier.Low,
                JustifiedEvidence.Usd(1_000m),
                "operator@example.test",
                "the numbers looked close enough.",
                Now,
                TimeSpan.FromDays(7)));

        Assert.Equal(PromotionWarrant.UnjustifiedRule, error.Rule);
        Assert.Contains("does not justify promoting", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The canonical scope is the lowest-risk, reversible classes, and the warrant is where that
    /// binds. A warrant above the lowest tier cannot be issued at all.
    /// </summary>
    [Theory]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    [InlineData(RiskTier.Critical)]
    public void A_warrant_may_not_cover_more_than_the_lowest_risk_class(RiskTier tier)
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            PromotionWarrant.Issue(
                JustifiedEvidence.Assessment(),
                null,
                "Test",
                tier,
                JustifiedEvidence.Usd(1_000m),
                "operator@example.test",
                "justified.",
                Now,
                TimeSpan.FromDays(7)));

        Assert.Equal(PromotionWarrant.BeyondAssessmentRule, error.Rule);
        Assert.Equal(RiskTier.Low, BoundedExecutionRule.MaximumRiskTier);
    }

    [Fact]
    public void A_warrant_must_name_a_person_a_reason_and_an_expiry()
    {
        Assert.Throws<DomainValidationException>(() => Warrant(issuedBy: "  "));
        Assert.Throws<DomainValidationException>(() => Warrant(justification: "  "));
        Assert.Throws<DomainValidationException>(() => Warrant(validFor: TimeSpan.Zero));
        Assert.Throws<DomainValidationException>(() =>
            Warrant(validFor: TimeSpan.FromDays(PromotionWarrant.MaxValidityDays + 1)));
    }

    [Fact]
    public void A_warrant_records_the_evidence_it_was_argued_from()
    {
        var assessment = JustifiedEvidence.Assessment();
        var warrant = JustifiedEvidence.Warrant();

        Assert.Equal(Capability.SimulatedExecution, warrant.Capability);
        Assert.Equal(AutonomyMode.AutoExecuteBounded, warrant.MaxMode);
        Assert.NotEqual(Guid.Empty, warrant.ValidationRunId);
        Assert.Equal(assessment.BenchmarkFingerprint!.Length, warrant.BenchmarkFingerprint.Length);
    }

    // ---- what a warrant covers ------------------------------------------------------------------

    [Fact]
    public void A_warrant_covers_only_what_it_names()
    {
        var warrant = JustifiedEvidence.Warrant(actionType: "execution.simulated-order");

        Assert.Null(Cover(warrant));

        Assert.NotNull(Cover(warrant, capability: Capability.Analysis));
        Assert.NotNull(Cover(warrant, environment: "Production"));
        Assert.NotNull(Cover(warrant, actionType: "execution.something-else"));
        Assert.NotNull(Cover(warrant, mode: AutonomyMode.ContinuousBounded));
        Assert.NotNull(Cover(warrant, maxRiskTier: RiskTier.High));
        Assert.NotNull(Cover(warrant, maxExposure: 500_000m));
    }

    /// <summary>
    /// Two ceilings in different currencies have not been compared, so the warrant refuses rather
    /// than converting. There is no exchange rate anywhere in this platform, on purpose.
    /// </summary>
    [Fact]
    public void A_warrant_refuses_a_grant_it_cannot_compare_exposure_with()
    {
        var refusal = JustifiedEvidence.Warrant().WhyItDoesNotCover(
            Capability.SimulatedExecution,
            null,
            "Test",
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Low,
            Money.Create(1_000m, Currency.Create("EUR")),
            Now);

        Assert.NotNull(refusal);
        Assert.Contains("cannot be compared", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_or_revoked_warrant_covers_nothing()
    {
        var expired = JustifiedEvidence.Warrant(validFor: TimeSpan.FromDays(1));

        Assert.NotNull(Cover(expired, nowUtc: Now.AddDays(2)));
        Assert.Contains("expired", Cover(expired, nowUtc: Now.AddDays(2))!, StringComparison.Ordinal);

        var revoked = JustifiedEvidence.Warrant();

        revoked.Revoke("the evidence was re-examined and did not hold.", Now.AddHours(1));

        Assert.NotNull(Cover(revoked, nowUtc: Now.AddHours(2)));
        Assert.Contains("revoked", Cover(revoked, nowUtc: Now.AddHours(2))!, StringComparison.Ordinal);

        // Revoking twice is idempotent rather than an error: two operators, or an operator and the
        // breaker, must not fail because the warrant is already off.
        revoked.Revoke("again", Now.AddHours(3));

        Assert.True(revoked.IsRevoked);
    }

    // ---- grants under a warrant ------------------------------------------------------------------

    [Fact]
    public void A_bounded_grant_may_not_exceed_its_warrant_on_any_dimension()
    {
        var warrant = JustifiedEvidence.Warrant(maxExposure: 5_000m);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            AutonomyGrant.IssueBounded(
                warrant,
                null,
                "Test",
                AutonomyMode.AutoExecuteBounded,
                RiskTier.Low,
                JustifiedEvidence.Usd(50_000m),
                "limits.default",
                "operator@example.test",
                Now,
                TimeSpan.FromDays(7)));

        Assert.Equal(AutonomyGrant.BeyondWarrantRule, error.Rule);
    }

    [Fact]
    public void A_bounded_grant_records_the_warrant_that_permitted_it()
    {
        var warrant = JustifiedEvidence.Warrant();

        var grant = AutonomyGrant.IssueBounded(
            warrant,
            null,
            "Test",
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Low,
            JustifiedEvidence.Usd(1_000m),
            "limits.default",
            "operator@example.test",
            Now,
            TimeSpan.FromDays(7));

        Assert.Equal(AutonomyMode.AutoExecuteBounded, grant.GrantedMode);
        Assert.Equal(warrant.PromotionWarrantId, grant.PromotionWarrantId);
        Assert.True(grant.IsActive(Now));
    }

    /// <summary>An attended grant carries no warrant, and does not pretend to.</summary>
    [Fact]
    public void An_attended_grant_records_no_warrant()
    {
        var grant = AutonomyGrant.Issue(
            Capability.SimulatedExecution,
            null,
            "Test",
            AutonomyMode.PrepareForApproval,
            RiskTier.Medium,
            JustifiedEvidence.Usd(1_000m),
            "limits.default",
            "operator@example.test",
            Now,
            TimeSpan.FromDays(7));

        Assert.Null(grant.PromotionWarrantId);
        Assert.Equal(AutonomyMode.PrepareForApproval, AutonomyGrant.HighestAttendedMode);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static PromotionAssessment Assess(ValidationReport report) =>
        PromotionAssessment.Evaluate(
            Capability.SimulatedExecution,
            AutonomyMode.AutoExecuteBounded,
            report,
            PromotionCriteria.Standard,
            Now);

    private static ValidationReport Empty(ValidationVerdict expected)
    {
        var report = ValidationReport.Create(
            Guid.NewGuid(),
            Now,
            EvaluationWindow.Create(Now.AddDays(-180), Now.AddDays(-1), TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
            Percentage.Zero,
            CalculationVersion.Create(1, 0),
            JustifiedEvidence.Benchmark(Now),
            [],
            0,
            0,
            0,
            ConfusionMatrix.From([]),
            CalibrationCurve.From([]),
            Measurement.Unavailable("no positions"),
            Measurement.Unavailable("no prices"),
            ShadowComparisonResult.From([], new Dictionary<Guid, OutcomeLabel>()),
            [],
            []);

        Assert.Equal(expected, report.Verdict);

        return report;
    }

    private static PromotionWarrant Warrant(
        string issuedBy = "operator@example.test",
        string justification = "justified.",
        TimeSpan? validFor = null) =>
        PromotionWarrant.Issue(
            JustifiedEvidence.Assessment(),
            null,
            "Test",
            RiskTier.Low,
            JustifiedEvidence.Usd(1_000m),
            issuedBy,
            justification,
            Now,
            validFor ?? TimeSpan.FromDays(7));

    private static string? Cover(
        PromotionWarrant warrant,
        Capability capability = Capability.SimulatedExecution,
        string? actionType = "execution.simulated-order",
        string environment = "Test",
        AutonomyMode mode = AutonomyMode.AutoExecuteBounded,
        RiskTier maxRiskTier = RiskTier.Low,
        decimal maxExposure = 1_000m,
        DateTime? nowUtc = null) =>
        warrant.WhyItDoesNotCover(
            capability,
            actionType,
            environment,
            mode,
            maxRiskTier,
            JustifiedEvidence.Usd(maxExposure),
            nowUtc ?? Now);
}
