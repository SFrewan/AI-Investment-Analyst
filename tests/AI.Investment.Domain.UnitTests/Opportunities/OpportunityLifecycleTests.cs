using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The opportunity lifecycle: what may happen, in what order, and what may never happen at all.
/// </summary>
/// <remarks>
/// The state machine is the only thing standing between "a candidate somebody thought about" and
/// "an action the system is willing to propose", so every refusal below is asserted by the reason
/// it carries rather than only by the fact that it threw.
/// </remarks>
public sealed class OpportunityLifecycleTests
{
    [Fact]
    public void A_new_opportunity_is_a_draft_and_has_committed_to_nothing()
    {
        var opportunity = OpportunityFixtures.Draft();

        Assert.Equal(OpportunityStatus.Draft, opportunity.Status);
        Assert.Null(opportunity.Economics);
        Assert.Null(opportunity.Risk);
        Assert.Null(opportunity.Confidence);
        Assert.Null(opportunity.Score);
        Assert.Null(opportunity.ApprovalTokenId);
        Assert.Null(opportunity.ExecutionId);
        Assert.Empty(opportunity.ProposalIds);
        Assert.False(opportunity.IsTerminal);
    }

    [Fact]
    public void A_detail_payload_for_another_type_is_refused()
    {
        var other = OpportunityType.Create("supplier-deal");
        var detail = OpportunityDetail.Empty(other);

        var error = Assert.Throws<DomainValidationException>(() =>
            Opportunity.Draft(
                EquityOpportunity.Type,
                OpportunityFixtures.Subject(),
                OpportunityFixtures.Source(),
                "Mismatched",
                "The payload was validated against a schema for a different type.",
                detail,
                OpportunityFixtures.Now));

        Assert.Contains("supplier-deal", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_opportunity_with_no_evidence_cannot_be_evaluated()
    {
        var opportunity = OpportunityFixtures.Draft(evidence: []);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.Evaluate(
                OpportunityFixtures.Economics(),
                OpportunityFixtures.Risk(),
                OpportunityFixtures.ConfidenceOf(),
                OpportunityFixtures.Now));

        Assert.Equal("Opportunity.EvaluationCitesEvidence", error.Rule);
        Assert.Equal(OpportunityStatus.Draft, opportunity.Status);
    }

    [Fact]
    public void Evaluation_records_economics_risk_and_confidence_together()
    {
        var opportunity = OpportunityFixtures.Draft();

        opportunity.Evaluate(
            OpportunityFixtures.Economics(),
            OpportunityFixtures.Risk(),
            OpportunityFixtures.ConfidenceOf(0.65m),
            OpportunityFixtures.Now);

        Assert.Equal(OpportunityStatus.Evaluated, opportunity.Status);
        Assert.NotNull(opportunity.Economics);
        Assert.NotNull(opportunity.Risk);
        Assert.Equal(0.65m, opportunity.Confidence!.Value);
    }

    [Fact]
    public void Evidence_is_frozen_once_the_opportunity_has_been_evaluated()
    {
        var opportunity = OpportunityFixtures.Draft();

        opportunity.Evaluate(
            OpportunityFixtures.Economics(),
            OpportunityFixtures.Risk(),
            OpportunityFixtures.ConfidenceOf(),
            OpportunityFixtures.Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.AddEvidence(ClaimId.New()));

        Assert.Equal("Opportunity.WrongStatus", error.Rule);
        Assert.Single(opportunity.Evidence);
    }

    [Fact]
    public void The_same_claim_is_not_recorded_as_evidence_twice()
    {
        var claim = ClaimId.New();
        var opportunity = OpportunityFixtures.Draft(evidence: [claim]);

        opportunity.AddEvidence(claim);

        Assert.Single(opportunity.Evidence);
    }

    [Fact]
    public void An_unevaluated_opportunity_cannot_be_ranked()
    {
        var opportunity = OpportunityFixtures.Draft();

        Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.Rank(OpportunityFixtures.Score(), OpportunityFixtures.Now));
    }

    [Fact]
    public void An_unranked_opportunity_cannot_have_an_action_proposed_for_it()
    {
        var opportunity = OpportunityFixtures.Draft();

        opportunity.Evaluate(
            OpportunityFixtures.Economics(),
            OpportunityFixtures.Risk(),
            OpportunityFixtures.ConfidenceOf(),
            OpportunityFixtures.Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.RecordProposal(Guid.NewGuid(), OpportunityFixtures.Now));

        Assert.Equal("Opportunity.ProposalRequiresRanking", error.Rule);
    }

    [Fact]
    public void A_second_proposal_is_recorded_without_moving_the_status_again()
    {
        var opportunity = OpportunityFixtures.Draft();
        var at = OpportunityFixtures.Now;

        opportunity.Evaluate(
            OpportunityFixtures.Economics(),
            OpportunityFixtures.Risk(),
            OpportunityFixtures.ConfidenceOf(),
            at);
        opportunity.Rank(OpportunityFixtures.Score(), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);

        Assert.Equal(OpportunityStatus.Proposed, opportunity.Status);
        Assert.Equal(2, opportunity.ProposalIds.Count);
    }

    [Fact]
    public void An_approval_must_name_the_token_that_granted_it()
    {
        var opportunity = OpportunityFixtures.Draft();
        var at = OpportunityFixtures.Now;

        opportunity.Evaluate(
            OpportunityFixtures.Economics(),
            OpportunityFixtures.Risk(),
            OpportunityFixtures.ConfidenceOf(),
            at);
        opportunity.Rank(OpportunityFixtures.Score(), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);

        Assert.Throws<DomainValidationException>(() => opportunity.Approve(Guid.Empty, at));
    }

