using AI.Investment.Application.Actions;
using AI.Investment.Application.Approvals;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// Approving is itself an action, and it passes the same gate as everything else.
/// </summary>
/// <remarks>
/// <para>
/// The rule these tests hold in place is the one the whole architecture rests on: <strong>an AI
/// proposer may never administer the safety system</strong>. The policy engine evaluates that rule
/// before the first configurable one, so no combination of settings can permit it - and this suite
/// asserts it against the real engine rather than against a description of it.
/// </para>
/// <para>
/// These tests run the real <c>ActionGateway</c> and the real <c>PolicyEngine</c>. Only the stores
/// and the clock are doubles.
/// </para>
/// </remarks>
public sealed class ApprovalSeamSafetyTests
{
    [Fact]
    public async Task An_approval_is_issued_only_when_policy_permits_the_capability()
    {
        var harness = new ApprovalHarness(PermitsApproval());
        var scenario = Approvable();

        await harness.Opportunities.AddAsync(scenario.Opportunity);

        var outcome = await harness.Workflow.ApproveAsync(
            scenario.Request,
            scenario.Proposal,
            "operator@example.test",
            scenario.Proposal.Economics.EstimatedExposure);

        Assert.True(outcome.Issued);
        Assert.NotNull(outcome.Token);
        Assert.Single(harness.Tokens.Stored);
        Assert.NotEmpty(harness.Audit.Records);
    }

    [Fact]
    public async Task With_no_policy_for_approval_administration_no_token_is_stored()
    {
        var harness = new ApprovalHarness(PolicyContext.FailClosed("Test"));
        var scenario = Approvable();

        await harness.Opportunities.AddAsync(scenario.Opportunity);

        var outcome = await harness.Workflow.ApproveAsync(
            scenario.Request,
            scenario.Proposal,
            "operator@example.test",
            scenario.Proposal.Economics.EstimatedExposure);

        Assert.False(outcome.Issued);
        Assert.Equal(ApprovalOutcomeStatus.DeniedByPolicy, outcome.Status);
        Assert.Empty(harness.Tokens.Stored);
    }

    [Fact]
    public async Task Approving_the_same_action_twice_is_suppressed_rather_than_issuing_a_second_token()
    {
        var harness = new ApprovalHarness(PermitsApproval());
        var scenario = Approvable();

        await harness.Opportunities.AddAsync(scenario.Opportunity);

        await harness.Workflow.ApproveAsync(
            scenario.Request,
            scenario.Proposal,
            "operator@example.test",
            scenario.Proposal.Economics.EstimatedExposure);

        var second = await harness.Workflow.ApproveAsync(
            scenario.Request,
            scenario.Proposal,
            "operator@example.test",
            scenario.Proposal.Economics.EstimatedExposure);

        Assert.Equal(ApprovalOutcomeStatus.DuplicateSuppressed, second.Status);
        Assert.Single(harness.Tokens.Stored);
    }

    [Fact]
    public async Task An_ai_proposer_can_never_administer_approvals_however_the_policy_is_configured()
    {
        // Deliberately the most permissive configuration that could be written: the capability is
        // enabled, AI proposers are allowed, irreversible auto-execution is allowed, and the
        // ceiling is Critical. The structural rule is evaluated before all of it.
        var permissive = PolicyContext.Create(
            "Test",
            KillSwitchState.Disengaged,
            [
                CapabilityPolicy.Create(
                    Capability.ApprovalAdministration,
                    enabled: true,
                    RiskTier.Critical,
                    allowIrreversibleAutoExecute: true,
                    allowAiProposers: true),
            ]);

        var harness = new ApprovalHarness(permissive);

        var proposal = ActionProposal.Create(
            CorrelationId.New(),
            Capability.ApprovalAdministration,
            ActionType.Create("approval.issue-token"),
            ActionTarget.Create("Opportunity", Guid.NewGuid().ToString()),
            new AgentParameters(),
            ActionEconomics.NoFinancialEffect(Currency.Usd),
            ProposedBy.AiAgent("synthesis", "1.0", "synthesist/analysis-synthesis", "1.0"),
            Guid.NewGuid().ToString("n"),
            Phase5Fixtures.Now,
            evidence: [ClaimId.New()],
            confidence: Confidence.Create(0.9m));

        var invoked = false;

        var outcome = await harness.Gateway.DispatchAsync(
            proposal,
            _ =>
            {
                invoked = true;

                return Task.FromResult(true);
            });

        Assert.Equal(ActionOutcomeStatus.Denied, outcome.Status);
        Assert.False(invoked);
        Assert.Contains("structural", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Financial_execution_stays_refused_whatever_the_configuration_says()
    {
        var permissive = PolicyContext.Create(
            "Test",
            KillSwitchState.Disengaged,
            [
                CapabilityPolicy.Create(
                    Capability.FinancialExecution,
                    enabled: true,
                    RiskTier.Critical,
                    allowIrreversibleAutoExecute: true,
                    allowAiProposers: true),
            ]);

        var harness = new ApprovalHarness(permissive);

        var proposal = ActionProposal.Create(
            CorrelationId.New(),
            Capability.FinancialExecution,
            ActionType.Create("execution.place-order"),
            ActionTarget.Create("Instrument", "AAPL"),
            new AgentParameters(),
            ActionEconomics.Create(
                Money.Zero(Currency.Usd),
                Money.Create(1m, Currency.Usd),
                ReversibilityClass.ReversibleWithCost),
            ProposedBy.Human("operator@example.test"),
            Guid.NewGuid().ToString("n"),
            Phase5Fixtures.Now);

        var invoked = false;

        var outcome = await harness.Gateway.DispatchAsync(
            proposal,
            _ =>
            {
                invoked = true;

                return Task.FromResult(true);
            });

        Assert.Equal(ActionOutcomeStatus.Denied, outcome.Status);
        Assert.False(invoked);
    }

    private static PolicyContext PermitsApproval() =>
        PolicyContext.Create(
            "Test",
            KillSwitchState.Disengaged,
            [CapabilityPolicy.Create(Capability.ApprovalAdministration, enabled: true, RiskTier.High)]);

    private static (Opportunity Opportunity, ActionProposal Proposal, ApprovalRequest Request) Approvable()
    {
        var (opportunity, proposal) = Phase5Fixtures.Pending();

        return (
            opportunity,
            proposal,
            ApprovalRequest.For(opportunity, proposal, Phase5Fixtures.Now));
    }

    private sealed record AgentParameters : IActionParameters
    {
        public string Describe() => "an agent's attempt to administer the safety system";
    }
}
