using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Retention;

public sealed class RetentionLimitTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// "No obligation" and "obligation not established" must not look alike, so the absence of a
    /// cap is modelled rather than left as a null TimeSpan.
    /// </summary>
    [Fact]
    public void Unlimited_is_unbounded_and_never_exceeded()
    {
        Assert.False(RetentionLimit.Unlimited.IsBounded);
        Assert.Null(RetentionLimit.Unlimited.MaximumAge);
        Assert.False(RetentionLimit.Unlimited.IsExceededBy(Now.AddYears(-50), Now));
    }

    [Fact]
    public void A_bounded_limit_is_exceeded_only_after_its_maximum_age()
    {
        var limit = RetentionLimit.OfDays(30);

        Assert.False(limit.IsExceededBy(Now.AddDays(-29), Now));
        Assert.False(limit.IsExceededBy(Now.AddDays(-30), Now));
        Assert.True(limit.IsExceededBy(Now.AddDays(-31), Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_limit_is_rejected(int days) =>
        Assert.Throws<DomainValidationException>(() => RetentionLimit.Of(TimeSpan.FromDays(days)));
}

public sealed class LicensingRetentionTests
{
    /// <summary>
    /// Open data carries no retention obligation. That is a fact about the licence, and stating it
    /// explicitly is what stops a global default from being invented later.
    /// </summary>
    [Fact]
    public void Open_data_has_no_retention_limit() =>
        Assert.False(LicensingTerms.OpenData().Retention.IsBounded);

    [Fact]
    public void A_licence_can_declare_a_retention_limit()
    {
        var terms = LicensingTerms.Create(
            storageAllowed: true,
            redistributionAllowed: false,
            automatedProcessingAllowed: true,
            attributionRequired: true,
            notes: "12-month retention clause",
            retention: RetentionLimit.OfDays(365));

        Assert.True(terms.Retention.IsBounded);
        Assert.Equal(TimeSpan.FromDays(365), terms.Retention.MaximumAge);
    }

    /// <summary>
    /// Unestablished terms permit no ingestion at all, so nothing is ever stored under a licence
    /// nobody has read - which is why an unbounded default here is safe rather than lax.
    /// </summary>
    [Fact]
    public void Unknown_terms_permit_nothing_so_their_retention_is_moot()
    {
        Assert.False(LicensingTerms.Unknown.StorageAllowed);
        Assert.False(LicensingTerms.Unknown.Retention.IsBounded);
    }
}

public sealed class RetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private static LicensingTerms Capped(int days) =>
        LicensingTerms.Create(
            storageAllowed: true,
            redistributionAllowed: false,
            automatedProcessingAllowed: true,
            attributionRequired: true,
            retention: RetentionLimit.OfDays(days));

    [Fact]
    public void A_source_with_no_cap_retains_indefinitely()
    {
        var decision = RetentionPolicy.Evaluate(
            LicensingTerms.OpenData(),
            Now.AddYears(-20),
            Now,
            isReferencedByEvidence: false);

        Assert.Equal(RetentionOutcome.Retain, decision.Outcome);
        Assert.Equal(RetentionPolicy.NoLicensedLimitRule, decision.RuleId);
    }

    [Fact]
    public void A_payload_inside_its_licensed_limit_is_retained()
    {
        var decision = RetentionPolicy.Evaluate(
            Capped(365),
            Now.AddDays(-100),
            Now,
            isReferencedByEvidence: false);

        Assert.Equal(RetentionOutcome.Retain, decision.Outcome);
        Assert.Equal(RetentionPolicy.WithinLicensedLimitRule, decision.RuleId);
    }

    [Fact]
    public void A_payload_past_its_licensed_limit_must_be_deleted()
    {
        var decision = RetentionPolicy.Evaluate(
            Capped(365),
            Now.AddDays(-400),
            Now,
            isReferencedByEvidence: false);

        Assert.True(decision.RequiresDeletion);
        Assert.Equal(RetentionPolicy.LicensedLimitExceededRule, decision.RuleId);
        Assert.False(decision.RequiresEvidenceMarking);
    }

    /// <summary>
    /// The floor. A licence may compel deletion of referenced evidence - but the reference is not
    /// cancelled by the deletion, so the caller is told to mark it rather than let the gap go
    /// unrecorded.
    /// </summary>
    [Fact]
    public void Referenced_evidence_past_its_limit_is_deleted_and_marked()
    {
        var decision = RetentionPolicy.Evaluate(
            Capped(365),
            Now.AddDays(-400),
            Now,
            isReferencedByEvidence: true);

        Assert.True(decision.RequiresDeletion);
        Assert.True(decision.RequiresEvidenceMarking);
        Assert.Contains("unreplayable", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Being referenced is not, on its own, a reason to delete anything. Without a licensed cap
    /// there is no obligation, and this platform has no other deletion path.
    /// </summary>
    [Fact]
    public void Reference_state_alone_never_causes_deletion()
    {
        foreach (var referenced in new[] { true, false })
        {
            var decision = RetentionPolicy.Evaluate(
                LicensingTerms.OpenData(),
                Now.AddYears(-20),
                Now,
                referenced);

            Assert.False(decision.RequiresDeletion);
        }
    }

    /// <summary>
    /// The default outcome is Retain, not Deny. Everywhere else the safe default is refusal;
    /// here the irreversible operation is the deletion, so an unset value must read as "keep".
    /// </summary>
    [Fact]
    public void The_default_outcome_is_retain() =>
        Assert.Equal(RetentionOutcome.Retain, default(RetentionOutcome));

    [Fact]
    public void Timestamps_must_be_utc()
    {
        Assert.Throws<DomainValidationException>(() =>
            RetentionPolicy.Evaluate(LicensingTerms.OpenData(), DateTime.Now, Now, false));

        Assert.Throws<DomainValidationException>(() =>
            RetentionPolicy.Evaluate(LicensingTerms.OpenData(), Now, DateTime.Now, false));
    }

    /// <summary>
    /// Nothing in the retention engine names a provider. A source with a different obligation is a
    /// registration, not a change to this rule set.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(365)]
    [InlineData(2555)]
    public void Any_licensed_limit_is_honoured_without_the_engine_knowing_the_source(int days)
    {
        Assert.False(RetentionPolicy.Evaluate(Capped(days), Now.AddDays(-(days - 1)), Now, false).RequiresDeletion);
        Assert.True(RetentionPolicy.Evaluate(Capped(days), Now.AddDays(-(days + 1)), Now, false).RequiresDeletion);
    }
}

public sealed class UnreplayableEvidenceTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ContentHash Hash = ContentHash.Compute([1, 2, 3]);
    private static readonly SourceId Source = SourceId.Create("some-vendor");

    private static RetentionDecision Deletion(bool marking = true) =>
        new(RetentionOutcome.DeleteRequired,
            RetentionPolicy.LicensedLimitExceededRule,
            "past the licensed limit",
            marking);

    [Fact]
    public void A_marker_records_the_rule_and_reason()
    {
        var marker = UnreplayableEvidence.Mark(Hash, Source, Deletion(), Now);

        Assert.Equal(Hash, marker.Id);
        Assert.Equal(Source, marker.SourceId);
        Assert.Equal(RetentionPolicy.LicensedLimitExceededRule, marker.RuleId);
        Assert.Equal("past the licensed limit", marker.Reason);
        Assert.Equal(Now, marker.MarkedAtUtc);
    }

    /// <summary>
    /// Marking evidence whose payload still exists would report a gap that is not there.
    /// </summary>
    [Fact]
    public void Evidence_cannot_be_marked_unreplayable_while_its_payload_survives()
    {
        var retain = new RetentionDecision(
            RetentionOutcome.Retain,
            RetentionPolicy.WithinLicensedLimitRule,
            "still within the limit");

        Assert.Throws<DomainRuleViolationException>(() =>
            UnreplayableEvidence.Mark(Hash, Source, retain, Now));
    }
}
