using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Autonomy;

/// <summary>
/// The live-venue gate, the class of action that may run unattended, and the circuit breaker's rule.
/// </summary>
public sealed class LiveVenueAndDemotionTests
{
    private static readonly DateTime Now = JustifiedEvidence.Now;

    // ---- configuration is not authorisation -----------------------------------------------------

    /// <summary>
    /// The single most important test in the phase. A settings value cannot turn on real money, and
    /// the check is first, so it holds even when everything else about the request is in order.
    /// </summary>
    [Fact]
    public void Configuration_cannot_activate_a_live_venue_even_with_a_valid_authorisation()
    {
        var warrant = JustifiedEvidence.Warrant();
        var authorization = Authorization(warrant);

        var permitted = LiveVenueGate.Evaluate(
            new LiveVenueRequest("venue-x", "Test", authorization, warrant, RequestedFromConfiguration: false),
            Now);

        Assert.True(permitted.MayActivate);

        // The same request, arriving from a configuration value, is refused - and refused for that
        // reason rather than for any of the others.
        var fromConfiguration = LiveVenueGate.Evaluate(
            new LiveVenueRequest("venue-x", "Test", authorization, warrant, RequestedFromConfiguration: true),
            Now);

        Assert.False(fromConfiguration.MayActivate);
        Assert.Equal(LiveVenueRefusal.ConfigurationIsNotAuthorisation, fromConfiguration.Refusal);
        Assert.Contains("typed at midnight", fromConfiguration.Explanation, StringComparison.Ordinal);
    }

