using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
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

namespace AI.Investment.Tools.ReplayRecover;

/// <summary>
/// The host environment the production policy provider reads its environment name from.
/// </summary>
/// <remarks>
/// Development, deliberately: that is the environment whose appsettings the acquisitions ran under,
/// and the policy context - kill switch, capability grants - must be the same one that governed
/// them. Choosing a different environment here would be choosing a different safety posture.
/// </remarks>
internal sealed class ToolEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";

    public string ApplicationName { get; set; } = "replay-recover";

    public string ContentRootPath { get; set; } = "";

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

internal static class Program
{
    private const string RequiredDatabase = "ai_investment";
    private const string ForbiddenDatabase = "ai_investment_tests";
    private const string PriceSource = "eodhd-eod";
    private const string PriceCategory = "MarketPrices";

    /// <summary>
    /// The five, and only the five. A symbol not in this table is refused rather than looked up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Bytes</c> and <c>Retrieved</c> are the values the READ-ONLY final preflight measured on
    /// 2026-09-08, recorded in <c>artifacts/verify/local-archived-price-replay-final-preflight.md</c>.
    /// They are pinned here so that the recovery re-asserts the archive is in the same state the
    /// preflight found it in, rather than merely in a self-consistent one. A content hash proves the
    /// bytes are unchanged; these two prove the sidecar is unchanged as well, and the sidecar is
    /// where provenance comes from.
    /// </para>
    /// <para>
    /// <c>Retrieved</c> is a full-tick value as the sidecar records it. It is compared at the
    /// database's precision, for the same reason as everything else here - see
    /// <see cref="ToPersistencePrecision"/>.
    /// </para>
    /// </remarks>
    private static readonly (string Symbol, string Hash, string RunId, long Bytes, string Retrieved)[] Known =
    [
        ("CCF.US",  "ae79be18dc5534772905b3d184472916a45d0446b090b0f29af64237086dd679", "45d3fe5c-69ca-48a8-a7b2-4ad4dd2c8552", 63822, "2026-09-04T22:46:43.1521719Z"),
        ("EVBG.US", "438650fad031491550ee2f1c2d673d6da4a716d03cd2ab74f1124ce6212d270d", "e2f77177-4827-43bf-8951-2f56a28c0f2e", 80661, "2026-09-04T23:10:16.1554059Z"),
        ("GPP.US",  "626806275c359cc70dc7bdc9021375b4109556abeefca6190d2b468c4c487fc0", "4c1bc92f-ade8-4535-8322-053e1dcafbbb", 67667, "2026-09-04T23:24:02.2613270Z"),
        ("VLDR.US", "a2b139e5e2c068b1c031c5ed0ca233550fdd9787f73f4b1fa63ada98771ac711", "e4097a2d-1db7-4c85-9388-6b45cad106c4", 40824, "2026-09-05T03:03:11.9247831Z"),
        ("WIRE.US", "6d3c18170963aab334c176d8bbfee4927b93d28429a0d150479d907469578a2f", "bc0eb767-473d-481d-ad3d-d9d9bc491c50", 84953, "2026-09-05T03:04:25.5886852Z"),
    ];

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   LOCAL ARCHIVED-PAYLOAD RECOVERY - one member, one replay");
        Console.WriteLine("   NO provider call. NO acquisition authorisation. NO new run.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        if (args.Length < 2)
        {
            Console.WriteLine("  STOPPING. Usage: replay-recover <repository-root> <SYMBOL>");

            return 2;
        }

        var root = args[0];
        var symbol = args[1];

        var known = Known.Where(k => string.Equals(k.Symbol, symbol, StringComparison.Ordinal)).ToList();

        if (known.Count != 1)
        {
            Console.WriteLine("  STOPPING. '" + symbol + "' is not one of the five recoverable members.");

            return 2;
        }

        var target = known[0];

        Console.WriteLine("--- runtime");
        Console.WriteLine("  framework          : " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("  target framework   : " + (AppContext.TargetFrameworkName ?? "(unknown)"));
        Console.WriteLine();

        var raw = Environment.GetEnvironmentVariable("AIINV_TEST_POSTGRES");

        if (string.IsNullOrWhiteSpace(raw))
        {
            Console.WriteLine("  STOPPING. AIINV_TEST_POSTGRES is not set.");

            return 3;
        }

        var archiveRoot = Path.Combine(
            root, "tests", "AI.Investment.Api.Tests", "bin", "Release", "net8.0", "archive");

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

        // ---- the production composition, not a hand-wired one -------------------------------
        //
        // AddApplication + AddInfrastructure is exactly what the API host does. The seam, the
        // policy engine, the audit sink, the idempotency store and the write authorisation are all
        // the real ones; the two overrides below are the connection and the archive root, which
        // must be explicit because the configured connection points at the test database.
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

                // Both connectors off. Nothing in this path can reach a provider; leaving them
                // registered would invite an accident unrelated to what is being done.
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
        var replay = sp.GetRequiredService<IArchivedPayloadReplay>();
        var lookup = sp.GetRequiredService<IArchivedRunLookup>();
        var archive = sp.GetRequiredService<IRawResponseArchive>();

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["Symbol"] = target.Symbol,
            ["ContentHash"] = target.Hash,
            ["ExpectedRunId"] = target.RunId,
            ["ArchiveRoot"] = archiveRoot,
            ["Host"] = builder.Host,
            ["Port"] = builder.Port,
        };

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

        // ================= PRECONDITIONS =================
        Console.WriteLine("--- preconditions (all must pass before anything is replayed)");

        var checks = new List<Dictionary<string, object?>>();
        var contentHash = ContentHash.Create(target.Hash);

        var runs = await lookup.RunsForArchivedPayloadAsync(contentHash).ConfigureAwait(false);

        Check(checks, "1. live database is ai_investment", string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal), liveDatabase);
        Check(checks, "2. exactly one run holds this payload", runs.Count == 1, runs.Count.ToString(CultureInfo.InvariantCulture));

