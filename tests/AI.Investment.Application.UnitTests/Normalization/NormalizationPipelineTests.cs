using System.Text;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Normalization;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Application.UnitTests.Ingestion;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Normalization;

/// <summary>
/// Reading what a run archived, and what happens when it cannot be read.
/// </summary>
/// <remarks>
/// <para>
/// Three claims carry most of the weight. A payload that cannot be read is quarantined rather than
/// dropped, because a discarded failure is indistinguishable from data that never arrived. Writing
/// observations goes through the safety seam, because an observation is something the platform
/// believes. And a denial records zero observations while still reporting the payloads it read,
/// because collapsing those two numbers would hide the denial.
/// </para>
/// <para>
/// The archive double is shared with the ingestion tests deliberately: the pipeline reads exactly
/// what ingestion wrote, and a second, subtly different fake would let the two halves drift.
/// </para>
/// </remarks>
public sealed class NormalizationPipelineTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Retrieved = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId TestSource = SourceId.Create("test-source");
    private const DataCategory Profile = DataCategory.CompanyProfile;

    private static readonly IngestionSubject Apple = IngestionSubject.Create("Company", "0000320193");

    // ---------- fixtures ----------

    private static IngestionRequest Request() =>
        IngestionRequest.Create(
            TestSource,
            Profile,
            Region.UnitedStates,
            Apple,
            CorrelationId.New(),
            Now);

    private static Observation AnObservation(string attribute = "company.name") =>
        Observation.RecordFact(
            Apple,
            attribute,
            ObservationValue.Text("Apple Inc."),
            Provenance.Create(TestSource, Retrieved, Retrieved, Retrieved));

    private sealed record Harness(
        NormalizationPipeline Pipeline,
        RecordingArchive Archive,
        RecordingObservationStore Observations,
        RecordingQuarantineStore Quarantine,
        StubActionGateway Actions,
        IngestionRun Run);

    /// <summary>Builds a run whose artifacts are all present in the archive.</summary>
    private static Harness Build(
        INormalizer? normalizer = null,
        int payloads = 1,
        ActionOutcomeStatus policy = ActionOutcomeStatus.Executed,
        bool archivePayloads = true)
    {
        var archive = new RecordingArchive();
        var observations = new RecordingObservationStore();
        var quarantine = new RecordingQuarantineStore();
        var actions = new StubActionGateway(policy);

        var request = Request();
        var run = IngestionRun.Start(request, Now);

        for (var i = 0; i < payloads; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($$"""{"name": "Company {{i}}"}""");

            var hash = archivePayloads
                ? archive.Seed(bytes, TestSource, Retrieved)
                : ContentHash.Compute(bytes);

            run.RecordArtifact(hash);
        }

        run.MarkSucceeded(Now);

        INormalizer[] normalizers = normalizer is null ? [] : [normalizer];

        var pipeline = new NormalizationPipeline(
            archive,
            normalizers,
            observations,
            quarantine,
            actions,
            new FixedClock(Now));

        return new Harness(pipeline, archive, observations, quarantine, actions, run);
    }

    private static StubNormalizer Reads(int observationsEach = 1) =>
        new(_ => NormalizationResult.Normalized(
            Enumerable.Range(0, observationsEach)
                .Select(i => AnObservation($"company.attribute-{i}"))
                .ToList()));

    private static StubNormalizer Rejects(string rule = "normalization.test-rejection@1") =>
        new(_ => NormalizationResult.Quarantine(rule, "the stub refused this payload"));

    // ---------- the happy path ----------

    [Fact]
    public async Task A_readable_payload_becomes_recorded_observations()
    {
        var harness = Build(Reads(observationsEach: 3));

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(1, summary.PayloadsRead);
        Assert.Equal(3, summary.ObservationsRecorded);
        Assert.Equal(0, summary.PayloadsQuarantined);
        Assert.Equal(3, harness.Observations.Recorded.Count);
    }

    [Fact]
    public async Task Every_artifact_in_the_run_is_read()
    {
        var normalizer = Reads();
        var harness = Build(normalizer, payloads: 4);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(4, normalizer.Calls);
        Assert.Equal(4, summary.PayloadsRead);
    }

    [Fact]
    public async Task The_normaliser_is_given_the_archived_retrieval_time()
    {
        DateTime? seen = null;

        var normalizer = new StubNormalizer(input =>
        {
            seen = input.RetrievedAtUtc;

            return NormalizationResult.Normalized([]);
        });

        var harness = Build(normalizer);

        await harness.Pipeline.NormalizeAsync(harness.Run);

        // Taken from the archive entry, not from the clock. Every observation's provenance is
        // built from it, and using "now" would silently claim the platform learned the value at
        // normalisation time rather than when it actually fetched it.
        Assert.Equal(Retrieved, seen);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_a_run_produced_no_observations()
    {
        var harness = Build(new StubNormalizer(_ => NormalizationResult.Normalized([])));

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        // No proposal either. An empty document is not a side effect, and dispatching one would
        // add an audit row saying nothing happened.
        Assert.Equal(1, summary.PayloadsRead);
        Assert.Equal(0, summary.ObservationsRecorded);
        Assert.Null(harness.Actions.LastProposal);
        Assert.Equal(0, harness.Observations.RecordCalls);
    }

    // ---------- the safety seam ----------

    [Fact]
    public async Task Recording_observations_goes_through_the_seam()
    {
        var harness = Build(Reads());

        await harness.Pipeline.NormalizeAsync(harness.Run);

        var proposal = Assert.IsType<ActionProposal>(harness.Actions.LastProposal);

        Assert.Equal(Capability.DataIngestion, proposal.Capability);
        Assert.Equal(1, harness.Actions.EffectInvocations);
    }

    [Fact]
    public async Task The_proposal_is_keyed_on_the_run_so_a_replay_cannot_double_the_observations()
    {
        var harness = Build(Reads());

        await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(
            $"normalization.record:{harness.Run.Id}",
            harness.Actions.LastProposal!.IdempotencyKey);
    }

    [Fact]
    public async Task The_proposal_describes_the_shape_rather_than_the_values()
    {
        var harness = Build(Reads(observationsEach: 2));

        await harness.Pipeline.NormalizeAsync(harness.Run);

        var parameters = Assert.IsType<NormalizationParameters>(
            harness.Actions.LastProposal!.Parameters);

        // The audit trail is append-only and unredactable, so a provider's raw content must never
        // be copied into it. Counts and categories, never values.
        Assert.Equal(2, parameters.ObservationCount);
        Assert.Equal(TestSource, parameters.SourceId);
        Assert.Equal(Profile, parameters.Category);
        Assert.DoesNotContain("Apple", parameters.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ActionOutcomeStatus.Denied)]
    [InlineData(ActionOutcomeStatus.ApprovalRequired)]
    [InlineData(ActionOutcomeStatus.DuplicateSuppressed)]
    public async Task A_refused_write_records_nothing_but_still_reports_what_was_read(
        ActionOutcomeStatus status)
    {
        var harness = Build(Reads(observationsEach: 3), policy: status);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        // The two numbers are separate facts. Reporting zero payloads read because the write was
        // denied would hide the denial behind what looks like an empty response.
        Assert.Equal(1, summary.PayloadsRead);
        Assert.Equal(0, summary.ObservationsRecorded);
        Assert.Equal(0, harness.Actions.EffectInvocations);
        Assert.Empty(harness.Observations.Recorded);
    }

    // ---------- quarantine, never discard ----------

    [Fact]
    public async Task A_payload_the_normaliser_rejects_is_quarantined()
    {
        var harness = Build(Rejects("normalization.unreadable-payload@1"));

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(0, summary.PayloadsRead);
        Assert.Equal(1, summary.PayloadsQuarantined);
        Assert.True(summary.HadFailures);

        var record = Assert.Single(harness.Quarantine.Recorded);

        Assert.Equal("normalization.unreadable-payload@1", record.RuleId);
        Assert.Equal(TestSource, record.SourceId);
        Assert.Equal(Profile, record.Category);
    }

    [Fact]
    public async Task A_quarantine_is_not_re_recorded_for_the_same_payload()
    {
        var harness = Build(Rejects());

        await harness.Pipeline.NormalizeAsync(harness.Run);
        await harness.Pipeline.NormalizeAsync(harness.Run);

        // One record per payload, not one per attempt. Re-recording would make a retry look like
        // a new problem, and the operator queue would fill with the same failure.
        Assert.Single(harness.Quarantine.Recorded);
    }

    [Fact]
    public async Task Quarantining_does_not_pass_through_the_seam()
    {
        var harness = Build(Rejects(), policy: ActionOutcomeStatus.Denied);

        await harness.Pipeline.NormalizeAsync(harness.Run);

        // A policy denial is one of the things worth quarantining a run over, so the record of
        // "this could not be read" must be writable in exactly the state where nothing else is.
        Assert.Single(harness.Quarantine.Recorded);
        Assert.Null(harness.Actions.LastProposal);
    }

    [Fact]
    public async Task A_rejected_payload_does_not_stop_the_others()
    {
        var rejected = 0;

        // Rejects the first payload and reads the rest.
        var normalizer = new StubNormalizer(_ =>
            rejected++ == 0
                ? NormalizationResult.Quarantine("normalization.test-rejection@1", "the first one")
                : NormalizationResult.Normalized([AnObservation()]));

        var harness = Build(normalizer, payloads: 3);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(2, summary.PayloadsRead);
        Assert.Equal(2, summary.ObservationsRecorded);
        Assert.Equal(1, summary.PayloadsQuarantined);
    }

    [Fact]
    public async Task A_run_with_no_normaliser_quarantines_rather_than_silently_producing_nothing()
    {
        var harness = Build(normalizer: null);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(1, summary.PayloadsQuarantined);
        Assert.Equal(
            NormalizationPipeline.NoNormalizerRule,
            Assert.Single(harness.Quarantine.Recorded).RuleId);
    }

    [Fact]
    public async Task A_normaliser_that_does_not_claim_the_category_is_not_used()
    {
        var wrongCategory = new StubNormalizer(
            _ => NormalizationResult.Normalized([AnObservation()]),
            onlyCategory: DataCategory.MarketPrices);

        var harness = Build(wrongCategory);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(0, wrongCategory.Calls);
        Assert.Equal(1, summary.PayloadsQuarantined);
    }

    [Fact]
    public async Task A_payload_the_archive_no_longer_holds_is_quarantined()
    {
        var harness = Build(Reads(), archivePayloads: false);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        // Either retention deleted it under licence or the archive lost it. Both are worth knowing
        // about, and neither is a reason to invent observations.
        Assert.Equal(1, summary.PayloadsQuarantined);
        Assert.Equal(
            NormalizationPipeline.PayloadMissingRule,
            Assert.Single(harness.Quarantine.Recorded).RuleId);
    }

    [Fact]
    public async Task A_missing_payload_is_never_read()
    {
        var normalizer = Reads();
        var harness = Build(normalizer, archivePayloads: false);

        await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(0, normalizer.Calls);
    }

    // ---------- refusals ----------

    [Fact]
    public async Task A_null_run_is_refused()
    {
        var harness = Build(Reads());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => harness.Pipeline.NormalizeAsync(null!));
    }

    [Fact]
    public async Task A_run_that_archived_nothing_produces_an_empty_summary()
    {
        var harness = Build(Reads(), payloads: 0);

        var summary = await harness.Pipeline.NormalizeAsync(harness.Run);

        Assert.Equal(new NormalizationSummary(0, 0, 0), summary);
        Assert.Null(harness.Actions.LastProposal);
        Assert.False(summary.HadFailures);
    }
}