    /// <summary>The default answer, and the correct one for a platform in this state.</summary>
    [Fact]
    public void With_no_authorisation_the_gate_refuses_and_says_so_plainly()
    {
        var decision = LiveVenueGate.Evaluate(
            new LiveVenueRequest("venue-x", "Test", null, null, false),
            Now);

        Assert.False(decision.MayActivate);
        Assert.Equal(LiveVenueRefusal.NotAuthorised, decision.Refusal);
        Assert.Contains("the correct one", decision.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one control that is hard to defeat by accident is requiring somebody else to agree.
    /// </summary>
    [Fact]
    public void One_person_cannot_countersign_their_own_decision()
    {
        var warrant = JustifiedEvidence.Warrant();

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            LiveVenueAuthorization.Create(
                "venue-x",
                "Test",
                warrant,
                "operator@example.test",
                "OPERATOR@example.test",
                "we are confident.",
                JustifiedEvidence.Usd(1_000m),
                Now,
                TimeSpan.FromDays(1)));

        Assert.Equal(LiveVenueAuthorization.SameSignatoryRule, error.Rule);
        Assert.Contains("defeats it exactly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_venue_is_never_authorised_without_a_live_warrant()
    {
        var expired = JustifiedEvidence.Warrant(validFor: TimeSpan.FromDays(1));

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            LiveVenueAuthorization.Create(
                "venue-x", "Test", expired, "a@example.test", "b@example.test",
                "we are confident.", JustifiedEvidence.Usd(1_000m), Now.AddDays(2), TimeSpan.FromDays(1)));

        Assert.Equal(LiveVenueAuthorization.NoWarrantRule, error.Rule);

        Assert.Throws<DomainRuleViolationException>(() =>
            LiveVenueAuthorization.Create(
                "venue-x", "Test", null!, "a@example.test", "b@example.test",
                "we are confident.", JustifiedEvidence.Usd(1_000m), Now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void An_authorisation_must_be_bounded_in_money_and_in_time()
    {
        var warrant = JustifiedEvidence.Warrant();

        Assert.Throws<DomainValidationException>(() => Authorization(warrant, ceiling: 0m));
        Assert.Throws<DomainValidationException>(() => Authorization(warrant, validFor: TimeSpan.Zero));
        Assert.Throws<DomainValidationException>(() =>
            Authorization(warrant, validFor: TimeSpan.FromDays(LiveVenueAuthorization.MaxValidityDays + 1)));
    }

    [Fact]
    public void A_withdrawn_expired_or_mismatched_authorisation_activates_nothing()
    {
        var warrant = JustifiedEvidence.Warrant();

        var withdrawn = Authorization(warrant);
        withdrawn.Withdraw("we changed our minds.", Now.AddHours(1));

        Assert.Equal(
            LiveVenueRefusal.Withdrawn,
            Evaluate(withdrawn, warrant, nowUtc: Now.AddHours(2)).Refusal);

        Assert.Equal(
            LiveVenueRefusal.Expired,
            Evaluate(Authorization(warrant), warrant, nowUtc: Now.AddDays(3)).Refusal);

        Assert.Equal(
            LiveVenueRefusal.EnvironmentMismatch,
            Evaluate(Authorization(warrant), warrant, environment: "Production").Refusal);

        Assert.Equal(
            LiveVenueRefusal.VenueMismatch,
            Evaluate(Authorization(warrant), warrant, venueId: "venue-y").Refusal);

        Assert.Equal(
            LiveVenueRefusal.WarrantNoLongerValid,
            Evaluate(Authorization(warrant), null).Refusal);
    }

    // ---- the class of action that may run unattended ---------------------------------------------

    /// <summary>
    /// Lowest-risk and reversible. Reversible-at-a-cost is refused separately from irreversible,
    /// because the two are different mistakes and a reader should be told which one.
    /// </summary>
    [Theory]
    [InlineData(ReversibilityClass.Reversible, RiskTier.Low, BoundedExecutionRefusal.None)]
    [InlineData(ReversibilityClass.Reversible, RiskTier.Medium, BoundedExecutionRefusal.RiskTierTooHigh)]
    [InlineData(ReversibilityClass.ReversibleWithCost, RiskTier.Low, BoundedExecutionRefusal.ReversibleOnlyAtACost)]
    [InlineData(ReversibilityClass.Irreversible, RiskTier.Low, BoundedExecutionRefusal.NotReversible)]
    public void Only_the_lowest_risk_reversible_class_may_run_unattended(
        ReversibilityClass reversibility,
        RiskTier riskTier,
        BoundedExecutionRefusal expected) =>
        Assert.Equal(
            expected,
            BoundedExecutionRule.Admits(
                Capability.SimulatedExecution, reversibility, riskTier, AutonomyMode.AutoExecuteBounded));

    [Theory]
    [InlineData(Capability.FinancialExecution)]
    [InlineData(Capability.AutonomyAdministration)]
    [InlineData(Capability.PolicyAdministration)]
    [InlineData(Capability.ApprovalAdministration)]
    public void Some_capabilities_are_outside_the_class_whatever_the_action(Capability capability) =>
        Assert.Equal(
            BoundedExecutionRefusal.CapabilityExcluded,
            BoundedExecutionRule.Admits(
                capability, ReversibilityClass.Reversible, RiskTier.Low, AutonomyMode.AutoExecuteBounded));

    /// <summary>
    /// At or below preparing for approval somebody is still looking, so the class rule has no opinion.
    /// </summary>
    [Theory]
    [InlineData(AutonomyMode.Advise)]
    [InlineData(AutonomyMode.PrepareForApproval)]
    public void The_class_rule_has_no_opinion_about_attended_modes(AutonomyMode mode) =>
        Assert.Equal(
            BoundedExecutionRefusal.None,
            BoundedExecutionRule.Admits(
                Capability.SimulatedExecution, ReversibilityClass.Irreversible, RiskTier.Critical, mode));

    [Fact]
    public void An_unrecognised_mode_admits_nothing()
    {
        Assert.Equal(
            BoundedExecutionRefusal.ModeNotRecognised,
            BoundedExecutionRule.Admits(
                Capability.SimulatedExecution, ReversibilityClass.Reversible, RiskTier.Low,
                AutonomyMode.Unknown));

        Assert.Equal(
            BoundedExecutionRefusal.ModeNotRecognised,
            BoundedExecutionRule.Admits(
                Capability.SimulatedExecution, (ReversibilityClass)99, RiskTier.Low,
                AutonomyMode.AutoExecuteBounded));
    }

    // ---- automatic demotion ----------------------------------------------------------------------

    /// <summary>
    /// Fail closed. Every signal that cannot be read lowers the level, and that is checked before
    /// anything that reads a number.
    /// </summary>
    [Fact]
    public void A_signal_that_could_not_be_read_demotes()
    {
        Assert.Equal(DemotionTrigger.StateUnknown, Required(policyBreaches: null));
        Assert.Equal(DemotionTrigger.StateUnknown, Required(executionFailures: null));
        Assert.Equal(DemotionTrigger.StateUnknown, Required(unhandledEscalations: null));
        Assert.Equal(DemotionTrigger.StateUnknown, Required(evidenceAgeKnown: false));
    }

    [Fact]
    public void Nothing_wrong_leaves_the_grant_where_it_is() =>
        Assert.Equal(DemotionTrigger.None, Required());

    /// <summary>
    /// The order is the order of severity, so the reason recorded on a grant is the most serious
    /// thing that was true rather than whichever check happened to run first.
    /// </summary>
    [Fact]
    public void The_most_serious_reason_is_the_one_recorded()
    {
        var trigger = Required(
            killSwitch: true,
            warrantInvalid: true,
            policyBreaches: 5,
            executionFailures: 9,
            unhandledEscalations: 3);

        Assert.Equal(DemotionTrigger.KillSwitchEngaged, trigger);
    }

    [Theory]
    [InlineData(1, 0, 0, DemotionTrigger.PolicyBreach)]
    [InlineData(0, 3, 0, DemotionTrigger.ExecutionFailures)]
    [InlineData(0, 0, 1, DemotionTrigger.UnhandledEscalations)]
    public void Each_threshold_has_its_own_trigger(
        int breaches,
        int failures,
        int escalations,
        DemotionTrigger expected) =>
        Assert.Equal(
            expected,
            Required(policyBreaches: breaches, executionFailures: failures, unhandledEscalations: escalations));

    [Fact]
    public void A_grant_whose_warrant_stopped_covering_it_is_demoted()
    {
        Assert.Equal(DemotionTrigger.WarrantNoLongerValid, Required(warrantInvalid: true));
        Assert.Equal(DemotionTrigger.EvidenceNoLongerJustifies, Required(evidenceNoLongerJustifies: true));
        Assert.Equal(DemotionTrigger.EvidenceStale, Required(evidenceAge: TimeSpan.FromDays(400)));
    }

    [Fact]
    public void Thresholds_that_cannot_be_satisfied_are_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            DemotionPolicy.Required(Signals(), new DemotionThresholds(-1, 0, 0, TimeSpan.FromDays(1))));

        Assert.Throws<DomainValidationException>(() =>
            DemotionPolicy.Required(Signals(), new DemotionThresholds(0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>Demotion walks a grant down and stops at Off. There is no promotion path.</summary>
    [Fact]
    public void Demotion_walks_a_grant_down_one_level_at_a_time_and_stops()
    {
        var grant = AutonomyGrant.IssueBounded(
            JustifiedEvidence.Warrant(),
            null,
            "Test",
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Low,
            JustifiedEvidence.Usd(1_000m),
            "limits.default",
            "operator@example.test",
            Now,
            TimeSpan.FromDays(7));

        var levels = new List<AutonomyMode>();

        while (grant.Demote("threshold crossed", Now.AddMinutes(levels.Count + 1)))
        {
            levels.Add(grant.EffectiveMode);
        }

        Assert.Equal(
            [AutonomyMode.PrepareForApproval, AutonomyMode.Advise, AutonomyMode.ResearchOnly, AutonomyMode.Off],
            levels);

        Assert.Equal(AutonomyMode.Off, grant.EffectiveMode);
        Assert.False(grant.Demote("again", Now.AddHours(1)));

        // The granted mode is untouched, so the record still says what was permitted as well as what
        // is in force.
        Assert.Equal(AutonomyMode.AutoExecuteBounded, grant.GrantedMode);
        Assert.Null(typeof(AutonomyGrant).GetMethod("Promote"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static LiveVenueAuthorization Authorization(
        PromotionWarrant warrant,
        decimal ceiling = 1_000m,
        TimeSpan? validFor = null) =>
        LiveVenueAuthorization.Create(
            "venue-x",
            "Test",
            warrant,
            "first@example.test",
            "second@example.test",
            "the evidence holds and both of us have read it.",
            JustifiedEvidence.Usd(ceiling),
            Now,
            validFor ?? TimeSpan.FromDays(2));

    private static LiveVenueDecision Evaluate(
        LiveVenueAuthorization authorization,
        PromotionWarrant? warrant,
        string venueId = "venue-x",
        string environment = "Test",
        DateTime? nowUtc = null) =>
        LiveVenueGate.Evaluate(
            new LiveVenueRequest(venueId, environment, authorization, warrant, false),
            nowUtc ?? Now);

    private static DemotionSignals Signals(
        bool killSwitch = false,
        bool warrantInvalid = false,
        int? policyBreaches = 0,
        int? executionFailures = 0,
        int? unhandledEscalations = 0,
        bool evidenceNoLongerJustifies = false,
        TimeSpan? evidenceAge = null,
        bool evidenceAgeKnown = true) =>
        new()
        {
            KillSwitchEngagedOrUnknown = killSwitch,
            WarrantNoLongerValid = warrantInvalid,
            PolicyBreaches = policyBreaches,
            ExecutionFailures = executionFailures,
            UnhandledEscalations = unhandledEscalations,
            EvidenceNoLongerJustifies = evidenceNoLongerJustifies,
            EvidenceAge = evidenceAgeKnown ? evidenceAge ?? TimeSpan.FromDays(1) : null,
        };

    private static DemotionTrigger Required(
        bool killSwitch = false,
        bool warrantInvalid = false,
        int? policyBreaches = 0,
        int? executionFailures = 0,
        int? unhandledEscalations = 0,
        bool evidenceNoLongerJustifies = false,
        TimeSpan? evidenceAge = null,
        bool evidenceAgeKnown = true) =>
        DemotionPolicy.Required(
            Signals(killSwitch, warrantInvalid, policyBreaches, executionFailures, unhandledEscalations,
                evidenceNoLongerJustifies, evidenceAge, evidenceAgeKnown),
            DemotionThresholds.Standard);
}