        if (runs.Count != 1)
        {
            return Abort(checks, report, root, target.Symbol);
        }

        var run = runs[0];
        var actualRunId = run.Id.Value.ToString();

        Check(checks, "3. run id is the expected original", string.Equals(actualRunId, target.RunId, StringComparison.OrdinalIgnoreCase), actualRunId);
        Check(checks, "4. subject is the expected symbol", string.Equals(run.Request.Subject.Identifier, target.Symbol, StringComparison.Ordinal), run.Request.Subject.Kind + ":" + run.Request.Subject.Identifier);
        Check(checks, "5. source is eodhd-eod", string.Equals(run.Request.SourceId.Value, PriceSource, StringComparison.Ordinal), run.Request.SourceId.Value);
        Check(checks, "6. category is MarketPrices", string.Equals(run.Request.Category.ToString(), PriceCategory, StringComparison.Ordinal), run.Request.Category.ToString());
        Check(checks, "7. run outcome is Succeeded", run.Outcome == IngestionOutcome.Succeeded, run.Outcome.ToString());
        Check(checks, "8. exactly one artifact", run.Artifacts.Count == 1, run.Artifacts.Count.ToString(CultureInfo.InvariantCulture));
        Check(checks, "9. the artifact is the expected payload", run.Artifacts.Count == 1 && string.Equals(run.Artifacts[0].Value, target.Hash, StringComparison.OrdinalIgnoreCase), run.Artifacts.Count == 1 ? run.Artifacts[0].Value : "(none)");

