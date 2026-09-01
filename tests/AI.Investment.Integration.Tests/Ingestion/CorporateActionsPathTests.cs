using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The corporate-actions path proved end to end against known splits, without a live call.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The whole chain, not a link of it.</strong> Each test below drives the real connector
/// (real URL, real response handling), the real normaliser, the real archive, the real ingestion
/// ledger, real PostgreSQL, the real point-in-time read, and the real split adjustment. Only the
/// HTTP transport is a fixture.
/// </para>
/// <para>
/// <strong>The split events are real; the closes are constructed.</strong> NVIDIA's ten-for-one on
/// 10 June 2024 and Apple's four-for-one on 31 August 2020 are matters of record, and they are the
/// events being modelled - a fixture invented out of nothing would prove the code agreed with
/// itself. The surrounding closes are chosen round numbers at the right magnitudes, because the
/// property under test is whether a ninety per cent step is restated or read as a collapse, and
/// that does not depend on the fourth decimal place of a real print.
/// </para>
/// <para>
/// This matters because it is the one failure in this platform that produces a confident wrong
/// number rather than a refusal. Everything else that goes wrong here declines to answer.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class CorporateActionsPathTests : IAsyncLifetime
{
    /// <summary>After every session below is published, so the reads see a settled world.</summary>
    private static readonly DateTime Now = new(2024, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>NVIDIA's ten-for-one, effective 10 June 2024, in the vendor's wire format.</summary>
    private const string NvidiaSplit =
        """[{"date":"2024-06-10","split":"10.000000/1.000000"}]""";

    /// <summary>
    /// Four sessions across that split: about 1,200 before and about 120 after.
    /// </summary>
    /// <remarks>
    /// Raw, this is a ninety per cent fall in one session - the deepest drawdown the price-recovery
    /// screen would ever have seen, and entirely fictional.
    /// </remarks>
    private const string NvidiaPrices =
        """
        [{"date":"2024-06-06","open":1205.0,"high":1215.0,"low":1200.0,"close":1210.0,"adjusted_close":121.0,"volume":300000},
         {"date":"2024-06-07","open":1210.0,"high":1212.0,"low":1201.0,"close":1208.0,"adjusted_close":120.8,"volume":310000},
         {"date":"2024-06-10","open":120.0,"high":121.5,"low":119.5,"close":120.9,"adjusted_close":120.9,"volume":2900000},
         {"date":"2024-06-11","open":121.0,"high":122.0,"low":120.5,"close":121.8,"adjusted_close":121.8,"volume":2700000}]
        """;

    /// <summary>Restated into the shares in issue at the end of the window.</summary>
    private static readonly decimal[] RestatedByTen = [121.0m, 120.8m, 120.9m, 121.8m];

    /// <summary>What the two pre-split sessions were actually quoted at, at the time.</summary>
    private static readonly decimal[] AsQuotedBeforeTheSplit = [1210.0m, 1208.0m];

    /// <summary>
    /// Only the first session, because the second had not been published yet.
    /// </summary>
    /// <remarks>
    /// A close is stamped at the session it describes and published four hours later - the delay
    /// this installation states for the exchange. So at 21:00 on the 7th the 7th's own close is not
    /// yet something anybody could have acted on, and the point-in-time read must not show it.
    /// </remarks>
    private static readonly decimal[] PublishedByTheSeventhEvening = [1210.0m];

    private const string AppleSplit =
        """[{"date":"2020-08-31","split":"4.000000/1.000000"}]""";

    private const string ApplePrices =
        """
        [{"date":"2020-08-27","open":502.0,"high":508.0,"low":500.0,"close":506.0,"adjusted_close":126.5,"volume":200000},
         {"date":"2020-08-28","open":506.0,"high":510.0,"low":504.0,"close":508.0,"adjusted_close":127.0,"volume":210000},
         {"date":"2020-08-31","open":127.0,"high":129.0,"low":126.0,"close":128.0,"adjusted_close":128.0,"volume":900000},
         {"date":"2020-09-01","open":128.0,"high":134.0,"low":128.0,"close":133.0,"adjusted_close":133.0,"volume":950000}]
        """;

    private static readonly decimal[] RestatedByFour = [126.5m, 127.0m, 128.0m, 133.0m];

    private readonly PostgresFixture _fixture;

    public CorporateActionsPathTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// <strong>The whole path.</strong> A ten-for-one is restated rather than screened as a collapse.
    /// </summary>
    /// <remarks>
    /// Three states are asserted in one test on purpose, because each is only meaningful beside the
    /// others: refused without the corporate action, restated with it, and - reading as at a date
    /// before the split - shown in the shares that were actually in issue then.
    /// </remarks>
    [SkippableFact]
    public async Task A_known_ten_for_one_split_is_restated_through_the_whole_path()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        using var harness = await AcquisitionHarness.StartAsync(_fixture, Now);

        harness.Handler
            .WithPrices("NVDA.US", NvidiaPrices)
            .WithSplits("NVDA.US", NvidiaSplit);

        // 1. Prices only. The step has no explanation, so the platform declines to screen it.
        var prices = await harness.Acquisition.AcquireAsync(PriceRequest("NVDA.US"));

        Assert.Equal(IngestionOutcome.Succeeded, prices.Run.Outcome);
        Assert.Equal(4, prices.ObservationsRecorded);

        var withoutTheSplit = await ReadAsync(harness, "NVDA.US", Now);

        Assert.False(withoutTheSplit.IsUsable);
        Assert.Equal(SeriesRefusal.UnexplainedDiscontinuity, withoutTheSplit.Refusal);

        // 2. The corporate action, through the same gateway, ledger and archive as the prices.
        var splits = await harness.Acquisition.AcquireAsync(SplitRequest("NVDA.US"));

        Assert.Equal(IngestionOutcome.Succeeded, splits.Run.Outcome);
        Assert.Equal(1, splits.ObservationsRecorded);

        var withTheSplit = await ReadAsync(harness, "NVDA.US", Now);

        Assert.True(withTheSplit.IsUsable);
        Assert.Equal(RestatedByTen, withTheSplit.Observations.Select(o => o.Close).ToArray());

        // 3. Point in time, and it has two halves.
        //
        //    First, publication. Asked at nine on the evening of the 7th, the platform must not
        //    show the 7th's own close: it is stamped at the session and becomes public four hours
        //    after it, so nobody could have acted on it yet.
        var asAtTheSeventhEvening = await ReadAsync(
            harness,
            "NVDA.US",
            new DateTime(2024, 6, 7, 21, 0, 0, DateTimeKind.Utc));

        Assert.True(asAtTheSeventhEvening.IsUsable);

        Assert.Equal(
            PublishedByTheSeventhEvening,
            asAtTheSeventhEvening.Observations.Select(o => o.Close).ToArray());

        //    Second, restatement. Asked on the 8th, both closes are public and the platform must
        //    answer in the shares that were in issue then - a split two days in its future cannot
        //    reach back and restate a history nobody had restated yet. This is what makes a replay
        //    honest, and what stops a backtest scoring a series the market had not yet seen.
        var asAtTheEighth = await ReadAsync(
            harness,
            "NVDA.US",
            new DateTime(2024, 6, 8, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(asAtTheEighth.IsUsable);

        Assert.Equal(
            AsQuotedBeforeTheSplit,
            asAtTheEighth.Observations.Select(o => o.Close).ToArray());
    }

    /// <summary>
    /// A second known split, and the provenance the restated series rests on.
    /// </summary>
    /// <remarks>
    /// The restatement is only trustworthy if what it was derived from is still there to look at.
    /// This asserts the three things an audit would ask for: the vendor's bytes in the archive, the
    /// run in the ledger carrying that same content hash, and an observation whose citation resolves
    /// back to a stored row.
    /// </remarks>
    [SkippableFact]
    public async Task A_known_four_for_one_split_leaves_the_provenance_behind_it()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        using var harness = await AcquisitionHarness.StartAsync(_fixture, Now);

        harness.Handler
            .WithPrices("AAPL.US", ApplePrices)
            .WithSplits("AAPL.US", AppleSplit);

        _ = await harness.Acquisition.AcquireAsync(PriceRequest("AAPL.US"));

        var splits = await harness.Acquisition.AcquireAsync(SplitRequest("AAPL.US"));

        // The run names the payload it read.
        var artifact = Assert.Single(splits.Run.Artifacts);

        // And the payload is still on disk, byte for byte.
        var stored = await harness.Archive.RetrieveAsync(artifact);

        Assert.NotNull(stored);
        Assert.Equal(AppleSplit, System.Text.Encoding.UTF8.GetString(stored!));

        // The ledger agrees the request is done, which is what makes a rerun free.
        Assert.True(await harness.Runs.HasCompletedAsync(SplitRequest("AAPL.US").Fingerprint()));

        // And the split the screen reads is stamped at the effective session's close, not midnight.
        var observations = await harness.Observations.ForSubjectAsync(Subject("AAPL.US"), Now);

        var split = Assert.Single(
            observations,
            o => string.Equals(o.Attribute, EodhdSplitsNormalizer.SplitAttribute, StringComparison.Ordinal));

        Assert.Equal(4m, split.Value.AsNumber());
        Assert.Equal(new DateTime(2020, 8, 31, 20, 0, 0, DateTimeKind.Utc), split.Provenance.AsOfUtc);

        var series = await ReadAsync(harness, "AAPL.US", Now);

        Assert.True(series.IsUsable);
        Assert.Equal(RestatedByFour, series.Observations.Select(o => o.Close).ToArray());
    }

    /// <summary>
    /// An instrument that has never split records a completed run and no observations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the whole live universe returned during the Block 2B backfill: twenty calls,
    /// twenty successes, one two-byte body. That is not a failure, and the distinction is worth a
    /// test of its own - "the vendor says there were no splits" and "the normaliser dropped what it
    /// was given" produce the same empty attribute and mean opposite things.
    /// </para>
    /// <para>
    /// So the assertions are about what is left behind rather than about what is absent: a
    /// <c>Succeeded</c> run, the empty document archived and citable, and nothing in quarantine.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_empty_corporate_actions_answer_is_a_completed_run_not_a_silent_failure()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        using var harness = await AcquisitionHarness.StartAsync(_fixture, Now);

        harness.Handler
            .WithPrices("KO.US", ApplePrices)
            .WithSplits("KO.US", "[]");

        var splits = await harness.Acquisition.AcquireAsync(SplitRequest("KO.US"));

        Assert.Equal(IngestionOutcome.Succeeded, splits.Run.Outcome);
        Assert.Equal(0, splits.ObservationsRecorded);

        // The call was made and the answer was kept, which is how "no splits" stays checkable.
        Assert.Equal(1, harness.Handler.SplitCalls);

        var artifact = Assert.Single(splits.Run.Artifacts);
        var stored = await harness.Archive.RetrieveAsync(artifact);

        Assert.NotNull(stored);
        Assert.Equal("[]", System.Text.Encoding.UTF8.GetString(stored!));

        // An empty answer is not a data-quality problem, so nothing is quarantined.
        Assert.Equal(0, await harness.Context.QuarantinedPayloads.AsNoTracking().CountAsync());

        Assert.NotNull(splits.Normalization);
        Assert.False(splits.Normalization!.HadFailures);
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<AdjustedPriceSeries> ReadAsync(
        AcquisitionHarness harness,
        string symbol,
        DateTime asAtUtc) =>
        new PriceSeriesReader(harness.Observations).ReadAdjustedAsync(
            Subject(symbol),
            EodhdDailyPriceNormalizer.CloseAttribute,
            EodhdSplitsNormalizer.SplitAttribute,
            120,
            asAtUtc,
            SplitAdjustment.DefaultMaxUnexplainedMove);

    private static IngestionSubject Subject(string symbol) =>
        IngestionSubject.Create(EodhdProvider.SecurityKind, symbol);

    private static IngestionRequest PriceRequest(string symbol) =>
        IngestionRequest.Create(
            EodhdProvider.Id,
            DataCategory.MarketPrices,
            Region.Global,
            Subject(symbol),
            CorrelationId.Create("corp-actions-prices-" + symbol.Replace('.', '-')),
            Now);

    private static IngestionRequest SplitRequest(string symbol) =>
        IngestionRequest.Create(
            EodhdSplitsProvider.Id,
            DataCategory.CorporateActions,
            Region.Global,
            Subject(symbol),
            CorrelationId.Create("corp-actions-splits-" + symbol.Replace('.', '-')),
            Now);
}
