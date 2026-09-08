using System.Reflection;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Application.UnitTests.Normalization;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ingestion;

/// <summary>
/// Re-reading a payload the archive already holds, without asking a provider for it again.
/// </summary>
/// <remarks>
/// <para>
/// Eleven archived price payloads were discarded whole because a handful of their rows carried a
/// non-positive close. Row-level refusal fixed the reading; it did not put the readable rows into
/// the store, because nothing in the platform could re-interpret bytes it had already fetched. This
/// is that path, and these are the properties it has to have before anybody points it at real
/// evidence.
/// </para>
/// <para>
/// <strong>The property that matters most is the dull one.</strong> The observation store appends
/// without a natural key and every observation takes a fresh identity, so two replays would leave
/// two copies of a price series that no constraint would reject. This platform has already lived
/// through that once - 5,249 duplicate closing-price rows sat in the store while the gate that was
/// supposed to notice read PASS, because it had been pointed at the wrong namespace. The only thing
/// standing in the way is the seam's idempotency key, and the key is the ingestion run's identity.
/// So the tests below care less about what a successful replay does than about what a second one
/// does not.
/// </para>
/// </remarks>
public sealed class ArchivedPayloadReplayTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>When the provider actually answered. Nothing may substitute the replay clock for it.</summary>
    private static readonly DateTime Retrieved = new(2026, 9, 4, 22, 46, 43, DateTimeKind.Utc);

    private static readonly SourceId PriceSource = SourceId.Create("eodhd-eod");
    private const DataCategory Prices = DataCategory.MarketPrices;

    // ================= A. run discovery =================

    [Fact]
    public async Task The_run_that_archived_a_payload_is_found_by_that_payload()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.SeedRun("EVBG.US", """[{"date":"2021-09-01","close":9.75}]""");

        var found = await world.Runs.RunsForArchivedPayloadAsync(run.Artifacts[0]);

        Assert.Single(found);
        Assert.Equal(run.Id, found[0].Id);
    }

    /// <summary>
    /// Two runs holding the same bytes are both returned, in a stable order.
    /// </summary>
    /// <remarks>
    /// Not a hypothetical. Five members of the sealed universe were answered with the two-byte
    /// document <c>[]</c>, and the archive is content-addressed, so all five point at one payload.
    /// A lookup that returned "the" run would have had to pick one of the five in silence.
    /// </remarks>
    [Fact]
    public async Task A_payload_shared_by_several_runs_returns_all_of_them_in_a_stable_order()
    {
        var world = new World();
        var second = world.SeedRun("KIN.US", "[]", startedAtUtc: Now.AddHours(2));
        var first = world.SeedRun("BPYU.US", "[]", startedAtUtc: Now.AddHours(1));

        var found = await world.Runs.RunsForArchivedPayloadAsync(first.Artifacts[0]);

        Assert.Equal(2, found.Count);
        Assert.Equal(first.Id, found[0].Id);
        Assert.Equal(second.Id, found[1].Id);

        // Asked again, in the same order. Determinism is the whole value of the ordering.
        var again = await world.Runs.RunsForArchivedPayloadAsync(first.Artifacts[0]);
        Assert.Equal(
            found.Select(r => r.Id).ToArray(),
            again.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_payload_no_run_recorded_is_refused_rather_than_guessed_at()
    {
        var world = new World();
        world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");

        var orphan = ContentHash.Compute(Encoding.UTF8.GetBytes("""[{"date":"2021-09-01","close":1}]"""));

        var result = await world.Replay.ReplayAsync(orphan);

        Assert.Equal(ArchivedReplayStatus.NoRunHoldsThisPayload, result.Status);
        Assert.Null(result.RunId);
        Assert.Equal(0, result.ObservationsRecorded);
        Assert.Empty(world.Observations.Recorded);
    }

    [Fact]
    public async Task A_payload_several_runs_share_is_refused_rather_than_attributed_to_one_of_them()
    {
        var world = new World();
        var first = world.SeedRun("BPYU.US", "[]", startedAtUtc: Now.AddHours(1));
        world.SeedRun("KIN.US", "[]", startedAtUtc: Now.AddHours(2));

        var result = await world.Replay.ReplayAsync(first.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.MoreThanOneRunHoldsThisPayload, result.Status);
        Assert.Empty(world.Observations.Recorded);
        Assert.Equal(0, world.Gateway.EffectInvocations);
    }

    // ================= B. the replay itself =================

    /// <summary>
    /// A run whose payload was quarantined records its observations when read again.
    /// </summary>
    /// <remarks>
    /// The situation the eleven are in: a succeeded run, bytes in the archive, a quarantine record,
    /// and nothing in the observation store - because the pipeline never reaches its write when a
    /// payload is refused whole.
    /// </remarks>
    [Fact]
    public async Task Replaying_a_run_whose_payload_was_quarantined_records_its_observations()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.SeedHistoricalQuarantine(run, "market-data.unreadable-row@1", "Row 557: close is not positive.");

        Assert.Empty(world.Observations.Recorded);

        world.Admits(556);

        var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.Replayed, result.Status);
        Assert.True(result.RecordedObservations);
        Assert.Equal(run.Id, result.RunId);
        Assert.Equal(556, result.ObservationsRecorded);
        Assert.Equal(556, world.Observations.Recorded.Count);
        Assert.Equal(1, world.Gateway.EffectInvocations);
    }

    /// <summary>
    /// The five members the recovery is for, as a fixture: 2,940 observations across five runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this proves and what it does not.</strong> The per-member admitted counts are
    /// measurements taken from the real archived payloads by the census, and what the normaliser
    /// admits from those bytes is pinned by the EODHD normaliser's own tests. This test asserts
    /// something narrower and still worth asserting: that five independent replays, each under its
    /// own run identity, carry those counts through the pipeline and the seam and arrive at 2,940
    /// recorded observations with five separate idempotency keys. It is a fixture. It is not a
    /// claim about the production store, which this block does not touch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_five_member_fixture_records_two_thousand_nine_hundred_and_forty()
    {
        var members = new (string Symbol, int Admits)[]
        {
            ("CCF.US", 556),
            ("EVBG.US", 711),
            ("GPP.US", 592),
            ("VLDR.US", 369),
            ("WIRE.US", 712),
        };

        var world = new World();
        var runs = new List<IngestionRun>();

        foreach (var (symbol, _) in members)
        {
            var run = world.SeedRun(symbol, $$"""[{"date":"2021-09-01","close":2.5,"for":"{{symbol}}"}]""");
            world.SeedHistoricalQuarantine(run, "market-data.unreadable-row@1", $"{symbol}: a row was not positive.");
            runs.Add(run);
        }

        world.AdmitsPerPayload(runs.Select((r, i) => (r.Artifacts[0], members[i].Admits)));

        var recorded = 0;

        foreach (var run in runs)
        {
            var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

            Assert.Equal(ArchivedReplayStatus.Replayed, result.Status);
            Assert.Equal(run.Id, result.RunId);

            recorded += result.ObservationsRecorded;
        }

        Assert.Equal(2940, recorded);
        Assert.Equal(2940, world.Observations.Recorded.Count);

        // Five runs, five keys, five effects. Recovery is five independent operations, and one
        // failing must not suppress another.
        Assert.Equal(5, world.Gateway.EffectInvocations);
        Assert.Equal(5, world.Gateway.Keys.Distinct(StringComparer.Ordinal).Count());
    }

    // ================= C. the second replay =================

    /// <summary>
    /// Replaying the same run again is suppressed by the seam and adds nothing.
    /// </summary>
    /// <remarks>
    /// The property the whole design exists for. Nothing below the seam would have stopped this:
    /// the store appends, and the observations built on the second pass are real, distinct objects
    /// that it would have accepted.
    /// </remarks>
    [Fact]
    public async Task Replaying_the_same_run_twice_records_nothing_the_second_time()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.Admits(556);

        var first = await world.Replay.ReplayAsync(run.Artifacts[0]);
        var second = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.Replayed, first.Status);
        Assert.Equal(556, first.ObservationsRecorded);

        Assert.Equal(ArchivedReplayStatus.AlreadyRecorded, second.Status);
        Assert.False(second.RecordedObservations);
        Assert.Equal(0, second.ObservationsRecorded);

        // The store never saw the second batch at all.
        Assert.Equal(556, world.Observations.Recorded.Count);
        Assert.Equal(1, world.Observations.RecordCalls);

        // Both attempts offered the seam the same key; only the first ran the effect.
        Assert.Equal(2, world.Gateway.Keys.Count);
        Assert.Single(world.Gateway.Keys.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, world.Gateway.EffectInvocations);
    }

    /// <summary>The key the seam is offered is the run's identity, and it does not drift.</summary>
    [Fact]
    public async Task The_idempotency_key_is_the_original_runs_identity()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.Admits(3);

        await world.Replay.ReplayAsync(run.Artifacts[0]);
        await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.All(
            world.Gateway.Keys,
            key => Assert.Equal("normalization.record:" + run.Id.ToString(), key));
    }

    // ================= D. no way to replay under a fresh run =================

    /// <summary>
    /// The service cannot be handed a run, so it cannot be handed a new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated as a test rather than a convention because the failure it prevents is silent. A
    /// signature taking an <see cref="IngestionRun"/> would let a caller build a fresh run over the
    /// same archived bytes; the pipeline would key the write on that new identity, the seam would
    /// see a key it had never claimed, and a second copy of the whole series would land in a store
    /// with no constraint able to reject it.
    /// </para>
    /// <para>
    /// So the entry point takes a <see cref="ContentHash"/> and looks the run up itself. This test
    /// fails the moment anyone widens it.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_entry_point_accepts_a_caller_supplied_ingestion_run()
    {
        var methods = typeof(IArchivedPayloadReplay)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.Single(methods);

        var parameters = methods[0].GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Equal(new[] { typeof(ContentHash), typeof(CancellationToken) }, parameters);
        Assert.DoesNotContain(typeof(IngestionRun), parameters);
        Assert.DoesNotContain(typeof(IngestionRunId), parameters);
    }

    /// <summary>The run replayed is the stored one, not one made on the way past.</summary>
    [Fact]
    public async Task Replay_uses_the_stored_runs_identity_and_never_a_new_one()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.Admits(2);

        var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(run.Id, result.RunId);
        Assert.Contains(run.Id.ToString(), world.Gateway.Keys[0], StringComparison.Ordinal);
    }

    // ================= E. provenance =================

    /// <summary>
    /// Provenance comes from the sidecar, never from the replay clock.
    /// </summary>
    /// <remarks>
    /// A replay in September must not restamp a payload fetched in September as though it had
    /// arrived at the moment somebody re-read it. Every observation below carries the archive's own
    /// retrieval instant, which is three days older than the clock the pipeline is holding.
    /// </remarks>
    [Fact]
    public async Task Every_replayed_observation_carries_the_archives_retrieval_instant()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.Admits(4);

        await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(4, world.Observations.Recorded.Count);
        Assert.All(
            world.Observations.Recorded,
            o => Assert.Equal(Retrieved, o.Provenance.RetrievedAtUtc));
        Assert.All(
            world.Observations.Recorded,
            o => Assert.NotEqual(Now, o.Provenance.RetrievedAtUtc));
        Assert.All(world.Observations.Recorded, o => Assert.Equal(PriceSource, o.Provenance.SourceId));
    }

    /// <summary>The normaliser is handed the archived address and the archived bytes, unchanged.</summary>
    [Fact]
    public async Task The_normaliser_reads_the_archived_content_hash_and_its_own_bytes()
    {
        const string Document = """[{"date":"2021-09-01","close":2.5}]""";

        var world = new World();
        var run = world.SeedRun("CCF.US", Document);
        world.Admits(1);

        await world.Replay.ReplayAsync(run.Artifacts[0]);

        var seen = Assert.Single(world.Normalizer.Seen);

        Assert.Equal(run.Artifacts[0], seen);
        Assert.Equal(ContentHash.Compute(Encoding.UTF8.GetBytes(Document)), seen);
        Assert.Equal(Retrieved, world.Normalizer.LastRetrievedAtUtc);
        Assert.Equal(
            Document,
            Encoding.UTF8.GetString(world.Normalizer.LastPayload.Span));
    }

    // ================= F. historical quarantine =================

    /// <summary>
    /// A successful replay leaves the old quarantine record exactly where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is evidence of a decision that really was made: at the time those bytes were
    /// first read, the normaliser could not read them. That remains true afterwards, and deleting
    /// it would erase the only trace of why the observations were missing for as long as they were.
    /// </para>
    /// <para>
    /// So the two coexist, deliberately: a payload can hold a historical quarantine record and a
    /// set of observations recovered from it. Nothing in this path removes, rewrites or
    /// reinterprets the record, and no reconciliation is attempted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_successful_replay_leaves_the_historical_quarantine_record_untouched()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");

        var historical = world.SeedHistoricalQuarantine(
            run,
            "market-data.unreadable-row@1",
            "Row 557: the 'close' field is missing, is not a number, or is not positive.");

        world.Admits(556);

        await world.Replay.ReplayAsync(run.Artifacts[0]);

        var still = Assert.Single(world.Quarantine.Recorded);

        Assert.Same(historical, still);
        Assert.Equal(run.Artifacts[0], still.Id);
        Assert.Equal("market-data.unreadable-row@1", still.RuleId);
        Assert.Equal(
            "Row 557: the 'close' field is missing, is not a number, or is not positive.",
            still.Reason);
        Assert.Equal(historical.QuarantinedAtUtc, still.QuarantinedAtUtc);

        // And the observations are there beside it. Coexistence, not reconciliation.
        Assert.Equal(556, world.Observations.Recorded.Count);
    }

    /// <summary>A payload the current normaliser still cannot read records nothing new.</summary>
    /// <remarks>
    /// The pipeline's own guard - one quarantine record per payload - means a replay that fails the
    /// same way does not file a second record, so a retry does not look like a new problem.
    /// </remarks>
    [Fact]
    public async Task A_payload_that_is_still_unreadable_adds_no_second_quarantine_record()
    {
        var world = new World();
        var run = world.SeedRun("MSOF.US", "[]");
        var historical = world.SeedHistoricalQuarantine(run, "market-data.empty-series@1", "The document is an empty array.");

        world.Refuses("market-data.empty-series@1", "The document is an empty array.");

        var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.PayloadStillUnreadable, result.Status);
        Assert.Equal(0, result.ObservationsRecorded);
        Assert.Empty(world.Observations.Recorded);
        Assert.Same(historical, Assert.Single(world.Quarantine.Recorded));
        Assert.Equal(0, world.Gateway.EffectInvocations);
    }

    /// <summary>
    /// An empty array is still an empty array. Replay changes nothing about what it means.
    /// </summary>
    /// <remarks>
    /// The five members answered with <c>[]</c> cannot be recovered from local evidence, and this
    /// pins that replay does not quietly become the place where that changes. What <c>[]</c> means
    /// is an open policy question and is not settled here.
    /// </remarks>
    [Fact]
    public async Task Replay_does_not_reinterpret_an_empty_array()
    {
        var world = new World();
        var run = world.SeedRun("USCR.US", "[]");

        world.Refuses(
            "market-data.empty-series@1",
            "The document is an empty array. An instrument with no history and a request that "
            + "asked for a range the vendor does not cover are different problems.");

        var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.PayloadStillUnreadable, result.Status);
        Assert.Empty(world.Observations.Recorded);

        var filed = Assert.Single(world.Quarantine.Recorded);
        Assert.Equal("market-data.empty-series@1", filed.RuleId);
    }

    // ================= G. what the service is allowed to depend on =================

    /// <summary>
    /// The replay service holds nothing that could reach a provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "This makes no network call" is worth more as a property of the constructor than as a
    /// sentence in a comment. The three dependencies below are a lookup, an archive and the
    /// normalisation pipeline; none of them can fetch anything, and none of them can spend an
    /// acquisition unit.
    /// </para>
    /// <para>
    /// Asserted as an exact set rather than an absence list, so a dependency nobody thought to ban
    /// still fails this test.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_replay_service_has_no_provider_or_authorisation_dependency()
    {
        var constructor = Assert.Single(typeof(ArchivedPayloadReplayService).GetConstructors());

        var parameters = constructor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Equal(
            new[] { typeof(IArchivedRunLookup), typeof(IRawResponseArchive), typeof(INormalizationPipeline) },
            parameters);

        var forbidden = new[]
        {
            typeof(IDataProvider),
            typeof(IProviderCatalogue),
            typeof(IProviderRateLimiter),
            typeof(IIngestionGateway),
            typeof(IDataAcquisition),
        };

        Assert.All(forbidden, type => Assert.DoesNotContain(type, parameters));
    }

    // ================= H. refusals that protect the store =================

    [Fact]
    public async Task A_run_that_did_not_succeed_is_not_replayed()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""", succeeded: false);
        world.Admits(10);

        var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.RunDidNotSucceed, result.Status);
        Assert.Equal(run.Id, result.RunId);
        Assert.Empty(world.Observations.Recorded);
        Assert.Equal(0, world.Normalizer.Calls);
    }

    /// <summary>
    /// A payload the archive cannot describe stops the replay before the pipeline is asked.
    /// </summary>
    /// <remarks>
    /// The archive root is a relative path resolved against the process working directory, so the
    /// likeliest way to reach this is running recovery from the wrong directory. The pipeline's own
    /// answer would be a quarantine record under <c>normalization.payload-missing@1</c> - accurate,
    /// but it would file an operator's mistake as a data-quality defect.
    /// </remarks>
    [Fact]
    public async Task A_payload_the_archive_no_longer_describes_stops_before_the_pipeline()
    {
        var world = new World();
        var run = world.SeedRun("CCF.US", """[{"date":"2021-09-01","close":2.5}]""");
        world.Admits(5);

        await world.Archive.DeleteAsync(run.Artifacts[0]);

        var result = await world.Replay.ReplayAsync(run.Artifacts[0]);

        Assert.Equal(ArchivedReplayStatus.ArchiveNoLongerHoldsThePayload, result.Status);
        Assert.Equal(run.Id, result.RunId);
        Assert.Equal(0, world.Normalizer.Calls);
        Assert.Empty(world.Observations.Recorded);
        Assert.Empty(world.Quarantine.Recorded);
    }

    // ---------- the world these tests run in ----------

    /// <summary>
    /// An archive, a run store, a store, a quarantine store and a claiming seam - and no provider.
    /// </summary>
    private sealed class World
    {
        private readonly Dictionary<string, int> _admits = new(StringComparer.Ordinal);
        private int _defaultAdmits;
        private (string RuleId, string Reason)? _refusal;

        public World()
        {
            Archive = new RecordingArchive();
            Runs = new RecordingRunStore();
            Observations = new RecordingObservationStore();
            Quarantine = new RecordingQuarantineStore();
            Gateway = new ClaimingActionGateway();
            Normalizer = new SpyNormalizer(Answer);

            var pipeline = new NormalizationPipeline(
                Archive, [Normalizer], Observations, Quarantine, Gateway, new FixedClock(Now));

            Replay = new ArchivedPayloadReplayService(Runs, Archive, pipeline);
        }

        public RecordingArchive Archive { get; }

        public RecordingRunStore Runs { get; }

        public RecordingObservationStore Observations { get; }

        public RecordingQuarantineStore Quarantine { get; }

        public ClaimingActionGateway Gateway { get; }

        public SpyNormalizer Normalizer { get; }

        /// <summary>
        /// Held as the concrete type: the analyser objects to an interface-typed field that only
        /// ever holds one implementation, and the reflection assertions above examine the
        /// interface directly anyway.
        /// </summary>
        public ArchivedPayloadReplayService Replay { get; }

        /// <summary>Every payload yields this many observations.</summary>
        public void Admits(int observations) => _defaultAdmits = observations;

        /// <summary>Each named payload yields its own count.</summary>
        public void AdmitsPerPayload(IEnumerable<(ContentHash Hash, int Observations)> counts)
        {
            foreach (var (hash, observations) in counts)
            {
                _admits[hash.Value] = observations;
            }
        }

        /// <summary>Every payload is refused whole, as it was when first read.</summary>
        public void Refuses(string ruleId, string reason) => _refusal = (ruleId, reason);

        public IngestionRun SeedRun(
            string symbol,
            string document,
            bool succeeded = true,
            DateTime? startedAtUtc = null)
        {
            var started = startedAtUtc ?? Now;

            var request = IngestionRequest.Create(
                PriceSource,
                Prices,
                Region.UnitedStates,
                IngestionSubject.Create("Security", symbol),
                CorrelationId.New(),
                started);

            var run = IngestionRun.Start(request, started);

            run.RecordArtifact(Archive.Seed(Encoding.UTF8.GetBytes(document), PriceSource, Retrieved));

            if (succeeded)
            {
                run.MarkSucceeded(started);
            }
            else
            {
                run.MarkFailed("the provider did not answer in full", started);
            }

            Runs.Recorded.Add(run);

            return run;
        }

        /// <summary>
        /// The quarantine record the original normalisation left behind, seeded directly.
        /// </summary>
        public QuarantinedPayload SeedHistoricalQuarantine(IngestionRun run, string ruleId, string reason)
        {
            var record = QuarantinedPayload.Record(
                run.Artifacts[0],
                PriceSource,
                Prices,
                ruleId,
                reason,
                Retrieved);

            Quarantine.Recorded.Add(record);

            return record;
        }

        private NormalizationResult Answer(NormalizationInput input)
        {
            if (_refusal is { } refusal)
            {
                return NormalizationResult.Quarantine(refusal.RuleId, refusal.Reason);
            }

            var count = _admits.TryGetValue(input.ContentHash.Value, out var named)
                ? named
                : _defaultAdmits;

            if (count == 0)
            {
                return NormalizationResult.Quarantine(
                    "market-data.empty-series@1",
                    "The document is an empty array.");
            }

            var observations = Enumerable
                .Range(0, count)
                .Select(i => Observation.RecordFact(
                    input.Subject,
                    "market.close",
                    ObservationValue.Number(100m + i),
                    Provenance.Create(
                        input.SourceId,
                        input.RetrievedAtUtc.AddDays(-1),
                        input.RetrievedAtUtc.AddDays(-1),
                        input.RetrievedAtUtc)))
                .ToList();

            return NormalizationResult.Normalized(observations);
        }
    }

    /// <summary>
    /// A normaliser that answers on demand and remembers exactly what it was handed.
    /// </summary>
    /// <remarks>
    /// The provenance claims below are about what reached the normaliser, so recording the address,
    /// the bytes and the retrieval instant is the point rather than incidental bookkeeping.
    /// </remarks>
    private sealed class SpyNormalizer : INormalizer
    {
        private readonly Func<NormalizationInput, NormalizationResult> _answer;

        public SpyNormalizer(Func<NormalizationInput, NormalizationResult> answer) => _answer = answer;

        public int Calls { get; private set; }

        public List<ContentHash> Seen { get; } = [];

        public DateTime LastRetrievedAtUtc { get; private set; }

        public ReadOnlyMemory<byte> LastPayload { get; private set; }

        public bool CanNormalize(SourceId sourceId, DataCategory category) => true;

        public Task<NormalizationResult> NormalizeAsync(
            NormalizationInput input,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Seen.Add(input.ContentHash);
            LastRetrievedAtUtc = input.RetrievedAtUtc;
            LastPayload = input.Payload;

            return Task.FromResult(_answer(input));
        }
    }
}