        // The bytes on disk, hashed again. Content addressing means a changed file cannot answer to
        // the same name - this proves the archive still holds what the run recorded.
        var payloadPath = Path.Combine(archiveRoot, target.Hash[..2], target.Hash.Substring(2, 2), target.Hash + ".bin");
        var recomputed = File.Exists(payloadPath) ? ContentHash.Compute(await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false)).Value : "(missing)";

        Check(checks, "10. archived bytes still hash to the same address", string.Equals(recomputed, target.Hash, StringComparison.OrdinalIgnoreCase), recomputed);

        var described = await archive.DescribeAsync(contentHash).ConfigureAwait(false);

        Check(checks, "11. the archive describes the payload", described is not null, described is null ? "(not described)" : described.ByteLength + " B");

        var sidecarRetrieved = described?.RetrievedAtUtc;

        Console.WriteLine("      sidecar RetrievedAtUtc = " + (sidecarRetrieved?.ToString("o") ?? "(none)"));
        report["SidecarRetrievedAtUtc"] = sidecarRetrieved?.ToString("o");
        report["PayloadByteLength"] = described?.ByteLength;
        report["RecomputedContentHash"] = recomputed;

        var subject = run.Request.Subject.Identifier;

        var observationsBeforeForSubject = await context.Observations.AsNoTracking()
            .CountAsync(o => o.Subject.Identifier == subject).ConfigureAwait(false);

        Check(checks, "12. subject currently holds zero observations", observationsBeforeForSubject == 0, observationsBeforeForSubject.ToString(CultureInfo.InvariantCulture));

        var idempotencyKey = "normalization.record:" + run.Id;

        var claimedBefore = await context.ProcessedActions.AsNoTracking()
            .AnyAsync(p => p.IdempotencyKey == idempotencyKey).ConfigureAwait(false);

        Check(checks, "13. replay idempotency key is unclaimed", !claimedBefore, claimedBefore ? "CLAIMED" : "free");

        report["IdempotencyKey"] = idempotencyKey;
        report["CorrelationId"] = run.Request.CorrelationId.ToString();
        report["RequestFingerprint"] = run.Request.Fingerprint();

        Console.WriteLine("      idempotency key        = " + idempotencyKey);
        Console.WriteLine("      original correlation   = " + run.Request.CorrelationId);

        // No acquisition authorisation is reachable from this path: the replay service's
        // constructor takes a lookup, an archive and the normalisation pipeline, and nothing else.
        var ctorTypes = replay.GetType().GetConstructors()[0].GetParameters().Select(p => p.ParameterType.Name).ToArray();

        Check(checks, "14. replay service holds no provider or authorisation dependency",
            ctorTypes.Length == 3
            && ctorTypes.Contains("IArchivedRunLookup")
            && ctorTypes.Contains("IRawResponseArchive")
            && ctorTypes.Contains("INormalizationPipeline"),
            string.Join(", ", ctorTypes));

        // The archive must be in the state the read-only preflight measured, not merely in a
        // self-consistent one. Check 10 proves the bytes did not change; these two prove the sidecar
        // did not either, which matters because the sidecar is where provenance comes from.
        var preflightRetrieved = DateTime.Parse(
            target.Retrieved, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        Check(checks, "15. payload byte length matches the final preflight",
            described is not null && described.ByteLength == target.Bytes,
            (described?.ByteLength.ToString(CultureInfo.InvariantCulture) ?? "(none)") + " vs preflight " + target.Bytes.ToString(CultureInfo.InvariantCulture));

        Check(checks, "16. sidecar RetrievedAtUtc matches the final preflight",
            sidecarRetrieved.HasValue
            && ToPersistencePrecision(sidecarRetrieved.Value) == ToPersistencePrecision(preflightRetrieved),
            (sidecarRetrieved?.ToString("o") ?? "(none)") + " vs preflight " + target.Retrieved);

        report["PreflightRetrievedAtUtc"] = target.Retrieved;
        report["PreflightPayloadByteLength"] = target.Bytes;

        report["Preconditions"] = checks;

        if (checks.Any(c => c["Pass"] is false))
        {
            return Abort(checks, report, root, target.Symbol);
        }

        Console.WriteLine();
        Console.WriteLine("  ALL PRECONDITIONS PASS.");
        Console.WriteLine();

        // ---- historical quarantine, captured before ----
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking()
            .Where(q => q.Id == contentHash)
            .Select(q => new { q.RuleId, q.Reason, q.QuarantinedAtUtc })
            .SingleOrDefaultAsync().ConfigureAwait(false);

        report["QuarantineBefore"] = quarantineBefore is null ? null : new Dictionary<string, object?>
        {
            ["RuleId"] = quarantineBefore.RuleId,
            ["Reason"] = quarantineBefore.Reason,
            ["QuarantinedAtUtc"] = quarantineBefore.QuarantinedAtUtc.ToString("o"),
        };

        var before = await CountsAsync(context).ConfigureAwait(false);
        before["observations_for_subject"] = observationsBeforeForSubject;
        report["Before"] = before;

        Console.WriteLine("--- counts before");
        foreach (var pair in before) { Console.WriteLine("  " + pair.Key.PadRight(26) + pair.Value); }

        // ================= THE ONE REPLAY =================
        Console.WriteLine();
        Console.WriteLine("--- executing exactly one ArchivedPayloadReplayService.ReplayAsync");
        Console.WriteLine();

        var result = await replay.ReplayAsync(contentHash).ConfigureAwait(false);

        Console.WriteLine("  status                   " + result.Status);
        Console.WriteLine("  run id                   " + result.RunId);
        Console.WriteLine("  observations recorded    " + result.ObservationsRecorded);
        Console.WriteLine("  payloads read            " + result.Normalization?.PayloadsRead);
        Console.WriteLine("  payloads quarantined     " + result.Normalization?.PayloadsQuarantined);
        Console.WriteLine("  rows rejected            " + result.Normalization?.RowsRejected);
        Console.WriteLine("  reason                   " + result.Reason);
        Console.WriteLine();

        report["Result"] = new Dictionary<string, object?>
        {
            ["Status"] = result.Status.ToString(),
            ["RunId"] = result.RunId?.Value.ToString(),
            ["ObservationsRecorded"] = result.ObservationsRecorded,
            ["PayloadsRead"] = result.Normalization?.PayloadsRead,
            ["PayloadsQuarantined"] = result.Normalization?.PayloadsQuarantined,
            ["RowsRejected"] = result.Normalization?.RowsRejected,
            ["Reason"] = result.Reason,
        };

        // ================= AFTER =================
        var after = await CountsAsync(context).ConfigureAwait(false);

        var observationsAfterForSubject = await context.Observations.AsNoTracking()
            .CountAsync(o => o.Subject.Identifier == subject).ConfigureAwait(false);

        after["observations_for_subject"] = observationsAfterForSubject;
        report["After"] = after;

        Console.WriteLine("--- counts after");

        var deltas = new Dictionary<string, long>();

        foreach (var pair in before)
        {
            var delta = after[pair.Key] - pair.Value;
            deltas[pair.Key] = delta;

            Console.WriteLine("  " + pair.Key.PadRight(26) +
                pair.Value.ToString(CultureInfo.InvariantCulture).PadLeft(10) + " -> " +
                after[pair.Key].ToString(CultureInfo.InvariantCulture).PadLeft(10) +
                "   delta " + (delta >= 0 ? "+" : "") + delta.ToString(CultureInfo.InvariantCulture));
        }

        report["Deltas"] = deltas;

        var recorded = result.ObservationsRecorded;

        var expectedDeltas = new Dictionary<string, long>
        {
            ["observations"] = recorded,
            ["observations_for_subject"] = recorded,
            ["ingestion_runs"] = 0,
            ["quarantined_payloads"] = 0,
            ["processed_actions"] = 1,
            ["action_executions"] = 1,
        };

        var drift = new List<string>();

        foreach (var pair in expectedDeltas)
        {
            if (deltas.TryGetValue(pair.Key, out var actual) && actual != pair.Value)
            {
                drift.Add(pair.Key + ": expected +" + pair.Value + ", got " + (actual >= 0 ? "+" : "") + actual);
            }
        }

        report["UnexplainedDrift"] = drift;

        // ---- quarantine, after ----
        var quarantineAfter = await context.QuarantinedPayloads.AsNoTracking()
            .Where(q => q.Id == contentHash)
            .Select(q => new { q.RuleId, q.Reason, q.QuarantinedAtUtc })
            .SingleOrDefaultAsync().ConfigureAwait(false);

        var quarantinePreserved =
            quarantineBefore is not null && quarantineAfter is not null
            && string.Equals(quarantineBefore.RuleId, quarantineAfter.RuleId, StringComparison.Ordinal)
            && string.Equals(quarantineBefore.Reason, quarantineAfter.Reason, StringComparison.Ordinal)
            && quarantineBefore.QuarantinedAtUtc == quarantineAfter.QuarantinedAtUtc;

        report["QuarantineAfter"] = quarantineAfter is null ? null : new Dictionary<string, object?>
        {
            ["RuleId"] = quarantineAfter.RuleId,
            ["Reason"] = quarantineAfter.Reason,
            ["QuarantinedAtUtc"] = quarantineAfter.QuarantinedAtUtc.ToString("o"),
        };

        report["QuarantinePreservedUnchanged"] = quarantinePreserved;

        Console.WriteLine();
        Console.WriteLine("--- historical quarantine record");
        Console.WriteLine("  before : " + (quarantineBefore is null ? "(none)" : quarantineBefore.RuleId + " @ " + quarantineBefore.QuarantinedAtUtc.ToString("o")));
        Console.WriteLine("  after  : " + (quarantineAfter is null ? "(none)" : quarantineAfter.RuleId + " @ " + quarantineAfter.QuarantinedAtUtc.ToString("o")));
        Console.WriteLine("  unchanged: " + quarantinePreserved);

        // ---- idempotency, after ----
        var claimedAfter = await context.ProcessedActions.AsNoTracking()
            .AnyAsync(p => p.IdempotencyKey == idempotencyKey).ConfigureAwait(false);

        report["IdempotencyKeyClaimedAfter"] = claimedAfter;

        Console.WriteLine();
        Console.WriteLine("--- idempotency");
        Console.WriteLine("  key claimed before : " + claimedBefore);
        Console.WriteLine("  key claimed after  : " + claimedAfter);

        // ---- the recovered series, read back ----
        var stored = await context.Observations.AsNoTracking()
            .Where(o => o.Subject.Identifier == subject)
            .Where(o => o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute)
            .ToListAsync().ConfigureAwait(false);

        // ---- provenance ----
        //
        // The stored values are read and reported exactly as PostgreSQL returned them. Nothing is
        // rewritten, rounded or truncated in the database, in the sidecar, or in how production
        // generates provenance.
        //
        // The comparison itself is done at the precision the column actually has. A .NET DateTime
        // resolves to 100 ns (7 fractional digits); a PostgreSQL timestamp resolves to 1 us
        // (6 digits). The seventh digit of the sidecar's instant cannot be stored, so an exact-tick
        // equality between the sidecar and anything read back out of the database can never hold.
        // That is a property of the storage, not evidence about the data - so BOTH sides are
        // normalised to microseconds before they are compared, and neither side is modified.
        //
        // Two separate assertions, because they can fail for different reasons:
        //   1. one replay produced exactly ONE distinct RetrievedAtUtc  (no mixed provenance)
        //   2. that one value IS the archived sidecar's, at storage precision  (right provenance)

        var distinctRetrieved = stored
            .Select(o => o.Provenance.RetrievedAtUtc)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var singleRetrievedAt = stored.Count > 0 && distinctRetrieved.Count == 1;

        var expectedAtPersistence = sidecarRetrieved.HasValue
            ? ToPersistencePrecision(sidecarRetrieved.Value)
            : (DateTime?)null;

        var actualAtPersistence = singleRetrievedAt
            ? ToPersistencePrecision(distinctRetrieved[0])
            : (DateTime?)null;

        var matchesSidecarAtPersistence = expectedAtPersistence.HasValue
            && actualAtPersistence.HasValue
            && actualAtPersistence.Value == expectedAtPersistence.Value;

        var provenanceOk = singleRetrievedAt && matchesSidecarAtPersistence;

        report["ObservationsReadBack"] = stored.Count;
        report["PersistencePrecision"] = "1 microsecond (PostgreSQL timestamp)";
        report["AllObservationsShareOneRetrievedAtUtc"] = singleRetrievedAt;
        report["SidecarRetrievedAtUtcAtPersistencePrecision"] = expectedAtPersistence?.ToString("o");
        report["StoredRetrievedAtUtcAtPersistencePrecision"] = actualAtPersistence?.ToString("o");
        report["MatchesSidecarAtPersistencePrecision"] = matchesSidecarAtPersistence;
        report["EveryObservationCarriesSidecarRetrievedAt"] = provenanceOk;
        report["DistinctRetrievedAtValues"] = distinctRetrieved.Select(d => d.ToString("o")).ToArray();

        // Gate 12: the store's own identity for an observation. Duplicates are counted, not assumed
        // absent - this platform has had 5,249 of them before, unnoticed.
        var duplicateGroups = stored
            .GroupBy(o => string.Join('|',
                o.Subject.Kind,
                o.Subject.Identifier ?? "",
                o.Attribute,
                o.Provenance.AsOfUtc.ToString("O", CultureInfo.InvariantCulture),
                o.Provenance.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                o.Value.Canonical))
            .Count(g => g.Count() > 1);

        report["Gate12DuplicateGroups"] = duplicateGroups;

        Console.WriteLine();
        Console.WriteLine("--- the recovered series, read back from the store");
        Console.WriteLine("  observations held        " + stored.Count);
        Console.WriteLine("  distinct RetrievedAtUtc  " + distinctRetrieved.Count + "  (all share one : " + singleRetrievedAt + ")");
        Console.WriteLine("  stored, as returned      " + (distinctRetrieved.Count > 0 ? distinctRetrieved[0].ToString("o") : "(none)"));
        Console.WriteLine("  sidecar   @ 1 us         " + (expectedAtPersistence?.ToString("o") ?? "(none)"));
        Console.WriteLine("  stored    @ 1 us         " + (actualAtPersistence?.ToString("o") ?? "(none)"));
        Console.WriteLine("  provenance matches sidecar at persistence precision : " + matchesSidecarAtPersistence);
        Console.WriteLine("  Gate 12 duplicate groups " + duplicateGroups);

        // ---- read-only local Gate 6 observation (NOT a Gate 6 change) ----
        var sessions = stored
            .Select(o => o.Provenance.AsOfUtc)
            .OrderBy(d => d)
            .ToList();

        var tolerance = ReadInteriorGapTolerance(root);
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
            .Select(o => new ClosingPrice(
                o.Provenance.AsOfUtc,
                decimal.Parse(o.Value.Canonical, CultureInfo.InvariantCulture)))
            .ToList();

        AdjustedSeries? adjusted = null;

        if (splitsHeld == 0 && prices.Count > 0)
        {
            adjusted = SplitAdjustment.Apply(prices, [], SplitAdjustment.DefaultMaxUnexplainedMove);
        }

        report["LocalObservation"] = new Dictionary<string, object?>
        {
            ["ObservationsGreaterThanZero"] = stored.Count > 0,
            ["FirstSession"] = sessions.Count > 0 ? sessions[0].ToString("o") : null,
            ["LastSession"] = sessions.Count > 0 ? sessions[^1].ToString("o") : null,
            ["WorstInteriorGap"] = worst,
            ["WorstInteriorGapAt"] = worstAt,
            ["InteriorGapTolerance"] = tolerance,
            ["ExceedsGapTolerance"] = worst > tolerance,
            ["SplitObservationsHeld"] = splitsHeld,
            ["SplitAdjustmentEvaluated"] = adjusted is not null,
            ["SplitAdjustmentUsable"] = adjusted?.IsUsable,
            ["SplitAdjustmentRefusal"] = adjusted?.Refusal.ToString(),
            ["SplitAdjustmentExplanation"] = adjusted?.Explanation,
            ["AllThreeGate6FaultConditionsStillFail"] =
                stored.Count > 0 && worst <= tolerance && adjusted is not null && adjusted.IsUsable,
        };

        Console.WriteLine();
        Console.WriteLine("--- READ-ONLY local observation (Gate 6 is NOT modified and remains 24)");
        Console.WriteLine("  observations > 0         " + (stored.Count > 0));
        Console.WriteLine("  span                     " + (sessions.Count > 0 ? sessions[0].ToString("yyyy-MM-dd") + " .. " + sessions[^1].ToString("yyyy-MM-dd") : "(none)"));
        Console.WriteLine("  worst interior gap       " + worst + "   tolerance " + tolerance + "   exceeds: " + (worst > tolerance));
        Console.WriteLine("  split observations held  " + splitsHeld);
        Console.WriteLine("  SplitAdjustment          " + (adjusted is null ? "(not evaluated)" : adjusted.IsUsable ? "usable" : "REFUSED - " + adjusted.Refusal));

        if (adjusted is not null && !adjusted.IsUsable)
        {
            Console.WriteLine("      " + adjusted.Explanation);
        }

        await connection.CloseAsync().ConfigureAwait(false);

        var success =
            result.Status == ArchivedReplayStatus.Replayed
            && recorded > 0
            && observationsBeforeForSubject == 0
            && observationsAfterForSubject == recorded
            && duplicateGroups == 0
            && provenanceOk
            && quarantinePreserved
            && claimedAfter
            && deltas["ingestion_runs"] == 0
            && drift.Count == 0;

        report["Succeeded"] = success;
        report["Verdict"] = success ? "RECOVERED" : "NOT AS EXPECTED - read the entries above";

        var outPath = Path.Combine(root, "artifacts", "verify", "recovery-" + target.Symbol.Replace(".", "-").ToLowerInvariant() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(success ? "=== RECOVERED" : "=== NOT AS EXPECTED - read the entries above");
        Console.WriteLine("=== written: " + outPath);
        Console.WriteLine();
        Console.WriteLine("  ONE REPLAY WAS EXECUTED. NO SECOND REPLAY, NO RETRY, NO NEW INGESTION RUN.");
        Console.WriteLine("  NO PROVIDER WAS CONTACTED. NO ACQUISITION AUTHORISATION WAS INVOLVED.");
        Console.WriteLine("  THE HISTORICAL QUARANTINE RECORD WAS NEITHER DELETED NOR ALTERED.");
        Console.WriteLine("  GATE 6 WAS NOT MODIFIED AND REMAINS 24 UNTIL A DELIBERATE RE-EVALUATION.");

        return success ? 0 : 1;
    }

    private static int Abort(
        List<Dictionary<string, object?>> checks,
        Dictionary<string, object?> report,
        string root,
        string symbol)
    {
        Console.WriteLine();
        Console.WriteLine("  STOPPING BEFORE REPLAY. A precondition failed:");

        foreach (var check in checks.Where(c => c["Pass"] is false))
        {
            Console.WriteLine("    FAILED  " + check["Check"] + "   (" + check["Actual"] + ")");
        }

        report["Succeeded"] = false;
        report["Verdict"] = "STOPPED BEFORE REPLAY - a precondition failed";

        var outPath = Path.Combine(root, "artifacts", "verify", "recovery-" + symbol.Replace(".", "-").ToLowerInvariant() + "-aborted.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine();
        Console.WriteLine("  NOTHING WAS REPLAYED AND NOTHING WAS WRITTEN.");
        Console.WriteLine("=== written: " + outPath);

        return 6;
    }

    private static void Check(List<Dictionary<string, object?>> checks, string name, bool pass, string actual)
    {
        checks.Add(new Dictionary<string, object?> { ["Check"] = name, ["Pass"] = pass, ["Actual"] = actual });

        Console.WriteLine("  " + (pass ? "PASS  " : "FAIL  ") + name.PadRight(56) + actual);
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
    /// The same instant expressed at the precision the database column actually has.
    /// </summary>
    /// <remarks>
    /// PostgreSQL <c>timestamp</c> resolves to one microsecond; a .NET <see cref="DateTime"/> resolves
    /// to one hundred nanoseconds, which is ten ticks per microsecond. Comparing a value that has been
    /// through the column against one that has not is therefore only meaningful once both sides are
    /// expressed at one microsecond.
    /// <para>
    /// This is used ONLY to compare. It never writes: the database keeps what it stored, the sidecar
    /// keeps what it recorded, and production provenance generation is untouched. Both sides of the
    /// comparison go through this method, so neither is privileged.
    /// </para>
    /// </remarks>
    private static DateTime ToPersistencePrecision(DateTime instant) =>
        new(instant.Ticks - (instant.Ticks % TicksPerMicrosecond), instant.Kind);

    /// <summary>Ten 100-ns ticks to the microsecond.</summary>
    private const long TicksPerMicrosecond = 10L;

    /// <summary>
    /// The coverage rule's interior-gap tolerance, read from the evaluator rather than restated.
    /// </summary>
    private static int ReadInteriorGapTolerance(string root)
    {
        var path = Path.Combine(root, "src", "AI.Investment.Domain", "Coverage", "CoverageEvaluation.cs");

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
