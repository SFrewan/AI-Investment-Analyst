using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Autonomy;

/// <summary>
/// Resolution is the question "how much may this run without a human?", and it is answered
/// deterministically or not at all.
/// </summary>
/// <remarks>
/// The property that matters most across this file is that every rule <em>narrows</em>. There is no
/// combination of grants, exposures or tiers that produces a mode above what a human wrote down, and
/// several tests below exist only to say so.
/// </remarks>
public sealed class AutonomyResolverTests
{
    [Fact]
    public void A_matching_grant_resolves_to_the_mode_it_names()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [AutonomyFixtures.Grant()],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.AutoExecuteBounded, resolution.Mode);
        Assert.Equal(ExposureBand.Within, resolution.Band);
        Assert.True(resolution.PermitsUnattendedExecution);
    }

    /// <summary>
    /// The state every capability starts in, and the one it returns to when a grant lapses.
    /// </summary>
    [Fact]
    public void No_grant_resolves_to_unknown_which_denies()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.Unknown, resolution.Mode);
        Assert.True(resolution.Denies);
        Assert.Contains(AutonomyResolver.NoGrantRule, resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_grant_resolves_to_nothing()
    {
        var grant = AutonomyFixtures.Grant(validFor: TimeSpan.FromHours(1));

        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [grant],
            AutonomyFixtures.Now.AddHours(2));

        Assert.True(resolution.Denies);
    }

    [Fact]
    public void A_revoked_grant_resolves_to_nothing()
    {
        var grant = AutonomyFixtures.Grant();

        grant.Revoke("withdrawn", AutonomyFixtures.Now);

        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [grant],
            AutonomyFixtures.Now);

        Assert.True(resolution.Denies);
    }

    /// <summary>
    /// A permission granted where the venue is simulated carries no weight where it is not.
    /// </summary>
    [Fact]
    public void A_grant_does_not_travel_between_environments()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(environment: "Production"),
            [AutonomyFixtures.Grant(environment: "Development")],
            AutonomyFixtures.Now);

        Assert.True(resolution.Denies);
    }

    /// <summary>
    /// Which grant won would depend on ordering nobody controls, and the answer would be "sometimes
    /// more autonomous".
    /// </summary>
    [Fact]
    public void Two_equally_specific_grants_are_refused_rather_than_resolved()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [AutonomyFixtures.Grant(), AutonomyFixtures.Grant(mode: AutonomyMode.ContinuousBounded)],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.Off, resolution.Mode);
        Assert.True(resolution.Denies);
        Assert.Contains(AutonomyResolver.AmbiguousGrantRule, resolution.Reason, StringComparison.Ordinal);
    }

    /// <summary>The narrower statement is the more deliberate one.</summary>
    [Fact]
    public void A_grant_naming_the_action_type_beats_one_covering_the_capability()
    {
        var wide = AutonomyFixtures.Grant(mode: AutonomyMode.ContinuousBounded);
        var narrow = AutonomyFixtures.Grant(
            actionType: "execution.simulated-order",
            mode: AutonomyMode.PrepareForApproval);

        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(actionType: "execution.simulated-order"),
            [wide, narrow],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.PrepareForApproval, resolution.Mode);
        Assert.Equal(narrow.AutonomyGrantId, resolution.AutonomyGrantId);
    }

    /// <summary>
    /// Above a ceiling an action does not stop being possible - it stops being possible unattended.
    /// </summary>
    [Fact]
    public void Exposure_above_the_ceiling_withdraws_unattended_execution_rather_than_denying()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(exposure: 50_000m),
            [AutonomyFixtures.Grant(maxExposure: 10_000m)],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.PrepareForApproval, resolution.Mode);
        Assert.Equal(ExposureBand.Above, resolution.Band);
        Assert.False(resolution.PermitsUnattendedExecution);
        Assert.Contains(AutonomyResolver.ExposureAboveCeilingRule, resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_risk_tier_above_the_ceiling_withdraws_unattended_execution()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(riskTier: RiskTier.Critical),
            [AutonomyFixtures.Grant(maxRiskTier: RiskTier.Medium)],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.PrepareForApproval, resolution.Mode);
        Assert.Contains(AutonomyResolver.RiskTierAboveCeilingRule, resolution.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ceiling that cannot be compared has not been shown to hold, and this platform has no
    /// exchange rate anywhere in it.
    /// </summary>
    [Fact]
    public void An_exposure_in_another_currency_cannot_be_compared_and_denies()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(exposure: 1m, currency: "EUR"),
            [AutonomyFixtures.Grant(maxExposure: 10_000m)],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.Off, resolution.Mode);
        Assert.Equal(ExposureBand.Incomparable, resolution.Band);
        Assert.Contains(AutonomyResolver.ExposureIncomparableRule, resolution.Reason, StringComparison.Ordinal);
    }

    /// <summary>Zero is zero in any currency, so it is the one comparison that needs no rate.</summary>
    [Fact]
    public void A_zero_exposure_is_comparable_whatever_currency_it_names()
    {
        var resolution = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(exposure: 0m, currency: "JPY"),
            [AutonomyFixtures.Grant(maxExposure: 10_000m)],
            AutonomyFixtures.Now);

        Assert.Equal(ExposureBand.None, resolution.Band);
        Assert.Equal(AutonomyMode.AutoExecuteBounded, resolution.Mode);
    }

    /// <summary>
    /// The property the whole design rests on: every ceiling narrows, and nothing widens.
    /// </summary>
    [Fact]
    public void Resolution_is_total_and_never_exceeds_what_the_grant_says()
    {
        var modes = Enum.GetValues<AutonomyMode>().Where(m => m != AutonomyMode.Unknown).ToList();
        var tiers = Enum.GetValues<RiskTier>();
        var exposures = new[] { 0m, 1m, 10_000m, 50_000m };
        var evaluated = 0;

        foreach (var mode in modes)
        {
            foreach (var tier in tiers)
            {
                foreach (var exposure in exposures)
                {
                    var grant = AutonomyFixtures.Grant(
                        capability: Capability.OpportunityManagement,
                        mode: mode,
                        maxRiskTier: RiskTier.Medium,
                        maxExposure: 10_000m);

                    var resolution = AutonomyResolver.Resolve(
                        AutonomyFixtures.Request(
                            capability: Capability.OpportunityManagement,
                            riskTier: tier,
                            exposure: exposure),
                        [grant],
                        AutonomyFixtures.Now);

                    Assert.NotNull(resolution);
                    Assert.False(string.IsNullOrWhiteSpace(resolution.Reason));
                    Assert.True(resolution.Mode <= mode, $"{resolution.Mode} exceeded the granted {mode}");

                    evaluated++;
                }
            }
        }

        Assert.Equal(modes.Count * tiers.Length * exposures.Length, evaluated);
    }

    [Fact]
    public void Resolution_refuses_a_non_utc_instant() =>
        Assert.Throws<DomainValidationException>(() => AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [AutonomyFixtures.Grant()],
            DateTime.SpecifyKind(AutonomyFixtures.Now, DateTimeKind.Local)));

    [Fact]
    public void A_request_names_its_action_type_and_environment()
    {
        Assert.Throws<DomainValidationException>(() => AutonomyFixtures.Request(actionType: "  "));
        Assert.Throws<DomainValidationException>(() => AutonomyFixtures.Request(environment: "  "));
    }

    /// <summary>
    /// Shadow mode measures the next level up, and the next level up is capped at the highest there
    /// is rather than running off the end of the enum.
    /// </summary>
    [Fact]
    public void The_next_mode_up_is_capped_at_the_highest_level()
    {
        var top = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [AutonomyFixtures.Grant(mode: AutonomyMode.ContinuousBounded)],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.ContinuousBounded, top.NextModeUp);

        var middle = AutonomyResolver.Resolve(
            AutonomyFixtures.Request(),
            [AutonomyFixtures.Grant(mode: AutonomyMode.PrepareForApproval)],
            AutonomyFixtures.Now);

        Assert.Equal(AutonomyMode.AutoExecuteBounded, middle.NextModeUp);
    }
}
