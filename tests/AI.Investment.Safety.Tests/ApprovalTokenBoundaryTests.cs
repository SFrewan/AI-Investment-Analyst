using System.Reflection;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The edges of an approval: its argument guards, its exact boundaries, and what it says when it
/// refuses.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApprovalTokenTests"/> covers the four properties that make an approval mean something.
/// This file covers the parts mutation testing found unpinned: the guards that reject a malformed
/// issue, the off-by-one edges of the two length limits, and every refusal explanation.
/// </para>
/// <para>
/// The explanations matter more here than almost anywhere else in the system. When a token refuses
/// an action at the last gate before an effect, the sentence it produces is what a human reads to
/// decide whether this was the control working or the system misbehaving - and "it has already been
/// used" and "its window has passed" call for entirely different responses.
/// </para>
/// </remarks>
public sealed class ApprovalTokenBoundaryTests
{
    // ---- Issue: argument guards ----------------------------------------------------------------

    [Fact]
    public void An_approval_cannot_be_issued_for_no_proposal()
    {
        var opportunity = Phase5Fixtures.Approved();

        Assert.Throws<ArgumentNullException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                null!,
                Phase5Fixtures.Usd(1_000m),
                "operator@example.test",
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));
    }

    [Fact]
    public void An_approval_cannot_be_issued_without_a_ceiling()
    {
        var (opportunity, proposal) = Issued();

        Assert.Throws<ArgumentNullException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                null!,
                "operator@example.test",
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// An approval window is arithmetic on a timestamp. A local one shifts the expiry by an offset
    /// that changes twice a year, which turns "valid for four hours" into something else entirely.
    /// </summary>
    [Fact]
    public void An_approval_must_be_issued_at_a_utc_time()
    {
        var (opportunity, proposal) = Issued();

        Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                proposal.Economics.EstimatedExposure,
                "operator@example.test",
                DateTime.SpecifyKind(Phase5Fixtures.Now, DateTimeKind.Local),
                TimeSpan.FromHours(1)));
    }

    // ---- Issue: refusals, and what they say ----------------------------------------------------

    [Fact]
    public void An_unattributed_approval_says_why_attribution_is_the_point()
    {
        var (opportunity, proposal) = Issued();

        var error = Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                proposal.Economics.EstimatedExposure,
                "   ",
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));

        Assert.Contains("name the person who gave it", error.Message, StringComparison.Ordinal);
        Assert.Contains("questioned afterwards", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact boundary, in both directions. A ceiling that rejects the value it names is a
    /// different rule from the one that was written down.
    /// </summary>
    [Fact]
    public void An_approver_identifier_of_exactly_the_maximum_length_is_accepted()
    {
        var (opportunity, proposal) = Issued();
        var approver = new string('a', ApprovalToken.MaxApproverLength);

        var token = ApprovalToken.Issue(
            opportunity.OpportunityId,
            proposal,
            proposal.Economics.EstimatedExposure,
            approver,
            Phase5Fixtures.Now,
            TimeSpan.FromHours(1));

        Assert.Equal(approver, token.ApprovedBy);
    }

    [Fact]
    public void An_approver_identifier_one_character_too_long_is_refused_by_its_limit()
    {
        var (opportunity, proposal) = Issued();

        var error = Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                proposal.Economics.EstimatedExposure,
                new string('a', ApprovalToken.MaxApproverLength + 1),
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));

        Assert.Contains("may not exceed", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            ApprovalToken.MaxApproverLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A negative ceiling would bind nothing: every exposure is greater than it, so the token would
    /// refuse every action it was issued for, which reads as a system fault rather than as the
    /// configuration error it is.
    /// </summary>
    [Fact]
    public void A_negative_ceiling_is_refused_at_issue()
    {
        var (opportunity, proposal) = Issued();

        var error = Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                Phase5Fixtures.Usd(-1m),
                "operator@example.test",
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));

        Assert.Contains("ceiling may not be negative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_standing_approval_is_refused_and_says_what_a_standing_approval_is_worth()
    {
        var (opportunity, proposal) = Issued();

        var error = Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                proposal.Economics.EstimatedExposure,
                "operator@example.test",
                Phase5Fixtures.Now,
                TimeSpan.Zero));

        Assert.Contains("An approval must expire", error.Message, StringComparison.Ordinal);
        Assert.Contains("standing permission", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "indistinguishable from no approval",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_ceiling_in_another_currency_names_both_currencies()
    {
        var (opportunity, proposal) = Issued();

        var error = Assert.Throws<DomainValidationException>(() =>
            ApprovalToken.Issue(
                opportunity.OpportunityId,
                proposal,
                Money.Create(10_000m, Currency.Create("EUR")),
                "operator@example.test",
                Phase5Fixtures.Now,
                TimeSpan.FromHours(1)));

        Assert.Contains("EUR", error.Message, StringComparison.Ordinal);
        Assert.Contains("USD", error.Message, StringComparison.Ordinal);
        Assert.Contains("would never bind", error.Message, StringComparison.Ordinal);
    }

    // ---- Check and Consume: argument guards ----------------------------------------------------

    [Fact]
    public void A_token_cannot_be_checked_against_nothing()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Throws<ArgumentNullException>(() =>
            token.Check(opportunity.OpportunityId, null!, Phase5Fixtures.Now));
    }

    /// <summary>
    /// Expiry is a comparison against the clock. Comparing against a local timestamp can make a
    /// lapsed approval look current, which is the one direction that must never happen.
    /// </summary>
    [Fact]
    public void A_token_cannot_be_consumed_at_a_non_utc_time()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Throws<DomainValidationException>(() =>
            token.Consume(
                opportunity.OpportunityId,
                proposal,
                DateTime.SpecifyKind(Phase5Fixtures.Now, DateTimeKind.Local)));

        Assert.False(token.IsConsumed);
    }

    [Fact]
    public void A_token_cannot_be_revoked_at_a_non_utc_time()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        Assert.Throws<DomainValidationException>(() =>
            token.Revoke("no longer wanted", DateTime.SpecifyKind(Phase5Fixtures.Now, DateTimeKind.Local)));

        Assert.False(token.IsRevoked);
    }

    // ---- Revocation ---------------------------------------------------------------------------

    [Fact]
    public void A_consumed_approval_cannot_be_revoked_and_says_why_the_record_would_be_wrong()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Revoke("too late", Phase5Fixtures.Now));

        Assert.Contains("consumed approval cannot be revoked", error.Message, StringComparison.Ordinal);
        Assert.Contains("make the record wrong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revocation_without_a_reason_says_a_reason_is_required()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        var error = Assert.Throws<DomainValidationException>(() => token.Revoke("   ", Phase5Fixtures.Now));

        Assert.Contains("must state a reason", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revocation_reason_of_exactly_the_maximum_length_is_kept_whole()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);
        var reason = new string('r', ApprovalToken.MaxReasonLength);

        token.Revoke(reason, Phase5Fixtures.Now);

        Assert.Equal(reason, token.RevocationReason);
    }

    /// <summary>
    /// A longer reason is truncated rather than refused: a revocation that fails because somebody
    /// wrote too much would leave a token live, and the token being live is the dangerous state.
    /// </summary>
    [Fact]
    public void A_longer_revocation_reason_is_truncated_rather_than_refused()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Revoke(new string('r', ApprovalToken.MaxReasonLength + 50), Phase5Fixtures.Now);

        Assert.True(token.IsRevoked);
        Assert.Equal(ApprovalToken.MaxReasonLength, token.RevocationReason!.Length);
    }

    // ---- What a refusal says --------------------------------------------------------------------

    [Fact]
    public void A_replayed_approval_says_that_approvals_are_single_use()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        Assert.Contains(
            token.ApprovalTokenId.ToString(),
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains("cannot authorise this action", error.Message, StringComparison.Ordinal);
        Assert.Contains("already been used", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_revoked_approval_says_it_was_withdrawn_before_use()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        token.Revoke("the market moved", Phase5Fixtures.Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        Assert.Contains("revoked before it could be used", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lapsed_approval_says_its_window_has_passed()
    {
        var (opportunity, proposal) = Issued();

        var token = Phase5Fixtures.Token(
            opportunity.OpportunityId,
            proposal,
            validFor: TimeSpan.FromHours(1));

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now.AddHours(2)));

        Assert.Contains("window has passed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approval_for_more_than_the_approver_saw_says_so()
    {
        var (opportunity, proposal) = Issued();

        var token = Phase5Fixtures.Token(
            opportunity.OpportunityId,
            proposal,
            maxAmount: Phase5Fixtures.Usd(1m));

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        Assert.Contains("more than the approver saw", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approval_presented_for_another_opportunity_says_so()
    {
        var (opportunity, proposal) = Issued();
        var other = Phase5Fixtures.Approved(instrument: "MSFT");
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(other.OpportunityId, proposal, Phase5Fixtures.Now));

        Assert.Contains("different opportunity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approval_presented_for_another_proposal_says_so()
    {
        var (opportunity, proposal) = Issued();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());
        var second = Phase5Fixtures.Proposal(opportunity, order);
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, second, Phase5Fixtures.Now));

        Assert.Contains("different proposal", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tampered-row case: identifiers that line up with a fingerprint that does not.
    /// </summary>
    /// <remarks>
    /// A token is stored, so the fingerprint column can differ from the action it names without any
    /// caller having done anything wrong - a bad migration, a restored backup, or an edit against
    /// the database. Reaching that branch through the public surface is impossible, because a
    /// different action also has a different proposal identifier and the proposal check fires first.
    /// Setting the stored fingerprint directly is therefore the only way to prove the last identity
    /// check actually holds, and it is the check that stops an approval being reused for a larger
    /// action assembled afterwards.
    /// </remarks>
    [Fact]
    public void An_approval_whose_stored_fingerprint_does_not_match_refuses_even_when_the_identifiers_line_up()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        var elsewhere = Phase5Fixtures.Proposal(
            opportunity,
            Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid(), quantity: 999m));

        Store(token, ActionFingerprint.Of(elsewhere));

        Assert.Equal(
            ApprovalRefusal.FingerprintMismatch,
            token.Check(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            token.Consume(opportunity.OpportunityId, proposal, Phase5Fixtures.Now));

        Assert.Contains(
            "not the action that was approved",
            error.Message,
            StringComparison.Ordinal);
    }

    // ---- Description and materialisation ---------------------------------------------------------

    [Fact]
    public void An_approval_describes_itself_for_a_human_reading_a_log()
    {
        var (opportunity, proposal) = Issued();
        var token = Phase5Fixtures.Token(opportunity.OpportunityId, proposal);

        var described = token.ToString();

        Assert.Contains(token.ApprovalTokenId.ToString(), described, StringComparison.Ordinal);
        Assert.Contains("operator@example.test", described, StringComparison.Ordinal);
        Assert.Contains("expires", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The persistence constructor must leave no non-nullable string null.
    /// </summary>
    /// <remarks>
    /// It exists only for the provider to materialise a row into, and every property it does not set
    /// is overwritten immediately afterwards - but between construction and materialisation the
    /// object is real, and a null in a non-nullable string is the kind of thing that surfaces much
    /// later as a null reference in something that had every right to trust the type.
    /// </remarks>
    [Fact]
    public void The_persistence_constructor_leaves_no_null_string()
    {
        var constructor = typeof(ApprovalToken).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        Assert.NotNull(constructor);

        var token = (ApprovalToken)constructor!.Invoke(null);

        Assert.Equal(string.Empty, token.ApprovedBy);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static (Opportunity Opportunity, ActionProposal Proposal) Issued()
    {
        var opportunity = Phase5Fixtures.Approved();
        var order = Phase5Fixtures.Order(opportunity.OpportunityId, Guid.NewGuid());

        return (opportunity, Phase5Fixtures.Proposal(opportunity, order));
    }

    private static void Store(ApprovalToken token, ActionFingerprint fingerprint)
    {
        var setter = typeof(ApprovalToken)
            .GetProperty(nameof(ApprovalToken.Fingerprint))!
            .GetSetMethod(nonPublic: true)!;

        setter.Invoke(token, [fingerprint]);
    }
}
