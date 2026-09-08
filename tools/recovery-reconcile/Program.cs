using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Infrastructure;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Investment.Tools.RecoveryReconcile;

/// <summary>
/// The host environment the production policy provider reads its environment name from.
/// </summary>
internal sealed class ToolEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "recovery-reconcile";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>
/// READ-ONLY final reconciliation of the five-member archived-price recovery.
/// </summary>
/// <remarks>
/// <para>
/// This tool is read-only BY CONSTRUCTION, not by discipline. It never resolves
/// <c>IArchivedPayloadReplay</c> and does not reference <c>ArchivedPayloadReplayService</c> at all,
/// so there is no code path from here to a replay, and none to a provider: both connectors are
/// pinned off and every query below is a SELECT.
/// </para>
/// <para>
/// It re-measures the live database rather than re-reading the five recovery records. Re-reading my
/// own reports would only prove that I can read them; the point of a reconciliation is to ask the
/// store what it currently holds and check that against what was claimed.
/// </para>
/// </remarks>
internal static class Program
{
    private const string RequiredDatabase = "ai_investment";
    private const string ForbiddenDatabase = "ai_investment_tests";
    private const string PriceSource = "eodhd-eod";
    private const string PriceCategory = "MarketPrices";

    /// <summary>The 2-byte empty array, and the number of runs the preflight found holding it.</summary>
    private const string EmptyPayloadHash = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";
    private const int ExpectedEmptyPayloadRuns = 324;

    private static readonly string[] EmptyMembers = ["BPYU.US", "KIN.US", "LBRA.US", "MSOF.US", "USCR.US"];

    /// <summary>Baseline counts as they stood immediately before the CCF replay, 2026-09-08.</summary>
    private const long BaseObservations = 1_074_440;
    private const long BaseIngestionRuns = 2_274;
    private const long BaseProcessedActions = 3_732;
    private const long BaseActionExecutions = 3_731;
    private const long BaseQuarantinedPayloads = 50;

    private const long ExpectedTotalRecovered = 2_940;

    /// <summary>
    /// The five, with everything the five recovery records claimed about them. Every field here is
    /// an EXPECTATION to be checked against the live store, not a value to be reported back.
    /// </summary>
    private static readonly Member[] Five =
    [
        new("CCF.US",  "ae79be18dc5534772905b3d184472916a45d0446b090b0f29af64237086dd679", "45d3fe5c-69ca-48a8-a7b2-4ad4dd2c8552", 63822, "2026-09-04T22:46:43.1521719Z", 556, 557, "2026-09-04T22:46:48.8891290Z"),
        new("EVBG.US", "438650fad031491550ee2f1c2d673d6da4a716d03cd2ab74f1124ce6212d270d", "e2f77177-4827-43bf-8951-2f56a28c0f2e", 80661, "2026-09-04T23:10:16.1554059Z", 711, 712, "2026-09-04T23:10:24.5723020Z"),
        new("GPP.US",  "626806275c359cc70dc7bdc9021375b4109556abeefca6190d2b468c4c487fc0", "4c1bc92f-ade8-4535-8322-053e1dcafbbb", 67667, "2026-09-04T23:24:02.2613270Z", 592, 593, "2026-09-04T23:24:13.6749640Z"),
        new("VLDR.US", "a2b139e5e2c068b1c031c5ed0ca233550fdd9787f73f4b1fa63ada98771ac711", "e4097a2d-1db7-4c85-9388-6b45cad106c4", 40824, "2026-09-05T03:03:11.9247831Z", 369, 370, "2026-09-05T03:03:14.2750090Z"),
        new("WIRE.US", "6d3c18170963aab334c176d8bbfee4927b93d28429a0d150479d907469578a2f", "bc0eb767-473d-481d-ad3d-d9d9bc491c50", 84953, "2026-09-05T03:04:25.5886852Z", 712, 713, "2026-09-05T03:04:29.3628690Z"),
    ];

    private sealed record Member(
        string Symbol, string Hash, string RunId, long Bytes, string Retrieved,
        int ExpectedObservations, int QuarantineRow, string QuarantinedAtUtc);

