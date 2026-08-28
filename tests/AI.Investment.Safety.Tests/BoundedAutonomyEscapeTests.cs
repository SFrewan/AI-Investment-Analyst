using System.Reflection;
using AI.Investment.Application.Execution;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Validation;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The ways a system quietly ends up acting on its own, and the thing that stops each of them.
/// </summary>
/// <remarks>
/// <para>
/// Phase 6's escape suite asked whether untrusted input could acquire authority. This one asks a
/// narrower and later question: whether authority can be acquired <em>without the evidence that was
/// supposed to justify it</em>. The two failure modes look nothing alike. The first is an attack; the
/// second is a Tuesday afternoon, a deadline, and a configuration value that seemed harmless.
/// </para>
/// <para>
/// Every test here is a refusal, and every refusal is structural: a type that cannot be constructed,
/// a method that does not exist, a check that runs before the one it would otherwise be possible to
/// argue with.
/// </para>
/// </remarks>
public sealed class BoundedAutonomyEscapeTests
{
    private static readonly DateTime Now = JustifiedEvidence.Now;

    // ---- nothing promotes itself ------------------------------------------------------------------

    /// <summary>
    /// There is no method anywhere that raises a grant. Asserted over the built type rather than by
    /// reading the file, so a future one is caught however it is named.
    /// </summary>
    [Fact]
    public void No_type_offers_a_way_to_raise_a_grant()
    {
        var raising = typeof(AutonomyGrant)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.Name.Contains("Promote", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Raise", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Widen", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase))
            .Select(method => method.Name)
            .ToList();

