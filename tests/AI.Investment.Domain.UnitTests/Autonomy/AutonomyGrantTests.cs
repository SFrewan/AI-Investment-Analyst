using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Autonomy;

/// <summary>
/// A grant is the permission itself, so the rules about what one may say are safety rules.
/// </summary>
public sealed class AutonomyGrantTests
{
    [Fact]
    public void A_grant_states_what_a_human_permitted_and_when_it_lapses()
    {
        var grant = AutonomyFixtures.Grant();

        Assert.Equal(AutonomyMode.AutoExecuteBounded, grant.EffectiveMode);
        Assert.Equal(AutonomyFixtures.Now.AddDays(7), grant.ExpiresAtUtc);
        Assert.True(grant.IsActive(AutonomyFixtures.Now));
        Assert.False(grant.HasExpired(AutonomyFixtures.Now));
    }

    /// <summary>
    /// Autonomy that never expires is autonomy nobody re-examines.
    /// </summary>
    [Fact]
    public void A_grant_must_expire()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            AutonomyFixtures.Grant(validFor: TimeSpan.Zero));

        Assert.Contains("must expire", error.Message, StringComparison.Ordinal);
        Assert.Contains("nobody re-examines", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_may_not_outrun_the_maximum_validity()
    {
        Assert.Throws<DomainValidationException>(() =>
            AutonomyFixtures.Grant(validFor: TimeSpan.FromDays(AutonomyGrant.MaxValidityDays + 1)));

        // The boundary itself is accepted: a rule that rejects the value it names is a different
        // rule from the one that was written down.
        var atLimit = AutonomyFixtures.Grant(validFor: TimeSpan.FromDays(AutonomyGrant.MaxValidityDays));

        Assert.Equal(AutonomyFixtures.Now.AddDays(AutonomyGrant.MaxValidityDays), atLimit.ExpiresAtUtc);
    }

    /// <summary>
    /// Structural, and the reason a grant is safe to be a database row: no value of one creates an
    /// authority the system does not implement.
    /// </summary>
    [Fact]
    public void No_grant_can_permit_financial_execution()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            AutonomyFixtures.Grant(capability: Capability.FinancialExecution));

        Assert.Equal("AutonomyGrant.NoFinancialExecution", error.Rule);
    }

    /// <summary>
    /// A grant that could administer the safety system unattended is a grant that can widen itself
    /// on the next pass.
    /// </summary>
    [Theory]
    [InlineData(Capability.PolicyAdministration)]
    [InlineData(Capability.AutonomyAdministration)]
    [InlineData(Capability.ApprovalAdministration)]
    public void No_grant_can_run_safety_administration_unattended(Capability capability)
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            AutonomyFixtures.Grant(capability: capability, mode: AutonomyMode.AutoExecuteBounded));

        Assert.Equal("AutonomyGrant.NoUnattendedSafetyAdministration", error.Rule);

        // Up to and including PrepareForApproval is permitted: preparing a change for a human to
        // approve is the whole point of routing it through the seam.
        var prepared = AutonomyFixtures.Grant(capability: capability, mode: AutonomyMode.PrepareForApproval);

        Assert.Equal(AutonomyMode.PrepareForApproval, prepared.EffectiveMode);
    }

    [Fact]
    public void A_grant_cannot_be_issued_in_the_unknown_mode()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            AutonomyFixtures.Grant(mode: AutonomyMode.Unknown));

        Assert.Contains("must name a mode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_must_name_a_person_an_environment_and_a_limit_set()
    {
        Assert.Throws<DomainValidationException>(() => AutonomyGrant.Issue(
            Capability.SimulatedExecution, null, AutonomyFixtures.Environment,
            AutonomyMode.Advise, RiskTier.Low, AutonomyFixtures.Usd(1m), "limits", "  ",
            AutonomyFixtures.Now, TimeSpan.FromDays(1)));

        Assert.Throws<DomainValidationException>(() => AutonomyGrant.Issue(
            Capability.SimulatedExecution, null, "  ",
            AutonomyMode.Advise, RiskTier.Low, AutonomyFixtures.Usd(1m), "limits", "operator",
            AutonomyFixtures.Now, TimeSpan.FromDays(1)));

        var error = Assert.Throws<DomainValidationException>(() => AutonomyGrant.Issue(
            Capability.SimulatedExecution, null, AutonomyFixtures.Environment,
            AutonomyMode.Advise, RiskTier.Low, AutonomyFixtures.Usd(1m), "  ", "operator",
            AutonomyFixtures.Now, TimeSpan.FromDays(1)));

        Assert.Contains("within nothing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_ceiling_is_refused()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            AutonomyFixtures.Grant(maxExposure: -1m));

        Assert.Contains("may not be negative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revoked_grant_is_no_longer_active_and_revoking_twice_is_not_an_error()
    {
        var grant = AutonomyFixtures.Grant();

        grant.Revoke("the measured approval rate fell", AutonomyFixtures.Now);

        Assert.True(grant.IsRevoked);
        Assert.False(grant.IsActive(AutonomyFixtures.Now));

        var firstReason = grant.RevocationReason;

        grant.Revoke("a second caller", AutonomyFixtures.Now.AddMinutes(1));

        Assert.Equal(firstReason, grant.RevocationReason);
        Assert.Equal(AutonomyFixtures.Now, grant.RevokedAtUtc);
    }

    /// <summary>
    /// The circuit breaker. One level at a time, so a crossed threshold degrades the system rather
    /// than switching it off, and repeated crossings walk it down without anybody watching.
    /// </summary>
    [Fact]
    public void Demotion_lowers_the_effective_mode_one_level_at_a_time()
    {
        var grant = AutonomyFixtures.Grant(mode: AutonomyMode.ContinuousBounded);

        Assert.True(grant.Demote("groundedness failures crossed the threshold", AutonomyFixtures.Now));
        Assert.Equal(AutonomyMode.AutoExecuteBounded, grant.EffectiveMode);
        Assert.Equal(AutonomyMode.ContinuousBounded, grant.GrantedMode);

        Assert.True(grant.Demote("again", AutonomyFixtures.Now));
        Assert.Equal(AutonomyMode.PrepareForApproval, grant.EffectiveMode);
        Assert.Equal(2, grant.DemotionCount);
    }

    [Fact]
    public void Demotion_stops_at_off_and_reports_that_it_did()
    {
        var grant = AutonomyFixtures.Grant(mode: AutonomyMode.Off);

        Assert.False(grant.Demote("nothing lower exists", AutonomyFixtures.Now));
        Assert.Equal(AutonomyMode.Off, grant.EffectiveMode);
        Assert.Equal(0, grant.DemotionCount);
    }

    /// <summary>
    /// There is no promotion method, and there must never be one. A circuit breaker that can close
    /// itself is not a circuit breaker.
    /// </summary>
    [Fact]
    public void A_grant_has_no_way_to_raise_its_own_autonomy()
    {
        var raising = typeof(AutonomyGrant)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(AutonomyGrant))
            .Where(method => method.Name.Contains("Promote", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Raise", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Widen", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Extend", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(raising);

        // And no public setter either: what a human granted is not a field anything can assign.
        var grantedMode = typeof(AutonomyGrant).GetProperty(nameof(AutonomyGrant.GrantedMode));

        Assert.NotNull(grantedMode);
        Assert.False(grantedMode!.SetMethod?.IsPublic ?? false);
    }

    [Fact]
    public void A_grant_must_be_issued_at_a_utc_time() =>
        Assert.Throws<DomainValidationException>(() =>
            AutonomyFixtures.Grant(nowUtc: DateTime.SpecifyKind(AutonomyFixtures.Now, DateTimeKind.Local)));
}
