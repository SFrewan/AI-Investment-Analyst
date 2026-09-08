using System.Text;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Normalization;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Application.UnitTests.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Normalization;

/// <summary>
/// The third result shape: a payload read with some of its rows refused.
/// </summary>
/// <remarks>
/// <para>
/// It exists because eleven archived price payloads held between 556 and 969 sound rows each and
/// were discarded whole for one to twenty rows carrying a non-positive close. That was not a
/// judgement anybody made; it was what a two-shaped result forced, because
/// <c>NormalizationResult.Quarantine</c> constructs with an empty observation list and a normaliser
/// meeting a bad row had no other way to report it.
/// </para>
/// <para>
/// Two things have to stay true for the shape to be safe, and both are asserted below.
/// <c>IsQuarantined</c> must keep meaning "the payload could not be read at all", so every existing
/// caller that branches on it behaves as it did. And a partial read must be impossible to
/// manufacture out of a payload that yielded nothing - otherwise a document the platform could not
/// use would start looking like a document with nothing in it.
/// </para>
/// </remarks>
public sealed class PartialNormalizationTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Retrieved = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId TestSource = SourceId.Create("test-source");
    private const DataCategory Profile = DataCategory.CompanyProfile;
    private const string RowRule = "market-data.unreadable-row@1";

    private static readonly IngestionSubject Apple = IngestionSubject.Create("Company", "0000320193");

    // ================= the shape itself =================

    [Fact]
    public void A_partial_read_is_not_quarantined_and_carries_what_it_refused()
    {
        var result = NormalizationResult.Partial([AnObservation()], RowRule, "row 3 was dropped", 1);

        Assert.False(result.IsQuarantined);
        Assert.Null(result.RuleId);
        Assert.Null(result.Reason);

        Assert.True(result.HadRejectedRows);
        Assert.Equal(1, result.RejectedRows);
        Assert.Equal(RowRule, result.RejectedRowRuleId);
        Assert.Equal("row 3 was dropped", result.RejectedRowReason);
        Assert.Single(result.Observations);
    }

    /// <summary>A plain read and a refusal keep their exact previous shape.</summary>
    [Fact]
    public void The_two_existing_shapes_are_unchanged()
    {
        var read = NormalizationResult.Normalized([AnObservation()]);

        Assert.False(read.IsQuarantined);
        Assert.False(read.HadRejectedRows);
        Assert.Equal(0, read.RejectedRows);
        Assert.Null(read.RejectedRowRuleId);
        Assert.Null(read.RejectedRowReason);

        var refused = NormalizationResult.Quarantine("normalization.test-rejection@1", "no");

        Assert.True(refused.IsQuarantined);
        Assert.Empty(refused.Observations);
        Assert.Equal(0, refused.RejectedRows);
        Assert.Null(refused.RejectedRowRuleId);
    }

    /// <summary>
    /// A payload that yielded nothing cannot be dressed as a partial read.
    /// </summary>
    /// <remarks>
    /// The guard that keeps row-level refusal from becoming a way to be permissive. A document with
    /// no usable rows is quarantined; saying it was "read, with everything refused" would make it
    /// indistinguishable from an instrument that genuinely has no history.
    /// </remarks>
    [Fact]
    public void A_partial_read_of_nothing_is_refused_by_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NormalizationResult.Partial([], RowRule, "everything was dropped", 3));
    }

    [Fact]
    public void A_partial_read_that_refused_no_rows_is_refused_by_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NormalizationResult.Partial([AnObservation()], RowRule, "nothing was dropped", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_partial_read_must_name_the_rule_that_refused_the_rows(string? rule)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => NormalizationResult.Partial([AnObservation()], rule!, "a reason", 1));
    }

    [Fact]
    public void A_partial_read_describes_itself_as_both_read_and_refused()
    {
        var text = NormalizationResult.Partial([AnObservation()], RowRule, "row 3", 1).ToString();

        Assert.Contains("1 observations", text, StringComparison.Ordinal);
        Assert.Contains("1 row(s) refused", text, StringComparison.Ordinal);
        Assert.Contains(RowRule, text, StringComparison.Ordinal);
    }

    // ================= the pipeline carrying it =================

    /// <summary>
    /// A partially read payload has its observations recorded and its refused rows counted.
    /// </summary>
    /// <remarks>
    /// The point of the whole change: the sound rows reach the store instead of being discarded
    /// with the bad ones. The count of refused rows travels beside them so the hole stays visible.
    /// </remarks>
    [Fact]
    public async Task A_partially_read_payload_records_its_observations()
    {
        var harness = Build(PartiallyReads(observations: 3, rejected: 2));

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(1, summary.PayloadsRead);
        Assert.Equal(3, summary.ObservationsRecorded);
        Assert.Equal(3, harness.Observations.Recorded.Count);
        Assert.Equal(2, summary.RowsRejected);
    }

    /// <summary>
    /// A partial read is not a quarantined payload, and does not enter the operator's queue as one.
    /// </summary>
    /// <remarks>
    /// <c>PayloadsQuarantined</c> has always meant "the payload could not be read at all", and the
    /// quarantine store is the queue an operator works through. A payload that was read does not
    /// belong in it; its refused rows are reported as their own number instead, so neither fact
    /// hides the other.
    /// </remarks>
    [Fact]
    public async Task A_partial_read_is_not_recorded_as_a_quarantined_payload()
    {
        var harness = Build(PartiallyReads(observations: 2, rejected: 1));

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(0, summary.PayloadsQuarantined);
        Assert.False(summary.HadFailures);
        Assert.Empty(harness.Quarantine.Recorded);
    }

    /// <summary>A wholly unreadable payload behaves exactly as it did before.</summary>
    [Fact]
    public async Task A_quarantined_payload_is_unaffected_by_the_new_shape()
    {
        var harness = Build(new StubNormalizer(
            _ => NormalizationResult.Quarantine("normalization.test-rejection@1", "refused")));

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(0, summary.PayloadsRead);
        Assert.Equal(0, summary.ObservationsRecorded);
        Assert.Equal(1, summary.PayloadsQuarantined);
        Assert.Equal(0, summary.RowsRejected);
        Assert.True(summary.HadFailures);
        Assert.Single(harness.Quarantine.Recorded);
    }

    /// <summary>Refused rows are summed across payloads, not reported for the last one only.</summary>
    [Fact]
    public async Task Rejected_rows_are_summed_across_the_runs_payloads()
    {
        var harness = Build(PartiallyReads(observations: 1, rejected: 2), payloads: 3);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(3, summary.PayloadsRead);
        Assert.Equal(6, summary.RowsRejected);
    }

    [Fact]
    public void A_summary_with_no_rejected_rows_reads_exactly_as_it_did()
    {
        Assert.Equal(
            "read=2, recorded=5, quarantined=1",
            new NormalizationSummary(2, 5, 1).ToString());

        Assert.Equal(
            "read=2, recorded=5, quarantined=1, rows rejected=3",
            new NormalizationSummary(2, 5, 1, 3).ToString());
    }

    // ---------- fixtures ----------

    private static Observation AnObservation(string attribute = "company.name") =>
        Observation.RecordFact(
            Apple,
            attribute,
            ObservationValue.Text("Apple Inc."),
            Provenance.Create(TestSource, Retrieved, Retrieved, Retrieved));

    private static StubNormalizer PartiallyReads(int observations, int rejected) =>
        new(_ => NormalizationResult.Partial(
            Enumerable.Range(0, observations)
                .Select(i => AnObservation($"company.attribute-{i}"))
                .ToList(),
            RowRule,
            $"{rejected} row(s) were dropped",
            rejected));

    private sealed record Harness(
        NormalizationPipeline Pipeline,
        RecordingObservationStore Observations,
        RecordingQuarantineStore Quarantine,
        IngestionRun Run);

    private static Harness Build(INormalizer normalizer, int payloads = 1)
    {
        var archive = new RecordingArchive();
        var observations = new RecordingObservationStore();
        var quarantine = new RecordingQuarantineStore();
        var actions = new StubActionGateway(ActionOutcomeStatus.Executed);

        var request = IngestionRequest.Create(
            TestSource, Profile, Region.UnitedStates, Apple, CorrelationId.New(), Now);

        var run = IngestionRun.Start(request, Now);

        for (var i = 0; i < payloads; i++)
        {
            run.RecordArtifact(archive.Seed(
                Encoding.UTF8.GetBytes($$"""{"name": "Company {{i}}"}"""), TestSource, Retrieved));
        }

        run.MarkSucceeded(Now);

        var pipeline = new NormalizationPipeline(
            archive, [normalizer], observations, quarantine, actions, new FixedClock(Now));

        return new Harness(pipeline, observations, quarantine, run);
    }
}
