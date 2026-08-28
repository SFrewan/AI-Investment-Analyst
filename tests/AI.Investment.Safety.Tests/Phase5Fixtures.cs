using AI.Investment.Application.Execution;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// Builders for the opportunity, approval, capital and execution safety tests.
/// </summary>
/// <remarks>
/// Everything here produces a valid, permitted object, so each test breaks exactly one thing and
/// the name of the test says which. A fixture that quietly produced an already-refused object
/// would make a safety test pass for the wrong reason, which is the failure mode these tests
/// exist to catch elsewhere.
/// </remarks>
internal static class Phase5Fixtures
{
    internal static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    internal static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    internal static IngestionSubject Subject(string ticker = "AAPL") =>
        IngestionSubject.Create("Security", ticker);

    internal static OpportunityDetail Detail(
        string instrument = "AAPL",
        decimal quantity = 10m,
        decimal entryPrice = 100m,
        decimal targetPrice = 120m,
        decimal successProbability = 0.6m,
        int horizonDays = 90) =>
        OpportunityDetail.Create(
            EquityOpportunity.Type,
            EquityDetail.ToJson(
                instrument,
                quantity,
                entryPrice,
                targetPrice,
                "USD",
                successProbability,
                horizonDays));

    internal static Opportunity Draft(DateTime? nowUtc = null, string instrument = "AAPL")
    {
        var at = nowUtc ?? Now;

        return Opportunity.Draft(
            EquityOpportunity.Type,
            Subject(instrument),
            OpportunitySource.Create("equity-screener", at),
            "Buy 10 " + instrument,
            "The screener found a gap between the entry price and the analyst target.",
            Detail(instrument),
            at,
            [ClaimId.New()]);
    }

    /// <summary>An opportunity carried all the way to Approved, the state execution starts from.</summary>
    internal static Opportunity Approved(
        DateTime? nowUtc = null,
        Guid? approvalTokenId = null,
        string instrument = "AAPL")
    {
        var at = nowUtc ?? Now;
        var opportunity = Draft(at, instrument);

        opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(opportunity, at),
            OpportunityRisk.Create(
                "A single-name equity position carries issuer and market risk.",
                ReversibilityClass.ReversibleWithCost,
                [ClaimId.New()]),
            Confidence.Create(0.7m),
            at);

        opportunity.Rank(Score(at), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);
        opportunity.Approve(approvalTokenId ?? Guid.NewGuid(), at);

