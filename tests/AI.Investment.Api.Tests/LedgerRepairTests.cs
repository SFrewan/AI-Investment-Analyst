using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Finishes the one ingestion the Block 2B backfill left half-done.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is being repaired.</strong> During the backfill, <c>AAPL.US</c> prices were fetched
/// and archived, and then the ledger write threw - the change tracker had already been poisoned by
/// a shared owned-entity instance. The action seam had claimed the idempotency key before the
/// effect ran, so the end state was a claim with no run behind it: the request can never be
/// re-fetched, because the seam suppresses it as a duplicate, and was never recorded, because
/// nothing re-ran it.
/// </para>
/// <para>
/// <strong>Why the repair goes forward rather than back.</strong> The claim cannot be released. The
/// write guard refuses to delete a <c>ProcessedAction</c> unconditionally - authorised or not - and
/// that is correct: an idempotency ledger that can be edited is not one. So the only honest repair
/// is to finish the operation that was interrupted, from the bytes it had already archived. That
/// costs no provider call and deletes nothing.
/// </para>
/// <para>
/// <strong>It refuses rather than guesses.</strong> Every step is checked before the next: the
/// request really is stuck, exactly one archived payload really is orphaned, and it really came
/// from the price connector. If any of those is not so, the repair stops and says which - because a
/// ledger row invented for a fetch that did not happen is worse than the gap it would close.
/// </para>
/// <para>
/// Gated on <c>AIINV_REPAIR=1</c>. It writes to the real database and is not part of an ordinary
/// suite run.
/// </para>
/// </remarks>
public sealed class LedgerRepairTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_REPAIR";

    /// <summary>The request the backfill made, rebuilt exactly - the fingerprint depends on it.</summary>
    private const string Symbol = "AAPL.US";

    private const string Correlation = "backfill-MarketPrices-AAPL-US-20240831-20260831";

    private static readonly DateTime WindowStart = new(2024, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public LedgerRepairTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_stranded_price_ingestion_is_finished_from_its_archived_payload()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Ledger repair is off. Set {GateVariable}=1 to run it. It writes to the real database.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var clock = services.GetRequiredService<IClock>();
        var runs = services.GetRequiredService<IIngestionRunStore>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var settings = services.GetRequiredService<AI.Investment.Application.Opportunities.DiscoverySettings>();
        var sourceId = SourceId.Create(settings.PriceSourceId);

        var request = IngestionRequest.Create(
            sourceId,
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create("Security", Symbol),
            CorrelationId.Create(Correlation),
            clock.UtcNow,
            DateRange.Create(WindowStart, WindowEnd));

        var fingerprint = request.Fingerprint();
        var key = string.Create(CultureInfo.InvariantCulture, $"{fingerprint}:{request.CorrelationId}");

        Line(report, "# Block 2B - ledger repair");
        Line(report, string.Empty);
        Line(report, Inv($"Run at {clock.UtcNow:yyyy-MM-dd HH:mm:ss}Z. No provider call is made."));
        Line(report, string.Empty);
        Line(report, Inv($"- request fingerprint: `{fingerprint}`"));
        Line(report, Inv($"- idempotency key: `{key}`"));

        // ---- 1. is it actually stuck -------------------------------------

        var context = services.GetRequiredService<AppDbContext>();

        var claimed = await context.Set<ProcessedAction>()
            .AsNoTracking()
            .AnyAsync(p => p.IdempotencyKey == key);

        var completed = await runs.HasCompletedAsync(fingerprint);

        Line(report, string.Empty);
        Line(report, "## Diagnosis");
        Line(report, string.Empty);
        Line(report, Inv($"- idempotency claim present: {claimed}"));
        Line(report, Inv($"- completed run in the ledger: {completed}"));

        if (completed)
        {
            Line(report, string.Empty);
            Line(report, "**Nothing to repair.** The ledger already records this request as complete.");

            await WriteAsync(report);
            _output.WriteLine(report.ToString());

            return;
        }

        Assert.True(
            claimed,
            "This request holds no idempotency claim, so it is not in the stranded state this "
            + "repair exists for. Re-running the backfill would fetch it normally. Nothing was changed.");

        // ---- 2. find the orphaned payload --------------------------------

        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var run in await runs.GetRecentAsync(DateTime.UnixEpoch, int.MaxValue))
        {
            foreach (var hash in run.Artifacts)
            {
                referenced.Add(hash.Value);
            }
        }

        var orphans = new List<(ContentHash Hash, ArchivedPayload Payload)>();

        await foreach (var hash in archive.EnumerateAsync())
        {
            if (referenced.Contains(hash.Value))
            {
                continue;
            }

            var described = await archive.DescribeAsync(hash);

            if (described is not null && described.SourceId == sourceId)
            {
                orphans.Add((hash, described));
            }
        }

        Line(report, string.Empty);
        Line(report, "## Orphaned payloads from the price connector");
        Line(report, string.Empty);

        foreach (var (hash, payload) in orphans)
        {
            Line(report, Inv(
                $"- `{hash.Value[..16]}` {payload.ByteLength} bytes, retrieved {payload.RetrievedAtUtc:yyyy-MM-dd HH:mm:ss}Z"));
        }

        Assert.True(
            orphans.Count == 1,
            "Expected exactly one archived price payload that no run references - the one the "
            + $"stranded fetch left behind. Found {orphans.Count}. The repair stops rather than "
            + "guessing which bytes belong to this request. Nothing was changed.");

        var (artifact, described2) = orphans[0];

        // ---- 3. finish the run -------------------------------------------

        // Stamped at the moment the bytes were actually retrieved, not now. The ledger should say
        // when the fetch happened; a repair that dated it today would misreport the history it is
        // supposed to be restoring.
        var repaired = IngestionRun.Start(request, described2.RetrievedAtUtc);

        repaired.RecordArtifact(artifact);
        repaired.MarkSucceeded(described2.RetrievedAtUtc);

        await runs.RecordAsync(repaired);

        // ---- 4. and normalise what it fetched ------------------------------

        // The point of the repair. A Succeeded row with archived bytes that were never read would
        // clear the duplicate check while leaving the instrument empty - an instrument that looks
        // ingested and is not is a worse end state than the one being repaired.
        var summary = await services
            .GetRequiredService<INormalizationPipeline>()
            .NormalizeAsync(repaired);

        Line(report, string.Empty);
        Line(report, "## Repair");
        Line(report, string.Empty);
        Line(report, Inv($"- run recorded, started and completed {described2.RetrievedAtUtc:yyyy-MM-dd HH:mm:ss}Z"));
        Line(report, Inv($"- artifact: `{artifact.Value}`"));
        Line(report, Inv($"- payloads read: {summary.PayloadsRead}"));
        Line(report, Inv($"- observations recorded: {summary.ObservationsRecorded}"));
        Line(report, Inv($"- payloads quarantined: {summary.PayloadsQuarantined}"));

        // ---- 5. prove the state is consistent -----------------------------

        var nowCompleted = await runs.HasCompletedAsync(fingerprint);

        Line(report, string.Empty);
        Line(report, "## After");
        Line(report, string.Empty);
        Line(report, Inv($"- completed run in the ledger: {nowCompleted}"));
        Line(report, Inv($"- idempotency claim present: {claimed} (unchanged - nothing was deleted)"));

        await WriteAsync(report);
        _output.WriteLine(report.ToString());

        Assert.True(nowCompleted, "The repair did not leave a completed run in the ledger.");
        Assert.False(summary.HadFailures, "The archived payload could not be normalised.");
    }

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(StringBuilder report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "ledger-repair.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