    private const long TicksPerMicrosecond = 10L;

    /// <summary>The same instant at the precision the database column actually has (1 us).</summary>
    private static DateTime ToPersistencePrecision(DateTime instant) =>
        new(instant.Ticks - (instant.Ticks % TicksPerMicrosecond), instant.Kind);

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   FIVE-MEMBER RECOVERY - FINAL RECONCILIATION  (READ ONLY)");
        Console.WriteLine("   No replay. No provider. No acquisition. No write of any kind.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        var raw = Environment.GetEnvironmentVariable("AIINV_TEST_POSTGRES");

        if (string.IsNullOrWhiteSpace(raw))
        {
            Console.WriteLine("  STOPPING. AIINV_TEST_POSTGRES is not set.");
            return 3;
        }

        var archiveRoot = Path.Combine(root, "tests", "AI.Investment.Api.Tests", "bin", "Release", "net8.0", "archive");

        if (!Directory.Exists(archiveRoot))
        {
            Console.WriteLine("  STOPPING. The archive root does not exist: " + archiveRoot);
            return 4;
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(raw);

        Console.WriteLine("--- connection (password never printed)");
        Console.WriteLine("  host               : " + builder.Host);
        Console.WriteLine("  port               : " + builder.Port);
        Console.WriteLine("  configured database: " + builder.Database);

        if (!string.Equals(builder.Database, RequiredDatabase, StringComparison.Ordinal))
        {
            Console.WriteLine("  switching to       : " + RequiredDatabase);
            builder.Database = RequiredDatabase;
        }

        if (string.Equals(builder.Database, ForbiddenDatabase, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  STOPPING. The resolved database is " + ForbiddenDatabase + ".");
            return 5;
        }

        Console.WriteLine("  archive root       : " + archiveRoot);
        Console.WriteLine();

        var apiDirectory = Path.Combine(root, "src", "AI.Investment.Api");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = builder.ConnectionString,
                ["RawArchive:RootPath"] = archiveRoot,
                ["Providers:Eodhd:Enabled"] = "false",
                ["Providers:SecEdgar:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new ToolEnvironment { ContentRootPath = apiDirectory });
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication();
        services.AddInfrastructure(configuration);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var context = sp.GetRequiredService<AppDbContext>();
        var lookup = sp.GetRequiredService<IArchivedRunLookup>();
        var archive = sp.GetRequiredService<IRawResponseArchive>();

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["Mode"] = "READ ONLY - no replay service resolved, no write path exists in this tool",
            ["ArchiveRoot"] = archiveRoot,
            ["Host"] = builder.Host,
            ["Port"] = builder.Port,
        };

        var checks = new List<Dictionary<string, object?>>();

        // ---- live identity ----
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string liveDatabase;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT current_database()";
            liveDatabase = Convert.ToString(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) ?? "";
        }

        Console.WriteLine("--- live database identity");
        Console.WriteLine("  current_database() : " + liveDatabase);
        Console.WriteLine("  server             : " + connection.ServerVersion);
        Console.WriteLine();

        report["LiveDatabase"] = liveDatabase;
        report["ServerVersion"] = connection.ServerVersion;

        if (!string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal))
        {
            Console.WriteLine("  STOPPING. The LIVE database is '" + liveDatabase + "'.");
            return 5;
        }

        Check(checks, "0. live database is ai_investment", true, liveDatabase);

        // ================= WHOLE-DATABASE COUNTS =================
        var counts = await CountsAsync(context).ConfigureAwait(false);

        report["Current"] = counts;
        report["Baseline"] = new Dictionary<string, long>
        {
            ["observations"] = BaseObservations,
            ["ingestion_runs"] = BaseIngestionRuns,
            ["processed_actions"] = BaseProcessedActions,
            ["action_executions"] = BaseActionExecutions,
            ["quarantined_payloads"] = BaseQuarantinedPayloads,
        };

        var deltas = new Dictionary<string, long>
        {
            ["observations"] = counts["observations"] - BaseObservations,
            ["ingestion_runs"] = counts["ingestion_runs"] - BaseIngestionRuns,
            ["processed_actions"] = counts["processed_actions"] - BaseProcessedActions,
            ["action_executions"] = counts["action_executions"] - BaseActionExecutions,
            ["quarantined_payloads"] = counts["quarantined_payloads"] - BaseQuarantinedPayloads,
        };

        report["Deltas"] = deltas;

        Console.WriteLine("--- whole-database counts (baseline -> current)");

        foreach (var key in new[] { "observations", "ingestion_runs", "processed_actions", "action_executions", "quarantined_payloads" })
        {
            Console.WriteLine("  " + key.PadRight(22)
                + report_BaseOf(key).ToString(CultureInfo.InvariantCulture).PadLeft(10)
                + " -> " + counts[key].ToString(CultureInfo.InvariantCulture).PadLeft(10)
                + "   delta " + (deltas[key] >= 0 ? "+" : "") + deltas[key].ToString(CultureInfo.InvariantCulture));
        }

        Console.WriteLine();

        Check(checks, "2. no new ingestion runs were created", deltas["ingestion_runs"] == 0,
            BaseIngestionRuns + " -> " + counts["ingestion_runs"]);

        Check(checks, "3. exactly 2,940 observations were added", deltas["observations"] == ExpectedTotalRecovered,
            deltas["observations"].ToString(CultureInfo.InvariantCulture));

        Check(checks, "6. processed_actions increased exactly +5", deltas["processed_actions"] == 5,
            (deltas["processed_actions"] >= 0 ? "+" : "") + deltas["processed_actions"]);

        Check(checks, "7. action_executions increased exactly +5", deltas["action_executions"] == 5,
            (deltas["action_executions"] >= 0 ? "+" : "") + deltas["action_executions"]);

        Check(checks, "8a. quarantined_payloads unchanged", deltas["quarantined_payloads"] == 0,
            (deltas["quarantined_payloads"] >= 0 ? "+" : "") + deltas["quarantined_payloads"]);

        // A second, independent angle on "no new ingestion runs": ask the table for anything created
        // on or after the recovery day, rather than trusting a count that could coincidentally match.
        var recentRuns = await RunsCreatedOnOrAfterAsync(connection, new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc)).ConfigureAwait(false);

