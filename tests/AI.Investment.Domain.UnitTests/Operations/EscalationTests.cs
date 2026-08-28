using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Operations;

/// <summary>
/// The human is the authority on exceptions, and which exceptions is decided here rather than by a
/// model.
/// </summary>
public sealed class EscalationTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static EscalationSignals Clean() => new()
    {
        RiskTier = RiskTier.Low,
        EscalateAtOrAbove = RiskTier.High,
        Reversibility = ReversibilityClass.Reversible,
        ExposureBand = ExposureBand.Within,
        AutonomyMode = AutonomyMode.AutoExecuteBounded,
        LimitBreached = false,
        BudgetExhausted = false,
        ProviderFailed = false,
        EvidenceUntrustworthy = false,
        IsNovel = false,
    };

    // ---- The policy -------------------------------------------------------------------------

    [Fact]
    public void An_ordinary_permitted_action_does_not_reach_a_human() =>
        Assert.Equal(EscalationReason.None, EscalationPolicy.Required(Clean()));

    [Fact]
    public void No_resolved_grant_escalates_before_anything_else()
    {
        // Everything else is also wrong. The headline must still be the fail-closed one, because a
        // reader who is told "confidence was low" goes to the wrong question.
        var signals = Clean() with
        {
            AutonomyMode = AutonomyMode.Unknown,
            LimitBreached = true,
            Reversibility = ReversibilityClass.Irreversible,
        };

        Assert.Equal(EscalationReason.NoAutonomyGrant, EscalationPolicy.Required(signals));
    }

    [Fact]
    public void A_breached_limit_outranks_everything_below_it()
    {
        var signals = Clean() with { LimitBreached = true, IsNovel = true, ProviderFailed = true };

        Assert.Equal(EscalationReason.LimitBreach, EscalationPolicy.Required(signals));
    }

    /// <summary>Reversibility, not size, is the real axis.</summary>
    [Fact]
    public void An_irreversible_action_always_reaches_a_human() =>
        Assert.Equal(
            EscalationReason.Irreversible,
            EscalationPolicy.Required(Clean() with { Reversibility = ReversibilityClass.Irreversible }));

    [Fact]
    public void A_risk_tier_at_the_band_escalates()
    {
        Assert.Equal(
            EscalationReason.RiskTierAboveBand,
            EscalationPolicy.Required(Clean() with { RiskTier = RiskTier.High }));

        Assert.Equal(
            EscalationReason.None,
            EscalationPolicy.Required(Clean() with { RiskTier = RiskTier.Medium }));
    }

    [Theory]
    [InlineData(ExposureBand.Above)]
    [InlineData(ExposureBand.Incomparable)]
    [InlineData(ExposureBand.Unknown)]
    public void An_exposure_that_is_not_demonstrably_within_the_band_escalates(ExposureBand band) =>
        Assert.Equal(
            EscalationReason.ExposureAboveBand,
            EscalationPolicy.Required(Clean() with { ExposureBand = band }));

    [Fact]
    public void Untrustworthy_evidence_escalates() =>
        Assert.Equal(
            EscalationReason.UntrustworthyEvidence,
            EscalationPolicy.Required(Clean() with { EvidenceUntrustworthy = true }));

    /// <summary>
    /// "The step did not say how sure it was" is not evidence that it was sure.
    /// </summary>
    [Fact]
    public void A_configured_floor_with_no_stated_confidence_escalates()
    {
        var signals = Clean() with { ConfidenceFloor = Confidence.Create(0.7m), Confidence = null };

        Assert.Equal(EscalationReason.LowConfidence, EscalationPolicy.Required(signals));
    }

    [Fact]
    public void Confidence_below_the_floor_escalates_and_at_the_floor_does_not()
    {
        var floor = Confidence.Create(0.70m);

        Assert.Equal(
            EscalationReason.LowConfidence,
            EscalationPolicy.Required(Clean() with { ConfidenceFloor = floor, Confidence = Confidence.Create(0.69m) }));

        Assert.Equal(
            EscalationReason.None,
            EscalationPolicy.Required(Clean() with { ConfidenceFloor = floor, Confidence = floor }));
    }

    [Fact]
    public void No_floor_means_confidence_is_not_a_reason_to_escalate() =>
        Assert.Equal(
            EscalationReason.None,
            EscalationPolicy.Required(Clean() with { Confidence = Confidence.Create(0.01m) }));

    [Theory]
    [InlineData(true, false, false, EscalationReason.BudgetExhausted)]
    [InlineData(false, true, false, EscalationReason.ProviderFailure)]
    [InlineData(false, false, true, EscalationReason.Novelty)]
    public void The_remaining_conditions_each_escalate(
        bool budget,
        bool provider,
        bool novel,
        EscalationReason expected) =>
        Assert.Equal(
            expected,
            EscalationPolicy.Required(Clean() with
            {
                BudgetExhausted = budget,
                ProviderFailed = provider,
                IsNovel = novel,
            }));

    // ---- The record -------------------------------------------------------------------------

    [Fact]
    public void An_escalation_carries_the_case_and_expires()
    {
        var escalation = Escalation.Raise(
            Capability.SimulatedExecution,
            EscalationReason.LimitBreach,
            "the position-size ceiling would be exceeded",
            Now,
            TimeSpan.FromHours(24),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.False(escalation.IsResolved);
        Assert.False(escalation.HasExpired(Now));
        Assert.True(escalation.HasExpired(Now.AddHours(24)));
        Assert.False(escalation.IsUnhandled(Now));
        Assert.True(escalation.IsUnhandled(Now.AddHours(25)));
    }

    [Fact]
    public void An_escalation_must_name_a_condition_and_explain_itself()
    {
        Assert.Throws<DomainValidationException>(() => Escalation.Raise(
            Capability.Analysis, EscalationReason.None, "x", Now, TimeSpan.FromHours(1)));

        Assert.Throws<DomainValidationException>(() => Escalation.Raise(
            Capability.Analysis, EscalationReason.Novelty, "  ", Now, TimeSpan.FromHours(1)));

        Assert.Throws<DomainValidationException>(() => Escalation.Raise(
            Capability.Analysis, EscalationReason.Novelty, "x", Now, TimeSpan.Zero));
    }

    [Fact]
    public void Answering_an_escalation_records_who_and_what()
    {
        var escalation = Escalation.Raise(
            Capability.Analysis, EscalationReason.Novelty, "unfamiliar", Now, TimeSpan.FromHours(4));

        escalation.Acknowledge("operator@example.test", Now.AddMinutes(1));
        escalation.Resolve("operator@example.test", "approved after review", Now.AddMinutes(5));

        Assert.True(escalation.IsResolved);
        Assert.False(escalation.IsUnhandled(Now.AddDays(30)));
        Assert.Contains("approved after review", escalation.Resolution!, StringComparison.Ordinal);
    }

    /// <summary>Answering twice would leave the record showing two different decisions.</summary>
    [Fact]
    public void An_escalation_is_answered_once()
    {
        var escalation = Escalation.Raise(
            Capability.Analysis, EscalationReason.Novelty, "unfamiliar", Now, TimeSpan.FromHours(4));

        escalation.Resolve("operator", "approved", Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            escalation.Resolve("someone-else", "refused", Now.AddMinutes(1)));

        Assert.Equal("Escalation.AlreadyResolved", error.Rule);
    }

    [Fact]
    public void Resolving_without_acknowledging_records_the_acknowledgement_too()
    {
        var escalation = Escalation.Raise(
            Capability.Analysis, EscalationReason.Novelty, "unfamiliar", Now, TimeSpan.FromHours(4));

        escalation.Resolve("operator", "approved", Now.AddMinutes(3));

        Assert.True(escalation.IsAcknowledged);
        Assert.Equal(Now.AddMinutes(3), escalation.AcknowledgedAtUtc);
    }
}
