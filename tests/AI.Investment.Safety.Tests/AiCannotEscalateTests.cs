using System.Reflection;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The executable form of the platform's central safety claim: <strong>agent output is data, and
/// never execution authority.</strong>
/// </summary>
/// <remarks>
/// These are adversarial tests. Where the design says something is impossible, the test tries to
/// do it. A design claim nobody has attempted to violate is an assumption, not a guarantee.
/// </remarks>
public sealed class AiCannotEscalateTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The most direct escalation attempt: an agent proposing a change to the policy system.
    /// Refused structurally - see PolicyEngineTests for the configuration-independence proof.
    /// </summary>
    [Fact]
    public void An_agent_cannot_propose_a_change_to_policy_and_have_it_execute()
    {
        var engine = new PolicyEngine();

        var everythingAllowed = CapabilityPolicy.Create(
            Capability.PolicyAdministration,
            enabled: true,
            RiskTier.Critical,
            allowIrreversibleAutoExecute: true,
            allowAiProposers: true);

        var decision = engine.Evaluate(
            AgentProposal(Capability.PolicyAdministration),
            PolicyContext.Create("Test", KillSwitchState.Disengaged, [everythingAllowed]),
            Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    /// <summary>
    /// A proposal is a request, not a decision. There must be no way to construct a
    /// <see cref="PolicyDecision"/> except through the engine's own factories.
    /// </summary>
    [Fact]
    public void A_policy_decision_cannot_be_constructed_from_outside()
    {
        var publicConstructors = typeof(PolicyDecision)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(publicConstructors);
    }

    /// <summary>
    /// If a proposer could state its own risk tier, the classification would be exactly as
    /// trustworthy as whatever produced the proposal.
    /// </summary>
    [Fact]
    public void A_proposal_cannot_state_its_own_risk_tier()
    {
        var createParameters = typeof(ActionProposal)
            .GetMethod(nameof(ActionProposal.Create), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()
            .Select(p => p.ParameterType);

        Assert.DoesNotContain(typeof(RiskTier), createParameters);
    }

    [Fact]
    public void A_proposal_computes_its_risk_tier_from_its_economics()
    {
        var irreversible = ActionEconomics.Create(
            Money.ZeroUsd,
            Money.ZeroUsd,
            ReversibilityClass.Irreversible);

        Assert.Equal(RiskTier.High, ServiceProposal(economics: irreversible).RiskTier);
    }

    /// <summary>
    /// Policy objects must be immutable. If any of them acquired a public setter, code holding a
    /// reference could widen its own permissions.
    /// </summary>
    [Theory]
    [InlineData(typeof(CapabilityPolicy))]
    [InlineData(typeof(PolicyContext))]
    [InlineData(typeof(PolicyDecision))]
    [InlineData(typeof(ActionProposal))]
    public void Safety_types_expose_no_public_setter(Type type)
    {
        var settable = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);
    }

    /// <summary>
    /// The policy engine must not be reachable from an agent's output shape. Nothing an agent
    /// produces may carry a policy, a context or a decision.
    /// </summary>
    [Fact]
    public void No_safety_type_appears_in_the_action_parameters_contract()
    {
        var forbidden = new[]
        {
            typeof(CapabilityPolicy),
            typeof(PolicyContext),
            typeof(PolicyDecision),
            typeof(IPolicyEngine),
        };

        var members = typeof(IActionParameters).GetMembers();

        foreach (var member in members.OfType<MethodInfo>())
        {
            Assert.DoesNotContain(member.ReturnType, forbidden);
            Assert.DoesNotContain(member.GetParameters().Select(p => p.ParameterType), t => forbidden.Contains(t));
        }
    }

    /// <summary>
    /// An agent's proposal must be checkable. A proposal with no evidence cannot be verified for
    /// groundedness and must be treated as unfounded.
    /// </summary>
    [Fact]
    public void An_agent_proposal_without_evidence_is_rejected_at_construction() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            ActionProposal.Create(
                CorrelationId.New(),
                Capability.Analysis,
                ActionType.Create("test.action"),
                ActionTarget.Create("Test"),
                new TestParameters(),
                ActionEconomics.NoFinancialEffect(),
                ProposedBy.AiAgent("agent", "1.0", "prompts/p", "1.0"),
                Guid.NewGuid().ToString("n"),
                Now,
                confidence: Confidence.Create(0.9m)));

    [Fact]
    public void An_agent_proposal_without_confidence_is_rejected_at_construction() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            ActionProposal.Create(
                CorrelationId.New(),
                Capability.Analysis,
                ActionType.Create("test.action"),
                ActionTarget.Create("Test"),
                new TestParameters(),
                ActionEconomics.NoFinancialEffect(),
                ProposedBy.AiAgent("agent", "1.0", "prompts/p", "1.0"),
                Guid.NewGuid().ToString("n"),
                Now,
                evidence: [ClaimId.New()]));

    /// <summary>
    /// Without the prompt identity, a historical decision cannot be reproduced once the prompt
    /// changes - which quietly invalidates every later comparison.
    /// </summary>
    [Fact]
    public void An_agent_must_record_its_prompt_identity() =>
        Assert.Throws<DomainValidationException>(() =>
            ProposedBy.AiAgent("agent", "1.0", "", ""));

    private static ActionProposal AgentProposal(Capability capability) =>
        ActionProposal.Create(
            CorrelationId.New(),
            capability,
            ActionType.Create("policy.widen"),
            ActionTarget.Create("Policy"),
            new TestParameters(),
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.AiAgent("agent.rogue", "1.0", "prompts/rogue", "1.0"),
            Guid.NewGuid().ToString("n"),
            Now,
            evidence: [ClaimId.New()],
            confidence: Confidence.Create(0.99m));

    private static ActionProposal ServiceProposal(ActionEconomics economics) =>
        ActionProposal.Create(
            CorrelationId.New(),
            Capability.ReferenceDataManagement,
            ActionType.Create("test.action"),
            ActionTarget.Create("Test"),
            new TestParameters(),
            economics,
            ProposedBy.Service("test", "1.0"),
            Guid.NewGuid().ToString("n"),
            Now);

    private sealed record TestParameters : IActionParameters
    {
        public string Describe() => "test parameters";
    }
}