        report["IngestionRunsCreatedOnOrAfter20260908"] = recentRuns.Available ? recentRuns.Count : null;
        report["IngestionRunsTimestampColumn"] = recentRuns.Column;

        if (recentRuns.Available)
        {
            Check(checks, "2b. no ingestion run carries a 2026-09-08 or later timestamp",
                recentRuns.Count == 0,
                recentRuns.Count + " via " + recentRuns.Column);
        }
        else
        {
            Console.WriteLine("  NOTE  no timestamp column found on ingestion_runs; check 2b not available.");
            Console.WriteLine();
        }

        // ================= PER-MEMBER =================
        Console.WriteLine("--- the five members, re-measured against the live store");
        Console.WriteLine();

        var members = new List<Dictionary<string, object?>>();
        long observedTotal = 0;
        var allMembersClean = true;

        var tolerance = ReadInteriorGapTolerance(root);

        foreach (var m in Five)
        {
            var contentHash = ContentHash.Create(m.Hash);

            // Subject.Identifier is the BARE symbol; the "Security:" form is Kind + ":" + Identifier,
            // a display shape. Querying observations by the display shape matches nothing.
            var subject = m.Symbol;

            var runs = await lookup.RunsForArchivedPayloadAsync(contentHash).ConfigureAwait(false);
            var run = runs.Count == 1 ? runs[0] : null;

            var runIdOk = run is not null && string.Equals(run.Id.ToString(), m.RunId, StringComparison.OrdinalIgnoreCase);
            var subjectOk = run is not null && string.Equals(run.Request.Subject.Identifier, m.Symbol, StringComparison.Ordinal);
            var sourceOk = run is not null && string.Equals(run.Request.SourceId.Value, PriceSource, StringComparison.Ordinal);
            var categoryOk = run is not null && string.Equals(run.Request.Category.ToString(), PriceCategory, StringComparison.Ordinal);
            var artifactOk = run is not null && run.Artifacts.Count == 1
                && string.Equals(run.Artifacts[0].Value, m.Hash, StringComparison.OrdinalIgnoreCase);

            // The bytes on disk, hashed again.
            var payloadPath = Path.Combine(archiveRoot, m.Hash[..2], m.Hash.Substring(2, 2), m.Hash + ".bin");
            var recomputed = File.Exists(payloadPath)
                ? ContentHash.Compute(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false)).Value
                : "(missing)";
            var hashOk = string.Equals(recomputed, m.Hash, StringComparison.OrdinalIgnoreCase);