    [Fact]
    public void An_unapproved_opportunity_cannot_begin_executing()
    {
        var opportunity = OpportunityFixtures.Draft();
        var at = OpportunityFixtures.Now;

        opportunity.Evaluate(
            OpportunityFixtures.Economics(),
            OpportunityFixtures.Risk(),
            OpportunityFixtures.ConfidenceOf(),
            at);
        opportunity.Rank(OpportunityFixtures.Score(), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);

        Assert.Throws<DomainRuleViolationException>(() => opportunity.BeginExecution(at));
    }

    [Fact]
    public void The_full_path_reaches_Active_and_records_what_authorised_it()
    {
        var token = Guid.NewGuid();
        var execution = Guid.NewGuid();
        var at = OpportunityFixtures.Now;

        var opportunity = OpportunityFixtures.Approved(at, token);

        opportunity.BeginExecution(at);
        opportunity.Activate(execution, at);

        Assert.Equal(OpportunityStatus.Active, opportunity.Status);
        Assert.Equal(token, opportunity.ApprovalTokenId);
        Assert.Equal(execution, opportunity.ExecutionId);
    }

    [Fact]
    public void A_closed_opportunity_states_its_outcome_and_accepts_nothing_further()
    {
        var at = OpportunityFixtures.Now;
        var opportunity = OpportunityFixtures.Approved(at);

        opportunity.BeginExecution(at);
        opportunity.Activate(Guid.NewGuid(), at);
        opportunity.Close("Sold at the target price.", at);

        Assert.Equal(OpportunityStatus.Closed, opportunity.Status);
        Assert.True(opportunity.IsTerminal);
        Assert.Equal("Sold at the target price.", opportunity.Resolution);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.Reject("changed my mind", at));

        Assert.Equal("Opportunity.Terminal", error.Rule);
    }

    [Fact]
    public void An_opportunity_that_was_never_acted_on_cannot_be_closed()
    {
        var opportunity = OpportunityFixtures.Draft();

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.Close("done", OpportunityFixtures.Now));

        Assert.Equal("Opportunity.CloseRequiresExecution", error.Rule);
    }

    [Fact]
    public void An_active_opportunity_cannot_expire_because_it_has_already_been_acted_on()
    {
        var at = OpportunityFixtures.Now;
        var opportunity = OpportunityFixtures.Approved(at);

        opportunity.BeginExecution(at);
        opportunity.Activate(Guid.NewGuid(), at);

        var error = Assert.Throws<DomainRuleViolationException>(() => opportunity.Expire(at));

        Assert.Equal("Opportunity.ActiveCannotExpire", error.Rule);
    }

    [Fact]
    public void Expiry_and_rejection_are_distinguishable_afterwards()
    {
        var at = OpportunityFixtures.Now;

        var expired = OpportunityFixtures.Draft(at);
        expired.Expire(at);

        var rejected = OpportunityFixtures.Draft(at);
        rejected.Reject("The thesis did not survive review.", at);

        Assert.Equal(OpportunityStatus.Expired, expired.Status);
        Assert.Equal(OpportunityStatus.Rejected, rejected.Status);
        Assert.NotEqual(expired.Resolution, rejected.Resolution);
    }

    [Fact]
    public void A_status_change_cannot_move_backwards_in_time()
    {
        var at = OpportunityFixtures.Now;
        var opportunity = OpportunityFixtures.Draft(at);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            opportunity.Evaluate(
                OpportunityFixtures.Economics(),
                OpportunityFixtures.Risk(),
                OpportunityFixtures.ConfidenceOf(),
                at.AddSeconds(-1)));

        Assert.Equal("Opportunity.TimeMovesForward", error.Rule);
    }

    [Fact]
    public void A_non_utc_timestamp_is_refused()
    {
        var opportunity = OpportunityFixtures.Draft();

        Assert.Throws<DomainValidationException>(() =>
            opportunity.Evaluate(
                OpportunityFixtures.Economics(),
                OpportunityFixtures.Risk(),
                OpportunityFixtures.ConfidenceOf(),
                new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void A_risk_assessment_must_cite_evidence()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            OpportunityRisk.Create("Something could go wrong.", ReversibilityClass.Reversible, []));

        Assert.Equal("OpportunityRisk.CitesEvidence", error.Rule);
    }

    [Fact]
    public void A_risk_assessment_must_say_something()
    {
        Assert.Throws<DomainValidationException>(() =>
            OpportunityRisk.Create("   ", ReversibilityClass.Reversible, [ClaimId.New()]));
    }

    [Fact]
    public void A_score_must_be_a_dimensionless_ratio()
    {
        var moneyScore = MoneyScoreResult();

        Assert.Throws<DomainValidationException>(() => OpportunityScore.From(moneyScore));
    }

    private static MetricResult MoneyScoreResult()
    {
        var at = OpportunityFixtures.Now;
        var published = at.AddDays(-1);

        var provenance = Provenance.Create(
            SourceId.Create("scoring-engine"),
            published,
            published,
            at);

        var input = CalculationInput.Create(
            "value",
            Claims.Fact(12m, provenance),
            UnitOfMeasure.Money);

        var context = CalculationContext.Create(
            OpportunityFixtures.Subject(),
            KnowledgeCutoff.At(at),
            at);

        return MetricResult.Create(
            context,
            MetricId.Create("opportunity.value"),
            MetricValue.Money(12m, Currency.Usd),
            "a figure with units",
            SourceId.Create("scoring-engine"),
            CalculationVersion.Create(1, 0),
            published,
            [input]);
    }
}
