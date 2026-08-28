using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.UnitTests.Fakes;
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
/// Evaluate, rank, propose, reject and expire: the orchestration around the aggregate.
/// </summary>
/// <remarks>
/// The workflow refuses an opportunity type nobody registered a calculator or an evidence
/// requirement for. That direction is the point: an unregistered type is unusable rather than
/// being the one type nothing checks.
/// </remarks>
public sealed class OpportunityWorkflowTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_complete_candidate_is_evaluated_and_stored()
    {
        var repository = new InMemoryOpportunityRepository();
        var workflow = Build(repository);
        var opportunity = Draft();

        var missing = await workflow.EvaluateAsync(opportunity, Risk(), Confidence.Create(0.7m));

        Assert.Empty(missing);
        Assert.Equal(OpportunityStatus.Evaluated, opportunity.Status);
        Assert.NotNull(opportunity.Economics);
        Assert.Equal(1, repository.Saves);
    }

    [Fact]
    public async Task An_incomplete_candidate_is_reported_and_left_a_draft()
    {
        var repository = new InMemoryOpportunityRepository();
        var workflow = Build(repository);
        var opportunity = Draft(evidence: []);

        var missing = await workflow.EvaluateAsync(opportunity, Risk(), Confidence.Create(0.7m));

        Assert.NotEmpty(missing);
        Assert.Equal(OpportunityStatus.Draft, opportunity.Status);
        Assert.Equal(0, repository.Saves);
    }

    [Fact]
    public async Task A_type_with_no_registered_evidence_requirement_is_refused()
    {
        var workflow = new OpportunityWorkflow(
            new InMemoryOpportunityRepository(),
            [new EquityEconomicsCalculator()],
            [],
            new FixedClock(Now));

        var error = await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            workflow.EvaluateAsync(Draft(), Risk(), Confidence.Create(0.7m)));

        Assert.Equal("OpportunityWorkflow.NoEvidenceRequirement", error.Rule);
    }

    [Fact]
    public async Task A_type_with_no_registered_calculator_is_refused()
    {
        var workflow = new OpportunityWorkflow(
            new InMemoryOpportunityRepository(),
            [],
            [new EquityEvidenceRequirement()],
            new FixedClock(Now));

        var error = await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            workflow.EvaluateAsync(Draft(), Risk(), Confidence.Create(0.7m)));

        Assert.Equal("OpportunityWorkflow.NoCalculator", error.Rule);
    }

    [Fact]
    public async Task Ranking_records_the_score_and_stores_the_opportunity()
    {
        var repository = new InMemoryOpportunityRepository();
        var workflow = Build(repository);
        var opportunity = Draft();

        await workflow.EvaluateAsync(opportunity, Risk(), Confidence.Create(0.7m));
        await workflow.RankAsync(opportunity, Phase5Scores.Ratio(Now));

        Assert.Equal(OpportunityStatus.Ranked, opportunity.Status);
        Assert.NotNull(opportunity.Score);
        Assert.Equal(2, repository.Saves);
    }

    [Fact]
    public async Task Recording_a_proposal_moves_a_ranked_opportunity_to_proposed()
    {
        var repository = new InMemoryOpportunityRepository();
        var workflow = Build(repository);
        var opportunity = Draft();

        await workflow.EvaluateAsync(opportunity, Risk(), Confidence.Create(0.7m));
        await workflow.RankAsync(opportunity, Phase5Scores.Ratio(Now));
        await workflow.RecordProposalAsync(opportunity, Guid.NewGuid());

        Assert.Equal(OpportunityStatus.Proposed, opportunity.Status);
    }

    [Fact]
    public async Task Rejecting_states_the_reason_and_stores_it()
    {
        var repository = new InMemoryOpportunityRepository();
        var workflow = Build(repository);
        var opportunity = Draft();

        await workflow.RejectAsync(opportunity, "The thesis did not survive review.");

        Assert.Equal(OpportunityStatus.Rejected, opportunity.Status);
        Assert.Equal("The thesis did not survive review.", opportunity.Resolution);
    }

    [Fact]
    public async Task An_opportunity_past_its_horizon_expires_and_one_inside_it_does_not()
    {
        var repository = new InMemoryOpportunityRepository();
        var clock = new MutableClock(Now);

        var workflow = new OpportunityWorkflow(
            repository,
            [new EquityEconomicsCalculator()],
            [new EquityEvidenceRequirement()],
            clock);

        var overdue = Draft(horizonDays: 1);
        var current = Draft(horizonDays: 365);

        await workflow.EvaluateAsync(overdue, Risk(), Confidence.Create(0.7m));
        await workflow.EvaluateAsync(current, Risk(), Confidence.Create(0.7m));

        clock.UtcNow = Now.AddDays(2);

        var expired = await workflow.ExpireOverdueAsync();

        Assert.Equal(1, expired);
        Assert.Equal(OpportunityStatus.Expired, overdue.Status);
        Assert.Equal(OpportunityStatus.Evaluated, current.Status);
    }

    private static OpportunityWorkflow Build(InMemoryOpportunityRepository repository) =>
        new(
            repository,
            [new EquityEconomicsCalculator()],
            [new EquityEvidenceRequirement()],
            new FixedClock(Now));

    private static Opportunity Draft(IEnumerable<ClaimId>? evidence = null, int horizonDays = 90) =>
        Opportunity.Draft(
            EquityOpportunity.Type,
            IngestionSubject.Create("Security", "AAPL"),
            OpportunitySource.Create("equity-screener", Now),
            "Buy 10 AAPL",
            "The screener found a gap between the entry price and the analyst target.",
            OpportunityDetail.Create(
                EquityOpportunity.Type,
                EquityDetail.ToJson("AAPL", 10m, 100m, 120m, "USD", 0.6m, horizonDays)),
            Now,
            evidence ?? [ClaimId.New()]);

    private static OpportunityRisk Risk() =>
        OpportunityRisk.Create(
            "A single-name equity position carries issuer and market risk.",
            ReversibilityClass.ReversibleWithCost,
            [ClaimId.New()]);

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; set; }
    }
}