            var described = await archive.DescribeAsync(contentHash).ConfigureAwait(false);
            var sidecar = described?.RetrievedAtUtc;
            var bytesOk = described is not null && described.ByteLength == m.Bytes;

            var expectedRetrieved = DateTime.Parse(m.Retrieved, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var sidecarOk = sidecar.HasValue
                && ToPersistencePrecision(sidecar.Value) == ToPersistencePrecision(expectedRetrieved);

            // The stored series.
            var stored = await context.Observations.AsNoTracking()
                .Where(o => o.Subject.Identifier == subject)
                .Where(o => o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute)
                .ToListAsync().ConfigureAwait(false);

            var countOk = stored.Count == m.ExpectedObservations;
            observedTotal += stored.Count;

            var distinct = stored.Select(o => o.Provenance.RetrievedAtUtc).Distinct().OrderBy(d => d).ToList();
            var singleRetrieved = stored.Count > 0 && distinct.Count == 1;
            var provenanceOk = singleRetrieved && sidecar.HasValue
                && ToPersistencePrecision(distinct[0]) == ToPersistencePrecision(sidecar.Value);

            var duplicateGroups = stored
                .GroupBy(o => string.Join('|',
                    o.Subject.Kind,
                    o.Subject.Identifier ?? "",
                    o.Attribute,
                    o.Provenance.AsOfUtc.ToString("O", CultureInfo.InvariantCulture),
                    o.Provenance.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    o.Value.Canonical))
                .Count(g => g.Count() > 1);

            // The historical quarantine record.
            var quarantine = await context.QuarantinedPayloads.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == contentHash).ConfigureAwait(false);

