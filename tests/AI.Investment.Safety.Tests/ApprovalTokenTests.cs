using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// A human's permission to perform one exact action, once, before a stated time.
/// </summary>
/// <remarks>
/// Every test here corresponds to a known way approvals stop meaning anything: a token that can be
/// replayed, a token that never expires, a token that authorises a different action from the one
/// on the screen, and a token that authorises a larger amount than the approver saw.
/// </remarks>
public sealed class ApprovalTokenTests
{
    [Fact]
    public void A_token_authorises_exactly_the_action_it_was_issued_for()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Equal(
            ApprovalRefusal.None,
            token.Check(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));
    }

    [Fact]
    public void A_token_is_single_use()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now);

        Assert.True(token.IsConsumed);
        Assert.Equal(
            ApprovalRefusal.AlreadyConsumed,
            token.Check(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        Assert.Equal("ApprovalToken.AlreadyConsumed", error.Rule);
    }

    [Fact]
    public void A_token_expires()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        var token = Phase5Fixtures.Token(
            opportunity.OpportunityId,
            proposal,
            validFor: TimeSpan.FromHours(1));

        Assert.Equal(
            ApprovalRefusal.Expired,
            token.Check(opportunity.OpportunityId, proposal, Phase5Fixtures.Now.AddHours(1)));
    }

    [Fact]
    public void A_token_with_no_window_is_refused_at_issue()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        Assert.Throws<DomainValidationException>(() =>
            Phase5Fixtures.Token(opportunity.OpportunityId, proposal, validFor: TimeSpan.Zero));
    }

    [Fact]
    public void A_token_does_not_authorise_a_different_action_of_the_same_shape()
    {
        var opportunity = Phase5Fixtures.Approved();
        var approved = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid(), quantity: 10m);
        var approvedProposal = Phase5Fixtures.Proposal(opportunity, approved);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, approvedProposal);

        // Same capability, same type, same instrument - a larger order.
        var larger = Phase5Fixtures.Order(
            opportunity.OpportunityId,
            approved.ApprovalTokenId,
            quantity: 100m,
            idempotencyKey: approved.IdempotencyKey);

        var largerProposal = Phase5Fixtures.Proposal(opportunity, larger);

        Assert.NotEqual(
            ApprovalRefusal.None,
            token.Check(opportunity.OpportunityId, largerProposal, Phase5Fixtures.Now));
    }

    [Fact]
    public void A_token_does_not_authorise_a_proposal_it_was_not_issued_for()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var first = Phase5Fixtures.Proposal(opportunity, order);
        var second = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, first);

        Assert.Equal(
            ApprovalRefusal.WrongProposal,
            token.Check(opportunity.OpportunityId, second, Phase5Fixtures.Now));
    }

    [Fact]
    public void A_token_does_not_travel_to_another_opportunity()
    {
        var opportunity = Phase5Fixtures.Approved();
        var other = Phase5Fixtures.Approved(instrument: "MSFT");
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Equal(
            ApprovalRefusal.WrongOpportunity,
            token.Check(other.OpportunityId, proposal, Phase5Fixtures.Now));
    }

    [Fact]
    public void A_token_cannot_authorise_more_than_the_approver_saw()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid(), quantity: 10m, price: 100m);
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        var token = Phase5Fixtures.Token(
            opportunity.OpportunityId,
            proposal,
            maxAmount: Phase5Fixtures.Usd(999m));

        Assert.Equal(
            ApprovalRefusal.AmountExceeded,
            token.Check(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));
    }

    [Fact]
    public void A_ceiling_in_another_currency_cannot_be_compared_and_is_refused_at_issue()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        Assert.Throws<DomainValidationException>(() =>
            Phase5Fixtures.Token(
                opportunity.OpportunityId,
                proposal,
                maxAmount: Money.Create(10_000m, Currency.Create("EUR"))));
    }

    [Fact]
    public void An_approval_must_name_the_person_who_gave_it()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                proposal.Economics.EstimatedExposure,
                "   ",
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_revoked_token_authorises_nothing()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Revoke("The market moved.", Phase5Fixtures.Now);

        Assert.True(token.IsRevoked);
        Assert.Equal(
            ApprovalRefusal.Revoked,
            token.Check(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));
    }

    [Fact]
    public void A_consumed_token_cannot_be_revoked_afterwards()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Revoke("too late", Phase5Fixtures.Now));

        Assert.Equal("ApprovalToken.AlreadyConsumed", error.Rule);
    }

    [Fact]
    public void A_revocation_must_state_a_reason()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Throws<DomainValidationException>(() => token.Revoke("  ", Phase5Fixtures.Now));
    }

    [Fact]
    public void Consuming_an_unusable_token_throws_rather_than_returning_false()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal, validFor: TimeSpan.FromHours(1));

        Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now.AddHours(2)));

        Assert.False(token.IsConsumed);
    }

    [Fact]
    public void A_fingerprint_is_taken_over_every_field_that_changes_what_would_happen()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid(), quantity: 10m);
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        var repriced = Phase5Fixtures.Order(
            opportunity.OpportunityId,
            order.ApprovalTokenId,
            quantity: 10m,
            price: 101m,
            idempotencyKey: order.IdempotencyKey);

        var repricedProposal = Phase5Fixtures.Proposal(opportunity, repriced);

        Assert.NotEqual(
            ActionFingerprint.Of(proposal).Value,
            ActionFingerprint.Of(repricedProposal).Value);
    }

    [Fact]
    public void The_same_action_fingerprints_the_same_way_twice()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);

        Assert.Equal(ActionFingerprint.Of(proposal), ActionFingerprint.Of(proposal));
    }

    [Fact]
    public void A_malformed_fingerprint_cannot_be_compared_and_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => ActionFingerprint.Parse("not-a-digest"));
        Assert.Throws<DomainValidationException>(() => ActionFingerprint.Parse(new string('z', 64)));
    }

    [Fact]
    public void A_fingerprint_round_trips_through_its_stored_form()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var fingerprint = ActionFingerprint.Of(proposal);

        Assert.True(ActionFingerprint.Parse(fingerprint.Value).Matches(proposal));
    }

    [Fact]
    public void A_token_derives_its_usability_rather_than_storing_a_status()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var proposal = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Null(
            typeof(ApprovalToken).GetProperties().FirstOrDefault(property =>
                property.PropertyType.IsEnum &&
                property.Name.EndsWith("Status", StringComparison.Ordinal)));

        Assert.False(token.IsConsumed);
        Assert.False(token.IsRevoked);
        Assert.False(token.HasExpired(Phase5Fixtures.Now));
    }
}
