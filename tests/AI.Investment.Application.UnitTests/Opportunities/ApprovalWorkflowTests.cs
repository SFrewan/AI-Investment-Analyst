using AI.Investment.Application.Approvals;
using AI.Investment.Application.Execution;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Opportunities;

/// <summary>
/// Assembling what a person is shown, and turning their decision into a bound, expiring token.
/// </summary>
/// <remarks>
/// Building the request is deterministic service work rather than agent work: it is a structured
/// record assembled from an opportunity, and every figure in it was calculated somewhere that can
/// be re-run.
/// </remarks>
public sealed class ApprovalWorkflowTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_request_shows_the_risk_and_the_uncertainty_beside_the_money()
    {
        var (opportunity, proposal, workflow, _, _) = Build();

        var request = workflow.Present(opportunity, proposal);

        Assert.Equal(opportunity.OpportunityId, request.OpportunityId);
        Assert.Equal(proposal.ProposalId, request.ProposalId);
        Assert.Equal(opportunity.Title, request.Title);
        Assert.Equal(Capability.SimulatedExecution, request.Capability);
        Assert.Equal(1_000m, request.EstimatedExposure.Amount);
        Assert.Equal(RiskTier.Medium, request.RiskTier);
        Assert.Equal(0.7m, request.Confidence.Value);
        Assert.False(string.IsNullOrWhiteSpace(request.RiskSummary));
        Assert.Equal(1, request.EvidenceCount);
    }

    [Fact]
    public void A_request_cannot_be_built_for_an_opportunity_with_no_risk_assessment()
    {
        var opportunity = Draft();
        var order = Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Proposal(opportunity, order);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            ApprovalRequest.For(opportunity, proposal, Now));

        Assert.Equal("ApprovalRequest.RequiresRisk", error.Rule);
    }

    [Fact]
    public async Task Approving_issues_a_token_bound_to_the_exact_action()
    {
        var (opportunity, proposal, workflow, tokens, _) = Build();

        var request = workflow.Present(opportunity, proposal);

        var outcome = await workflow.ApproveAsync(
            request,
            proposal,
            "operator@example.test",
            proposal.Economics.EstimatedExposure);

        Assert.True(outcome.Issued);

        var token = outcome.Token!;

        Assert.Single(tokens.Tokens);
        Assert.Equal(proposal.ProposalId, token.ProposalId);
        Assert.Equal(opportunity.OpportunityId, token.OpportunityId);
        Assert.True(token.Fingerprint.Matches(proposal));
        Assert.Equal("operator@example.test", token.ApprovedBy);
        Assert.Equal(Now.Add(ApprovalWorkflow.DefaultValidity), token.ExpiresAtUtc);
    }

    [Fact]
    public async Task Approving_moves_the_opportunity_to_approved_and_records_the_token()
    {
        var (opportunity, proposal, workflow, _, repository) = Build();

        var request = workflow.Present(opportunity, proposal);

        var outcome = await workflow.ApproveAsync(
            request,
            proposal,
            "operator@example.test",
            proposal.Economics.EstimatedExposure);

        var token = outcome.Token!;
        var stored = await repository.GetAsync(opportunity.OpportunityId);

        Assert.NotNull(stored);
        Assert.Equal(OpportunityStatus.Approved, stored!.Status);
        Assert.Equal(token.ApprovalTokenId, stored.ApprovalTokenId);
    }

    [Fact]
    public async Task A_ceiling_above_the_figure_that_was_shown_is_refused()
    {
        var (opportunity, proposal, workflow, _, _) = Build();

        var request = workflow.Present(opportunity, proposal);

        var error = await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            workflow.ApproveAsync(
                request,
                proposal,
                "operator@example.test",
                Money.Create(5_000m, Currency.Usd)));

        Assert.Equal("ApprovalWorkflow.CeilingAboveWhatWasShown", error.Rule);
    }

    [Fact]
    public async Task An_action_that_changed_since_it_was_presented_cannot_be_approved()
    {
        var (opportunity, proposal, workflow, _, _) = Build();

        var request = workflow.Present(opportunity, proposal);

        var changed = Proposal(
            opportunity,
            Order(opportunity.OpportunityId, Guid.NewGuid(), quantity: 100m));

        var error = await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            workflow.ApproveAsync(
                request,
                changed,
                "operator@example.test",
                changed.Economics.EstimatedExposure));

        Assert.Equal("ApprovalWorkflow.ActionChanged", error.Rule);
    }

    [Fact]
    public async Task Revoking_an_unknown_token_is_refused_rather_than_ignored()
    {
        var (_, _, workflow, _, _) = Build();

        var error = await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            workflow.RevokeAsync(Guid.NewGuid(), "no such token"));

        Assert.Equal("ApprovalWorkflow.UnknownToken", error.Rule);
    }

    [Fact]
    public async Task A_revoked_token_stops_authorising_the_action()
    {
        var (opportunity, proposal, workflow, tokens, _) = Build();

        var request = workflow.Present(opportunity, proposal);

        var token = (await workflow.ApproveAsync(
            request,
            proposal,
            "operator@example.test",
            proposal.Economics.EstimatedExposure)).Token!;

        await workflow.RevokeAsync(token.ApprovalTokenId, "The market moved.");

        var refusal = await tokens.ConsumeAsync(
            token.ApprovalTokenId,
            opportunity.OpportunityId,
            proposal,
            Now);

        Assert.Equal(ApprovalRefusal.Revoked, refusal);
    }

    private static (
        Opportunity Opportunity,
        ActionProposal Proposal,
        ApprovalWorkflow Workflow,
        InMemoryApprovalTokenStore Tokens,
        InMemoryOpportunityRepository Repository) Build()
    {
        var repository = new InMemoryOpportunityRepository();
        var tokens = new InMemoryApprovalTokenStore();
        var clock = new FixedClock(Now);

        var opportunity = Draft();

        opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(opportunity, Now),
            OpportunityRisk.Create(
                "A single-name equity position carries issuer and market risk.",
                ReversibilityClass.ReversibleWithCost,
                [ClaimId.New()]),
            Confidence.Create(0.7m),
            Now);

        opportunity.Rank(OpportunityScore.From(Phase5Scores.Ratio(Now)), Now);
        opportunity.RecordProposal(Guid.NewGuid(), Now);

        repository.AddAsync(opportunity).GetAwaiter().GetResult();

        var proposal = Proposal(opportunity, Order(opportunity.OpportunityId, Guid.NewGuid()));

        var workflow = new ApprovalWorkflow(
            new StubActionGateway(),
            repository,
            tokens,
            new CountingUnitOfWork(),
            new FixedCorrelationContext(),
            clock);

        return (opportunity, proposal, workflow, tokens, repository);
    }

    private static Opportunity Draft() =>
        Opportunity.Draft(
            EquityOpportunity.Type,
            IngestionSubject.Create("Security", "AAPL"),
            OpportunitySource.Create("equity-screener", Now),
            "Buy 10 AAPL",
            "The screener found a gap between the entry price and the analyst target.",
            OpportunityDetail.Create(
                EquityOpportunity.Type,
                EquityDetail.ToJson("AAPL", 10m, 100m, 120m, "USD", 0.6m, 90)),
            Now,
            [ClaimId.New()]);

    private static VenueOrder Order(
        OpportunityId opportunityId,
        Guid approvalTokenId,
        decimal quantity = 10m) =>
        VenueOrder.Create(
            "AAPL",
            OrderSide.Buy,
            quantity,
            Money.Create(100m, Currency.Usd),
            opportunityId,
            approvalTokenId,
            Guid.NewGuid().ToString("n"));

    private static ActionProposal Proposal(Opportunity opportunity, VenueOrder order) =>
        SimulatedExecutionProposal.For(
            opportunity,
            order,
            ProposedBy.Service("opportunity-executor", "1.0"),
            CorrelationId.New(),
            Now);
}