            var expectedQuarantinedAt = DateTime.Parse(m.QuarantinedAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

            var quarantineOk = quarantine is not null
                && string.Equals(quarantine.RuleId, "market-data.unreadable-row@1", StringComparison.Ordinal)
                && quarantine.Reason.Contains("Row " + m.QuarantineRow + ":", StringComparison.Ordinal)
                && ToPersistencePrecision(quarantine.QuarantinedAtUtc) == ToPersistencePrecision(expectedQuarantinedAt);

            // The idempotency key.
            var key = "normalization.record:" + m.RunId;
            var claimed = await context.ProcessedActions.AsNoTracking()
                .AnyAsync(p => p.IdempotencyKey == key).ConfigureAwait(false);

            // ---- local Gate 6 projection for this member ----
            var sessions = stored.Select(o => o.Provenance.AsOfUtc).OrderBy(d => d).ToList();
            var worst = 0;
            var worstAt = "";

            for (var i = 1; i < sessions.Count; i++)
            {
                var missing = Weekdays(sessions[i - 1].AddDays(1), sessions[i].AddDays(-1));

                if (missing > worst)
                {
                    worst = missing;
                    worstAt = sessions[i - 1].ToString("yyyy-MM-dd") + " -> " + sessions[i].ToString("yyyy-MM-dd");
                }
            }

            var splitsHeld = await context.Observations.AsNoTracking()
                .CountAsync(o => o.Subject.Identifier == subject
                              && o.Attribute == EodhdSplitsNormalizer.SplitAttribute).ConfigureAwait(false);

            var prices = stored
                .OrderBy(o => o.Provenance.AsOfUtc)
                .Select(o => new ClosingPrice(o.Provenance.AsOfUtc,
                    decimal.Parse(o.Value.Canonical, CultureInfo.InvariantCulture)))
                .ToList();

            AdjustedSeries? adjusted = null;

            if (splitsHeld == 0 && prices.Count > 0)
            {
                adjusted = SplitAdjustment.Apply(prices, [], SplitAdjustment.DefaultMaxUnexplainedMove);
            }

            var faultZeroObservations = stored.Count == 0;
            var faultInteriorGap = worst > tolerance;
            var faultDiscontinuity = adjusted is not null && !adjusted.IsUsable;
            var clears = !faultZeroObservations && !faultInteriorGap && !faultDiscontinuity;

            var memberClean = runs.Count == 1 && runIdOk && subjectOk && sourceOk && categoryOk
                && artifactOk && hashOk && bytesOk && sidecarOk && countOk && singleRetrieved
                && provenanceOk && duplicateGroups == 0 && quarantineOk && claimed;

            allMembersClean &= memberClean;

            members.Add(new Dictionary<string, object?>
            {
                ["Symbol"] = m.Symbol,
                ["ExpectedRunId"] = m.RunId,
                ["RunsHoldingPayload"] = runs.Count,
                ["RunIdMatches"] = runIdOk,
                ["Subject"] = run is null ? null : run.Request.Subject.Kind + ":" + run.Request.Subject.Identifier,
                ["SubjectMatches"] = subjectOk,
                ["SourceMatches"] = sourceOk,
                ["CategoryMatches"] = categoryOk,
                ["ArtifactIsExpectedPayload"] = artifactOk,
                ["ContentHash"] = m.Hash,
                ["RecomputedContentHash"] = recomputed,
                ["ContentHashUnchanged"] = hashOk,
                ["PayloadByteLength"] = described?.ByteLength,
                ["ByteLengthMatches"] = bytesOk,
                ["SidecarRetrievedAtUtc"] = sidecar?.ToString("o"),
                ["SidecarMatchesRecovery"] = sidecarOk,
                ["ObservationsExpected"] = m.ExpectedObservations,
                ["ObservationsHeld"] = stored.Count,
                ["ObservationCountMatches"] = countOk,
                ["DistinctRetrievedAtValues"] = distinct.Select(d => d.ToString("o")).ToArray(),
                ["OneDistinctRetrievedAtUtc"] = singleRetrieved,
                ["StoredAtPersistencePrecision"] = distinct.Count > 0 ? ToPersistencePrecision(distinct[0]).ToString("o") : null,
                ["SidecarAtPersistencePrecision"] = sidecar.HasValue ? ToPersistencePrecision(sidecar.Value).ToString("o") : null,
                ["ProvenanceMatchesSidecarAtPersistencePrecision"] = provenanceOk,
                ["Gate12DuplicateGroups"] = duplicateGroups,
                ["QuarantineRuleId"] = quarantine?.RuleId,
                ["QuarantineAtUtc"] = quarantine?.QuarantinedAtUtc.ToString("o"),
                ["QuarantinePreservedUnchanged"] = quarantineOk,
                ["IdempotencyKey"] = key,
                ["IdempotencyKeyClaimed"] = claimed,
                ["CorrelationId"] = run?.Request.CorrelationId.ToString(),
                ["FirstSession"] = sessions.Count > 0 ? sessions[0].ToString("o") : null,
                ["LastSession"] = sessions.Count > 0 ? sessions[^1].ToString("o") : null,
                ["WorstInteriorGap"] = worst,
                ["WorstInteriorGapAt"] = worstAt,
                ["InteriorGapTolerance"] = tolerance,
                ["SplitObservationsHeld"] = splitsHeld,
                ["SplitAdjustmentUsable"] = adjusted?.IsUsable,
                ["SplitAdjustmentRefusal"] = adjusted?.Refusal.ToString(),
                ["FaultZeroObservations"] = faultZeroObservations,
                ["FaultInteriorGap"] = faultInteriorGap,
                ["FaultUnexplainedDiscontinuity"] = faultDiscontinuity,
                ["ClearsAllThreeFaultConditions"] = clears,
                ["AllChecksPassed"] = memberClean,
            });

            Console.WriteLine("  " + m.Symbol.PadRight(9)
                + "runs " + runs.Count
                + "  obs " + stored.Count.ToString(CultureInfo.InvariantCulture).PadLeft(4) + "/" + m.ExpectedObservations
                + "  dupGroups " + duplicateGroups
                + "  distinctRetrieved " + distinct.Count
                + "  provenance " + (provenanceOk ? "ok " : "NO ")
                + "  quarantine " + (quarantineOk ? "kept" : "CHANGED")
                + "  key " + (claimed ? "claimed" : "FREE")
                + "  clears " + clears
                + (memberClean ? "" : "   <== CHECK FAILED"));
        }