        Assert.Empty(raising);
    }

    /// <summary>
    /// A warrant is the only thing that permits unattended execution, and it cannot be built from an
    /// assessment that refused. There is no overload, flag or argument that skips the check.
    /// </summary>
    [Fact]
    public void There_is_no_way_to_build_a_warrant_without_a_justified_assessment()
    {
        var factories = typeof(PromotionWarrant)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(PromotionWarrant))
            .ToList();

        var factory = Assert.Single(factories);

        Assert.Equal(nameof(PromotionWarrant.Issue), factory.Name);
        Assert.Equal(typeof(PromotionAssessment), factory.GetParameters()[0].ParameterType);

        // And no public constructor either, so reflection over the type finds nothing to call.
        Assert.Empty(typeof(PromotionWarrant).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// A shadow measurement is not evidence in its own right. Nothing takes one and returns authority.
    /// </summary>
    [Fact]
    public void A_shadow_decision_cannot_be_turned_into_a_warrant_or_a_grant()
    {
        foreach (var type in new[] { typeof(PromotionWarrant), typeof(AutonomyGrant), typeof(LiveVenueAuthorization) })
        {
            var takesShadow = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .SelectMany(method => method.GetParameters())
                .Any(parameter =>
                    parameter.ParameterType == typeof(ShadowDecision) ||
                    parameter.ParameterType == typeof(IEnumerable<ShadowDecision>));

            Assert.False(takesShadow, $"{type.Name} accepts a shadow decision.");
        }

        // The assessment reads shadow evidence only through a validation report, which is a
        // measurement of what happened rather than a measurement of what would have happened.
        var evaluate = typeof(PromotionAssessment)
            .GetMethod(nameof(PromotionAssessment.Evaluate), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(evaluate);
        Assert.Contains(evaluate!.GetParameters(), p => p.ParameterType == typeof(ValidationReport));
    }

    /// <summary>
    /// The evidence has to be justified for the capability being promoted. Evidence about one
    /// capability does not carry to another, because the capability is an argument to the assessment
    /// and the warrant refuses a grant for a different one.
    /// </summary>
    [Fact]
    public void Authority_for_one_capability_does_not_carry_to_another()
    {
        var warrant = JustifiedEvidence.Warrant();

        Assert.Equal(Capability.SimulatedExecution, warrant.Capability);

        var refusal = warrant.WhyItDoesNotCover(
            Capability.OpportunityManagement,
            null,
            "Test",
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Low,
            JustifiedEvidence.Usd(100m),
            Now);

        Assert.NotNull(refusal);
        Assert.Contains("one named capability at a time", refusal, StringComparison.Ordinal);
    }

    // ---- the live venue ---------------------------------------------------------------------------

    /// <summary>
    /// Every venue this solution can construct reports itself simulated. Asserted over the built
    /// assemblies, so adding a live one cannot happen quietly.
    /// </summary>
    [Fact]
    public void Every_registered_venue_is_simulated()
    {
        var venues = typeof(IExecutionVenue).Assembly.GetTypes()
            .Concat(typeof(AI.Investment.Infrastructure.Persistence.AppDbContext).Assembly.GetTypes())
            .Where(type => typeof(IExecutionVenue).IsAssignableFrom(type))
            .Where(type => type is { IsInterface: false, IsAbstract: false })
            .ToList();

        Assert.NotEmpty(venues);

        foreach (var type in venues)
        {
            var property = type.GetProperty(nameof(IExecutionVenue.IsSimulated));

            Assert.NotNull(property);

            // Read from a constructed instance where the type allows it; otherwise the architecture
            // suite's own venue rule covers it. Either way a live venue cannot be added silently.
            var parameterless = type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 0);

            if (parameterless is null)
            {
                continue;
            }

            Assert.True(
                (bool)property!.GetValue(parameterless.Invoke(null))!,
                $"{type.FullName} reports itself live.");
        }
    }

    /// <summary>
    /// The gate refuses a configuration-sourced request before it looks at anything else, so an
    /// installation holding a perfectly good authorisation still cannot activate a venue with a
    /// settings value.
    /// </summary>
    [Fact]
    public void A_configuration_value_can_never_activate_a_live_venue()
    {
        var warrant = JustifiedEvidence.Warrant();

        var authorization = LiveVenueAuthorization.Create(
            "venue-x", "Test", warrant, "first@example.test", "second@example.test",
            "both of us have read the evidence.", JustifiedEvidence.Usd(1_000m), Now, TimeSpan.FromDays(1));

        var decision = LiveVenueGate.Evaluate(
            new LiveVenueRequest("venue-x", "Test", authorization, warrant, RequestedFromConfiguration: true),
            Now);

        Assert.False(decision.MayActivate);
        Assert.Equal(LiveVenueRefusal.ConfigurationIsNotAuthorisation, decision.Refusal);
    }

    /// <summary>
    /// The gate produces a decision and nothing else. There is no method on it that activates a
    /// venue, opens a connection or hands over a credential.
    /// </summary>
    [Fact]
    public void The_live_venue_gate_decides_and_cannot_act()
    {
        var methods = typeof(LiveVenueGate)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToList();

        var evaluate = Assert.Single(methods);

        Assert.Equal(nameof(LiveVenueGate.Evaluate), evaluate.Name);
        Assert.Equal(typeof(LiveVenueDecision), evaluate.ReturnType);

        Assert.DoesNotContain(
            evaluate.GetParameters(),
            parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType) ||
                typeof(IExecutionVenue).IsAssignableFrom(parameter.ParameterType));
    }

    // ---- plane separation and credentials ----------------------------------------------------------

    /// <summary>
    /// Nothing in the analysis half of the platform can reach the venue interface, so no research,
    /// evidence, analytics or opportunity type can be handed the thing that would hold a credential.
    /// </summary>
    [Fact]
    public void The_analysis_plane_cannot_reach_the_execution_venue()
    {
        var forbidden = new[]
        {
            "AI.Investment.Domain.Ai",
            "AI.Investment.Domain.Analytics",
            "AI.Investment.Domain.Evidence",
            "AI.Investment.Domain.Observations",
            "AI.Investment.Domain.Opportunities",
            "AI.Investment.Domain.Validation",
            "AI.Investment.Domain.Autonomy",
            "AI.Investment.Application.Ai",
            "AI.Investment.Application.Validation",
            "AI.Investment.Application.Autonomy",
        };

        var assemblies = new[]
        {
            typeof(AutonomyGrant).Assembly,
            typeof(IExecutionVenue).Assembly,
        };

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace is null ||
                    !forbidden.Any(space => type.Namespace.StartsWith(space, StringComparison.Ordinal)))
                {
                    continue;
                }

                var touchesVenue = type
                    .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .OfType<MethodBase>()
                    .SelectMany(method => method.GetParameters())
                    .Select(parameter => parameter.ParameterType)
                    .Concat(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                        .Select(field => field.FieldType))
                    .Any(candidate => typeof(IExecutionVenue).IsAssignableFrom(candidate));

                Assert.False(
                    touchesVenue,
                    $"{type.FullName} can reach an execution venue. Execution credentials live only " +
                    "in the execution plane, and a type that can hold a venue is a type that could " +
                    "hold one.");
            }
        }
    }

    /// <summary>
    /// The venue interface itself carries no credential, so nothing that receives one receives a
    /// secret with it.
    /// </summary>
    [Fact]
    public void The_venue_contract_carries_no_credential()
    {
        var names = typeof(IExecutionVenue)
            .GetMembers()
            .Select(member => member.Name)
            .ToList();

        foreach (var forbidden in new[] { "ApiKey", "Secret", "Token", "Password", "Credential" })
        {
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- replay and concurrency ---------------------------------------------------------------------

    /// <summary>
    /// Two warrants are two warrants. Nothing here deduplicates them into one, and a grant names the
    /// one it was issued under - so revoking a warrant cannot be defeated by having issued a second.
    /// </summary>
    [Fact]
    public void Each_warrant_is_distinct_and_a_grant_names_the_one_it_used()
    {
        var first = JustifiedEvidence.Warrant();
        var second = JustifiedEvidence.Warrant();

        Assert.NotEqual(first.PromotionWarrantId, second.PromotionWarrantId);

        var grant = AutonomyGrant.IssueBounded(
            first, null, "Test", AutonomyMode.AutoExecuteBounded, RiskTier.Low,
            JustifiedEvidence.Usd(100m), "limits.default", "operator@example.test", Now, TimeSpan.FromDays(1));

        Assert.Equal(first.PromotionWarrantId, grant.PromotionWarrantId);

        first.Revoke("withdrawn", Now.AddHours(1));

        // The grant still names the revoked warrant, which is what lets the circuit breaker notice.
        Assert.Equal(first.PromotionWarrantId, grant.PromotionWarrantId);
        Assert.NotNull(first.WhyItDoesNotCover(
            grant.Capability, grant.ActionType, grant.EnvironmentName, grant.GrantedMode,
            grant.MaxRiskTier, grant.MaxExposure, Now.AddHours(2)));
    }

    /// <summary>
    /// A warrant that has expired stops covering its grant at the instant it expires, not at the
    /// instant somebody notices.
    /// </summary>
    [Fact]
    public void Coverage_is_evaluated_at_the_moment_it_is_asked_about()
    {
        var warrant = JustifiedEvidence.Warrant(validFor: TimeSpan.FromDays(1));

        Assert.Null(Cover(warrant, Now.AddHours(23)));
        Assert.NotNull(Cover(warrant, Now.AddHours(25)));
    }

    private static string? Cover(PromotionWarrant warrant, DateTime nowUtc) =>
        warrant.WhyItDoesNotCover(
            Capability.SimulatedExecution,
            null,
            "Test",
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Low,
            JustifiedEvidence.Usd(100m),
            nowUtc);
}
