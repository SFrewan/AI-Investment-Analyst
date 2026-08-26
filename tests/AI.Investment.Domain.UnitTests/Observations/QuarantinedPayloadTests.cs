using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Observations;

/// <summary>
/// The record that a payload arrived and could not be read.
/// </summary>
/// <remarks>
/// Quarantining exists so that a failure to interpret data stays distinguishable from data that
/// never arrived. The invariants below are what make the record worth having: it names the rule, it
/// says why, and it is keyed by the bytes rather than by the attempt - so a retry collides with the
/// original instead of making one problem look like two.
/// </remarks>
public sealed class QuarantinedPayloadTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 14, 30, 0, DateTimeKind.Utc);

    private static readonly ContentHash Hash = ContentHash.Compute("payload"u8);

    private static QuarantinedPayload Record(
        string ruleId = "normalization.unreadable-payload@1",
        string reason = "The payload is not readable JSON (JsonException).") =>
        QuarantinedPayload.Record(
            Hash,
            SourceId.Create("sec-edgar"),
            DataCategory.CompanyProfile,
            ruleId,
            reason,
            Now);

    [Fact]
    public void A_quarantine_is_keyed_by_the_payload_it_describes() =>

        // Not by the attempt. The same bytes fail the same way, and one record per payload is more
        // useful than one per retry.
        Assert.Equal(Hash, Record().Id);

    [Fact]
    public void A_quarantine_names_the_rule_the_source_and_the_reason()
    {
        var record = Record();

        Assert.Equal("normalization.unreadable-payload@1", record.RuleId);
        Assert.Equal(SourceId.Create("sec-edgar"), record.SourceId);
        Assert.Equal(DataCategory.CompanyProfile, record.Category);
        Assert.Equal(Now, record.QuarantinedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_quarantine_with_no_rule_is_refused(string blank) =>

        // Recording that something failed without recording what rejected it leaves an operator
        // with a symptom and no way to reproduce it.
        Assert.Throws<DomainValidationException>(() => Record(ruleId: blank));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_quarantine_with_no_reason_is_refused(string blank) =>
        Assert.Throws<DomainValidationException>(() => Record(reason: blank));

    [Fact]
    public void An_overlong_reason_is_truncated_rather_than_rejected()
    {
        var record = Record(reason: new string('r', QuarantinedPayload.MaxReasonLength + 100));

        // Losing the record over the length of its explanation would trade the whole signal for a
        // formatting rule.
        Assert.Equal(QuarantinedPayload.MaxReasonLength, record.Reason.Length);
    }

    [Fact]
    public void An_overlong_rule_id_is_truncated()
    {
        var record = Record(ruleId: new string('x', QuarantinedPayload.MaxRuleIdLength + 10));

        Assert.Equal(QuarantinedPayload.MaxRuleIdLength, record.RuleId.Length);
    }

    [Fact]
    public void The_rule_and_reason_are_trimmed()
    {
        var record = Record(ruleId: "  rule@1  ", reason: "  because  ");

        Assert.Equal("rule@1", record.RuleId);
        Assert.Equal("because", record.Reason);
    }

    [Fact]
    public void A_non_UTC_timestamp_is_refused() =>
        Assert.ThrowsAny<DomainException>(() => QuarantinedPayload.Record(
            Hash,
            SourceId.Create("sec-edgar"),
            DataCategory.CompanyProfile,
            "rule@1",
            "because",
            new DateTime(2026, 8, 22, 14, 30, 0, DateTimeKind.Local)));

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => QuarantinedPayload.Record(
            null!,
            SourceId.Create("sec-edgar"),
            DataCategory.CompanyProfile,
            "rule@1",
            "because",
            Now));

        Assert.Throws<ArgumentNullException>(() => QuarantinedPayload.Record(
            Hash,
            null!,
            DataCategory.CompanyProfile,
            "rule@1",
            "because",
            Now));
    }
}