        report["Members"] = members;
        report["ObservationsHeldAcrossFive"] = observedTotal;

        Console.WriteLine();

        Check(checks, "1. the five original ingestion runs remain the identities used",
            members.All(x => (int)(x["RunsHoldingPayload"] ?? 0) == 1 && (bool)(x["RunIdMatches"] ?? false)),
            "5 payloads, 1 run each, all ids match");

        Check(checks, "4. per-member counts match (556/711/592/369/712)",
            members.All(x => (bool)(x["ObservationCountMatches"] ?? false)),
            string.Join("/", members.Select(x => x["ObservationsHeld"])));

        Check(checks, "4b. the five sum to 2,940 and equal the whole-database delta",
            observedTotal == ExpectedTotalRecovered && deltas["observations"] == ExpectedTotalRecovered,
            observedTotal + " held, " + deltas["observations"] + " added");

        Check(checks, "5a. zero Gate 12 duplicate groups on every member",
            members.All(x => (int)(x["Gate12DuplicateGroups"] ?? -1) == 0), "all zero");

        Check(checks, "5b. one distinct RetrievedAtUtc on every member",
            members.All(x => (bool)(x["OneDistinctRetrievedAtUtc"] ?? false)), "all single-valued");

        Check(checks, "5c. RetrievedAtUtc matches the archived sidecar at storage precision",
            members.All(x => (bool)(x["ProvenanceMatchesSidecarAtPersistencePrecision"] ?? false)), "all match at 1 us");

        Check(checks, "5d. original ContentHash still addresses the archived bytes",
            members.All(x => (bool)(x["ContentHashUnchanged"] ?? false)), "all recomputed equal");

        Check(checks, "5e. historical quarantine preserved unchanged on every member",
            members.All(x => (bool)(x["QuarantinePreservedUnchanged"] ?? false)), "all preserved");

        Check(checks, "5f. normalization idempotency key claimed on every member",
            members.All(x => (bool)(x["IdempotencyKeyClaimed"] ?? false)), "all claimed");

        // ================= THE FIVE [] MEMBERS =================
        Console.WriteLine("--- the five [] members (must be untouched)");

        var emptyHash = ContentHash.Create(EmptyPayloadHash);
        var emptyRuns = await lookup.RunsForArchivedPayloadAsync(emptyHash).ConfigureAwait(false);

        var emptyPath = Path.Combine(archiveRoot, EmptyPayloadHash[..2], EmptyPayloadHash.Substring(2, 2), EmptyPayloadHash + ".bin");
        var emptyBytes = File.Exists(emptyPath) ? await File.ReadAllBytesAsync(emptyPath).ConfigureAwait(false) : [];

        var emptyMemberRows = new List<Dictionary<string, object?>>();
        var allEmptyStillZero = true;

        foreach (var symbol in EmptyMembers)
        {
            var subject = symbol;   // bare identifier, as above

            var held = await context.Observations.AsNoTracking()
                .CountAsync(o => o.Subject.Identifier == subject
                              && o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute).ConfigureAwait(false);

            allEmptyStillZero &= held == 0;

            emptyMemberRows.Add(new Dictionary<string, object?>
            {
                ["Symbol"] = symbol,
                ["ObservationsHeld"] = held,
            });

            Console.WriteLine("  " + symbol.PadRight(9) + "observations " + held);
        }