        return opportunity;
    }

    internal static OpportunityScore Score(DateTime? nowUtc = null, decimal value = 0.82m)
    {
        var at = nowUtc ?? Now;
        var published = at.AddDays(-1);

        var provenance = Provenance.Create(
            SourceId.Create("scoring-engine"),
            published,
            published,
            at);

        var context = CalculationContext.Create(Subject(), KnowledgeCutoff.At(at), at);

        return OpportunityScore.From(MetricResult.Create(
            context,
            MetricId.Create("opportunity.composite-score"),
            MetricValue.Ratio(value),
            "the shipped scoring specification",
            SourceId.Create("scoring-engine"),
            CalculationVersion.Create(1, 0),
            published,
            [CalculationInput.Create("financial-health", Claims.Fact(value, provenance), UnitOfMeasure.Ratio)]));
    }

    internal static VenueOrder Order(
        OpportunityId opportunityId,
        Guid approvalTokenId,
        string instrument = "AAPL",
        OrderSide side = OrderSide.Buy,
        decimal quantity = 10m,
        decimal price = 100m,
        string? idempotencyKey = null) =>
        VenueOrder.Create(
            instrument,
            side,
            quantity,
            Usd(price),
            opportunityId,
            approvalTokenId,
            idempotencyKey ?? Guid.NewGuid().ToString("n"));

    internal static ActionProposal Proposal(Opportunity opportunity, VenueOrder order, DateTime? nowUtc = null) =>
        SimulatedExecutionProposal.For(
            opportunity,
            order,
            ProposedBy.Service("opportunity-executor", "1.0"),
            CorrelationId.New(),
            nowUtc ?? Now);

    internal static ApprovalToken Token(
        OpportunityId opportunityId,
        ActionProposal proposal,
        DateTime? nowUtc = null,
        Money? maxAmount = null,
        TimeSpan? validFor = null) =>
        ApprovalToken.Issue(
            opportunityId,
            proposal,
            maxAmount ?? proposal.Economics.EstimatedExposure,
            "operator@example.test",
            nowUtc ?? Now,
            validFor ?? TimeSpan.FromHours(4));

    /// <summary>A policy context that permits simulated execution at the tier it computes to.</summary>
    internal static PolicyContext PermissiveContext() =>
        PolicyContext.Create(
            "Test",
            KillSwitchState.Disengaged,
            [
                CapabilityPolicy.Create(Capability.SimulatedExecution, enabled: true, RiskTier.Medium),
                CapabilityPolicy.Create(Capability.OpportunityManagement, enabled: true, RiskTier.Medium),
            ]);

    /// <summary>
    /// One opportunity carried to the point where execution is the next step, with the proposal,
    /// the token issued against it and the order that names that token.
    /// </summary>
    /// <remarks>
    /// The order is rebuilt after the token exists because an order must name the approval that
    /// permitted it, and the approval cannot exist until there is a proposal to bind it to. The
    /// proposal is the one the token was issued for - the same object, not an equal one - because a
    /// token is bound to a proposal's identity and not merely to its shape.
    /// </remarks>
    internal sealed record Scenario(
        Opportunity Opportunity,
        ActionProposal Proposal,
        ApprovalToken Token,
        VenueOrder Order,
        ExecutionRequest Request);

    internal static Scenario Build(
        DateTime? nowUtc = null,
        string instrument = "AAPL",
        decimal quantity = 10m,
        decimal price = 100m,
        Money? maxAmount = null,
        TimeSpan? validFor = null,
        Money? costBasis = null,
        OrderSide side = OrderSide.Buy)
    {
        var at = nowUtc ?? Now;
        var idempotencyKey = Guid.NewGuid().ToString("n");

        var opportunity = Draft(at, instrument);

        opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(opportunity, at),
            OpportunityRisk.Create(
                "A single-name equity position carries issuer and market risk.",
                ReversibilityClass.ReversibleWithCost,
                [ClaimId.New()]),
            Confidence.Create(0.7m),
            at);

        opportunity.Rank(Score(at), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);

        var draftOrder = Order(
            opportunity.OpportunityId,
            Guid.NewGuid(),
            instrument,
            side,
            quantity,
            price,
            idempotencyKey);

        var proposal = Proposal(opportunity, draftOrder, at);

        var token = ApprovalToken.Issue(
            opportunity.OpportunityId,
            proposal,
            maxAmount ?? proposal.Economics.EstimatedExposure,
            "operator@example.test",
            at,
            validFor ?? TimeSpan.FromHours(4));

        opportunity.Approve(token.ApprovalTokenId, at);

        var order = Order(
            opportunity.OpportunityId,
            token.ApprovalTokenId,
            instrument,
            side,
            quantity,
            price,
            idempotencyKey);

        var request = ExecutionRequest.Create(
            opportunity,
            proposal,
            token.ApprovalTokenId,
            order,
            costBasis);

        return new Scenario(opportunity, proposal, token, order, request);
    }

    /// <summary>
    /// An opportunity carried to <see cref="OpportunityStatus.Proposed"/> with the proposal that
    /// was made for it - the state a human is asked to decide on.
    /// </summary>
    internal static (Opportunity Opportunity, ActionProposal Proposal) Pending(
        DateTime? nowUtc = null,
        string instrument = "AAPL")
    {
        var at = nowUtc ?? Now;
        var opportunity = Draft(at, instrument);

        opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(opportunity, at),
            OpportunityRisk.Create(
                "A single-name equity position carries issuer and market risk.",
                ReversibilityClass.ReversibleWithCost,
                [ClaimId.New()]),
            Confidence.Create(0.7m),
            at);

        opportunity.Rank(Score(at), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);

        var proposal = Proposal(
            opportunity,
            Order(opportunity.OpportunityId, Guid.NewGuid(), instrument),
            at);

        return (opportunity, proposal);
    }
}