        report["EmptyPayloadHash"] = EmptyPayloadHash;
        report["EmptyPayloadByteLength"] = emptyBytes.Length;
        report["EmptyPayloadContent"] = System.Text.Encoding.UTF8.GetString(emptyBytes);
        report["EmptyPayloadRecomputedHash"] = emptyBytes.Length > 0 ? ContentHash.Compute(emptyBytes).Value : "(missing)";
        report["EmptyPayloadRunsHolding"] = emptyRuns.Count;
        report["EmptyMembers"] = emptyMemberRows;
        report["EmptyReplayServiceInvoked"] = false;
        report["EmptyAmbiguityRefusalConditionHolds"] = emptyRuns.Count != 1;

        Console.WriteLine("  [] payload   " + emptyBytes.Length + " B, held by " + emptyRuns.Count + " runs");
        Console.WriteLine("  ambiguity refusal condition (runs != 1) : " + (emptyRuns.Count != 1));
        Console.WriteLine("  the replay service was NOT invoked for this payload.");
        Console.WriteLine();

        Check(checks, "11a. the [] payload is still the 2-byte empty array at the same address",
            emptyBytes.Length == 2
            && System.Text.Encoding.UTF8.GetString(emptyBytes) == "[]"
            && string.Equals(ContentHash.Compute(emptyBytes).Value, EmptyPayloadHash, StringComparison.OrdinalIgnoreCase),
            emptyBytes.Length + " B");

        Check(checks, "11b. the [] payload is still held by 324 archived runs",
            emptyRuns.Count == ExpectedEmptyPayloadRuns,
            emptyRuns.Count.ToString(CultureInfo.InvariantCulture));

        Check(checks, "11c. the ambiguity refusal condition holds (runs != 1)", emptyRuns.Count != 1,
            emptyRuns.Count + " runs hold it");

        Check(checks, "11d. zero observations recovered from any [] member", allEmptyStillZero,
            string.Join("/", emptyMemberRows.Select(x => x["ObservationsHeld"])));

        // ================= PROVIDER AND AUTHORISATION =================
        Check(checks, "9. no provider was contacted by this tool",
            !configuration.GetValue<bool>("Providers:Eodhd:Enabled")
            && !configuration.GetValue<bool>("Providers:SecEdgar:Enabled"),
            "both connectors pinned Enabled=false");

        Check(checks, "10. no acquisition authorisation was consumed",
            deltas["ingestion_runs"] == 0,
            "no ingestion run created; this tool resolves no replay and no acquisition service");

        Check(checks, "8b. no unexplained database drift",
            deltas["observations"] == ExpectedTotalRecovered
            && deltas["ingestion_runs"] == 0
            && deltas["processed_actions"] == 5
            && deltas["action_executions"] == 5
            && deltas["quarantined_payloads"] == 0,
            "every moved table moved by exactly its expected amount");

        report["Checks"] = checks;

        // ================= GATE 6: ACTUAL vs PROJECTION =================
        var cleared = members.Count(x => (bool)(x["ClearsAllThreeFaultConditions"] ?? false));

        report["Gate6"] = new Dictionary<string, object?>
        {
            ["SealedArtifactModified"] = false,
            ["ActualSealedFaultMembers"] = 24,
            ["ActualSealedFaultEvents"] = 130,
            ["MembersClearedByThisRecovery"] = cleared,
            ["ProjectedFaultMembers"] = 24 - cleared,
            ["ProjectedFaultEvents"] = 130 - cleared,
            ["Note"] = "PROJECTION ONLY. Gate 6 was not evaluated, modified or resealed. The sealed result remains 24.",
        };

        Console.WriteLine("--- Gate 6 (NOT modified, NOT re-evaluated, NOT resealed)");
        Console.WriteLine("  A. actual sealed result          24 fault members / 130 fault events");
        Console.WriteLine("  B. local projection after five   " + (24 - cleared) + " fault members / " + (130 - cleared) + " fault events");
        Console.WriteLine("     members clearing all three fault conditions locally: " + cleared);
        Console.WriteLine();

        var success = allMembersClean && allEmptyStillZero && checks.All(c => c["Pass"] is true);

        report["AllMemberChecksPassed"] = allMembersClean;
        report["Succeeded"] = success;
        report["Verdict"] = success ? "RECONCILED" : "NOT RECONCILED - read the entries above";

        var outPath = Path.Combine(root, "artifacts", "verify", "five-member-recovery-final-reconciliation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        Console.WriteLine("--- checks");

        foreach (var c in checks.OrderBy(c => Convert.ToString(c["Check"]), StringComparer.Ordinal))
        {
            Console.WriteLine("  " + ((c["Pass"] is true) ? "PASS  " : "FAIL  ") + c["Check"] + "   [" + c["Actual"] + "]");
        }

        Console.WriteLine();
        Console.WriteLine(success ? "=== RECONCILED" : "=== NOT RECONCILED - read the entries above");
        Console.WriteLine("=== written: " + outPath);
        Console.WriteLine();
        Console.WriteLine("  NOTHING WAS REPLAYED. NOTHING WAS WRITTEN TO THE DATABASE.");
        Console.WriteLine("  NO PROVIDER WAS CONTACTED. NO AUTHORISATION WAS CREATED OR CONSUMED.");
        Console.WriteLine("  GATE 6 WAS NOT MODIFIED, NOT RE-EVALUATED AND NOT RESEALED.");

        await connection.CloseAsync().ConfigureAwait(false);

        return success ? 0 : 1;
    }

    private static long report_BaseOf(string key) => key switch
    {
        "observations" => BaseObservations,
        "ingestion_runs" => BaseIngestionRuns,
        "processed_actions" => BaseProcessedActions,
        "action_executions" => BaseActionExecutions,
        "quarantined_payloads" => BaseQuarantinedPayloads,
        _ => 0,
    };

    private static void Check(List<Dictionary<string, object?>> checks, string name, bool pass, string actual)
    {
        checks.Add(new Dictionary<string, object?> { ["Check"] = name, ["Pass"] = pass, ["Actual"] = actual });
        Console.WriteLine("  " + (pass ? "PASS  " : "FAIL  ") + name + "   [" + actual + "]");
    }

    private static async Task<Dictionary<string, long>> CountsAsync(AppDbContext context) =>
        new()
        {
            ["observations"] = await context.Observations.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["ingestion_runs"] = await context.IngestionRuns.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["processed_actions"] = await context.ProcessedActions.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["quarantined_payloads"] = await context.QuarantinedPayloads.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["action_executions"] = await context.ActionExecutions.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["audit_records"] = await context.AuditRecords.AsNoTracking().LongCountAsync().ConfigureAwait(false),
        };

    /// <summary>
    /// How many ingestion runs carry a timestamp on or after <paramref name="since"/>, using whatever
    /// timestamp column the table actually has. The column name is discovered rather than assumed;
    /// if there is none, the caller is told the check is unavailable rather than given a zero.
    /// </summary>
    private static async Task<(bool Available, string? Column, long Count)> RunsCreatedOnOrAfterAsync(
        System.Data.Common.DbConnection connection, DateTime since)
    {
        string? column = null;

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_name = 'ingestion_runs' AND data_type LIKE 'timestamp%' " +
                "ORDER BY ordinal_position";

            await using var reader = await probe.ExecuteReaderAsync().ConfigureAwait(false);

            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                column = reader.GetString(0);
            }
        }

        if (column is null) { return (false, null, 0); }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM ingestion_runs WHERE \"" + column + "\" >= @since";

        var p = cmd.CreateParameter();
        p.ParameterName = "since";
        p.Value = since;
        cmd.Parameters.Add(p);

        var n = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));

        return (true, column, n);
    }

    /// <summary>The coverage rule's interior-gap tolerance, read from the evaluator rather than restated.</summary>
    private static int ReadInteriorGapTolerance(string root)
    {
        var path = Path.Combine(root, "tests", "AI.Investment.Api.Tests", "CoverageEvaluation.cs");

        if (!File.Exists(path)) { return 4; }

        var match = Regex.Match(File.ReadAllText(path), @"InteriorGapTolerance\s*=\s*(\d+)");

        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 4;
    }

    private static int Weekdays(DateTime from, DateTime to)
    {
        if (to < from) { return 0; }

        var n = 0;

        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) { n++; }
        }

        return n;
    }
}
