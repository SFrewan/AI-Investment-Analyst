using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Investment.Tools.PreflightSix;

internal sealed class ToolEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "preflight-six";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>The facts about one ingestion run that identity resolution is allowed to see.</summary>
/// <remarks>
/// A projection rather than the run itself, so the resolution rule is a pure function over data and
/// can be exercised without a database. Nothing here is pinned: every instance is built from a live
/// row.
/// </remarks>
internal sealed record RunFacts(string RunId, bool Succeeded, int Artifacts, string Fingerprint);

/// <summary>The outcome of trying to name one original run for a subject.</summary>
internal sealed record Resolution(string? RunId, string? Refusal, string Rule);

/// <summary>
/// READ-ONLY preflight (v2) for the six remaining Category E no-series members.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by construction. This tool never resolves <see cref="IArchivedPayloadReplay"/> and never
/// constructs <c>ArchivedPayloadReplayService</c>; it only reflects over that type to report its
/// dependency shape. Every database access is a SELECT. Both connectors are pinned off.
/// </para>
/// <para>
/// The payload measurement calls the REAL <see cref="INormalizer"/> from the production container.
/// That is a parse and nothing else, per the interface's own contract, so it answers "what would a
/// replay record?" without recording anything. The count it reports is authoritative; the row numbers
/// parsed out of its reason are a convenience and are cross-checked against it.
/// </para>
/// <para>
/// Nothing about the six is pinned. Run identity, content hash, subject, sidecar and row counts are
/// all resolved from the live store and the archived bytes.
/// </para>
/// </remarks>
internal static class Program
{
    private const string RequiredDatabase = "ai_investment";
    private const string ForbiddenDatabase = "ai_investment_tests";
    private const string PriceSource = "eodhd-eod";
    private const string PriceCategory = "MarketPrices";
    private const string SplitsSource = "eodhd-splits";
    private const string SplitsCategory = "CorporateActions";

    private static readonly string[] Six = ["LGIQ.US", "NGM.US", "ONEM.US", "QUMU.US", "SDC.US", "SHPW.US"];

    /// <summary>The members whose splits payload the brief asks to characterise.</summary>
    private static readonly string[] SplitsToCheck = ["NGM.US", "ONEM.US", "QUMU.US", "SDC.US"];

    private const long TicksPerMicrosecond = 10L;

    private static DateTime ToPersistencePrecision(DateTime instant) =>
        new(instant.Ticks - (instant.Ticks % TicksPerMicrosecond), instant.Kind);

    // ================================================================= identity resolution

    /// <summary>
    /// Names the one original price run for a subject, or refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The v1 rule demanded exactly one price run per subject. That was wrong: the replay path keys on
    /// the payload, not the subject, and a subject can legitimately carry a failed first attempt
    /// alongside its successful retry. LGIQ is exactly that shape - a Failed run holding no artifact
    /// and a Succeeded run holding one, both carrying the same request fingerprint.
    /// </para>
    /// <para>
    /// So the rule is: exactly one Succeeded run holding at least one artifact, every other run for the
    /// subject Failed and holding none, and every run agreeing on the request fingerprint. The
    /// fingerprint condition is what makes a rejected attempt a RETRY of the same request rather than a
    /// different request that happens to share a symbol; without it, two unrelated acquisitions could be
    /// collapsed into one. Payload-level uniqueness is checked separately by the caller and is the
    /// condition the replay service itself enforces.
    /// </para>
    /// </remarks>
    internal static Resolution ResolveOriginal(IReadOnlyList<RunFacts> runs)
    {
        const string rule =
            "exactly one Succeeded run holding an artifact; every other run for the subject Failed "
            + "holding none; all runs share one request fingerprint";

        if (runs.Count == 0)
        {
            return new Resolution(null, "no eodhd-eod/MarketPrices run holds this subject", rule);
        }

        var winners = runs.Where(r => r.Succeeded && r.Artifacts > 0).ToList();

        if (winners.Count == 0)
        {
            return new Resolution(null, "no Succeeded run holding an artifact", rule);
        }

        if (winners.Count > 1)
        {
            return new Resolution(null,
                "ambiguous: " + winners.Count + " Succeeded runs hold artifacts ("
                + string.Join(", ", winners.Select(w => w.RunId)) + ")", rule);
        }

        var others = runs.Where(r => r.RunId != winners[0].RunId).ToList();
        var badOthers = others.Where(r => r.Succeeded || r.Artifacts != 0).ToList();

        if (badOthers.Count > 0)
        {
            return new Resolution(null,
                "ambiguous: " + badOthers.Count + " other run(s) are not failed-and-artifactless ("
                + string.Join(", ", badOthers.Select(b => b.RunId + " " + (b.Succeeded ? "Succeeded" : "Failed")
                    + "/" + b.Artifacts + " artifacts")) + ")", rule);
        }

        var fingerprints = runs.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).ToList();

        if (fingerprints.Count > 1)
        {
            return new Resolution(null,
                "ambiguous: the runs do not share one request fingerprint (" + fingerprints.Count
                + " distinct), so the failed run is not a retry of the same request", rule);
        }

        return new Resolution(winners[0].RunId, null, rule);
    }

    // ================================================================= refused-row parsing

    /// <summary>
    /// The refused row numbers, read out of the normaliser's own reason string.
    /// </summary>
    /// <remarks>
    /// The reason contains the phrase "row(s)" TWICE - once in "{N} row(s) stated a 'close' of zero or
    /// less and were dropped:" and once in "row(s) 750, 751, ...". v1 took the first occurrence, so the
    /// prose in between became part of the first comma-separated token and the FIRST row number was
    /// silently lost on every member. The list is anchored on the LAST occurrence and every integer up
    /// to the terminating full stop is taken, so the first number is preserved like any other.
    /// <para>
    /// This is a convenience for reporting. The authoritative count is always
    /// <c>NormalizationResult.RejectedRows</c>, which comes from the normaliser itself; a disagreement
    /// between the two is reported as unexpected state rather than resolved in either direction.
    /// </para>
    /// </remarks>
    internal static int[] ParseRefusedRowNumbers(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) { return []; }

        var marker = reason.LastIndexOf("row(s) ", StringComparison.Ordinal);

        if (marker < 0) { return []; }

        var tail = reason[(marker + "row(s) ".Length)..];
        var stop = tail.IndexOf('.', StringComparison.Ordinal);

        if (stop >= 0) { tail = tail[..stop]; }

        return Regex.Matches(tail, @"\d+")
            .Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture))
            .ToArray();
    }

    // ================================================================= self test

    /// <summary>
    /// Exercises the tool's own pure logic. Not a substitute for the repository's suite - it covers
    /// only this tool's behaviour, and it touches no database, no archive and no production state.
    /// </summary>
    private static List<string> SelfTest()
    {
        var failures = new List<string>();

        void Check(string name, bool pass, string actual)
        {
            Console.WriteLine("  " + (pass ? "PASS  " : "FAIL  ") + name + "   [" + actual + "]");

            if (!pass) { failures.Add(name + " -> " + actual); }
        }

        // ---- defect C: the first row number must survive ----
        const string twentyRows =
            "20 row(s) stated a 'close' of zero or less and were dropped: row(s) 750, 751, 752, 753, "
            + "754, 755, 756, 757, 758, 759, 760, 761, 762, 763, 764, 765, 766, 767, 768, 769. A "
            + "non-positive closing price is a broken feed, not a market event.";

        var parsed = ParseRefusedRowNumbers(twentyRows);

        Check("parser keeps all twenty rows", parsed.Length == 20, parsed.Length.ToString(CultureInfo.InvariantCulture));
        Check("parser keeps the FIRST row number (750)", parsed.Length > 0 && parsed[0] == 750,
            parsed.Length > 0 ? parsed[0].ToString(CultureInfo.InvariantCulture) : "(empty)");
        Check("parser keeps the LAST row number (769)", parsed.Length > 0 && parsed[^1] == 769,
            parsed.Length > 0 ? parsed[^1].ToString(CultureInfo.InvariantCulture) : "(empty)");
        Check("parser preserves the exact sequence",
            parsed.SequenceEqual(Enumerable.Range(750, 20)), string.Join(",", parsed.Take(3)) + "...");

        const string oneRow =
            "1 row(s) stated a 'close' of zero or less and were dropped: row(s) 654. A non-positive "
            + "closing price is a broken feed, not a market event.";

        var single = ParseRefusedRowNumbers(oneRow);

        Check("parser handles a single row", single.Length == 1 && single[0] == 654,
            single.Length == 1 ? single[0].ToString(CultureInfo.InvariantCulture) : single.Length.ToString(CultureInfo.InvariantCulture));

        Check("parser handles null", ParseRefusedRowNumbers(null).Length == 0, "0");
        Check("parser handles a reason with no row list",
            ParseRefusedRowNumbers("something else entirely").Length == 0, "0");

        // ---- defect A/B and the identity rule ----
        const string fp = "fingerprint-alpha";

        var lgiqShape = new List<RunFacts>
        {
            new("failed-1", Succeeded: false, Artifacts: 0, fp),
            new("succeeded-1", Succeeded: true, Artifacts: 1, fp),
        };

        var r1 = ResolveOriginal(lgiqShape);
        Check("failed 0-artifact attempt + succeeded 1-artifact retry resolves to the retry",
            r1.RunId == "succeeded-1" && r1.Refusal is null, r1.RunId ?? r1.Refusal ?? "(null)");

        var single1 = new List<RunFacts> { new("only", true, 1, fp) };
        var r2 = ResolveOriginal(single1);
        Check("a single succeeded run resolves to itself", r2.RunId == "only", r2.RunId ?? r2.Refusal ?? "(null)");

        var twoWinners = new List<RunFacts> { new("a", true, 1, fp), new("b", true, 1, fp) };
        var r3 = ResolveOriginal(twoWinners);
        Check("two succeeded runs with artifacts are refused", r3.RunId is null && r3.Refusal is not null,
            r3.Refusal ?? "(resolved!)");

        var mismatched = new List<RunFacts>
        {
            new("failed-1", false, 0, "fingerprint-alpha"),
            new("succeeded-1", true, 1, "fingerprint-beta"),
        };
        var r4 = ResolveOriginal(mismatched);
        Check("a failed run with a DIFFERENT fingerprint is refused, not absorbed",
            r4.RunId is null && r4.Refusal is not null, r4.Refusal ?? "(resolved!)");

        var failedWithArtifact = new List<RunFacts> { new("f", false, 1, fp), new("s", true, 1, fp) };
        var r5 = ResolveOriginal(failedWithArtifact);
        Check("a failed run that DOES hold an artifact is refused",
            r5.RunId is null && r5.Refusal is not null, r5.Refusal ?? "(resolved!)");

        var noneSucceeded = new List<RunFacts> { new("f1", false, 0, fp), new("f2", false, 0, fp) };
        var r6 = ResolveOriginal(noneSucceeded);
        Check("no succeeded run is refused", r6.RunId is null, r6.Refusal ?? "(resolved!)");

        var r7 = ResolveOriginal([]);
        Check("no runs at all is refused", r7.RunId is null, r7.Refusal ?? "(resolved!)");

        // ---- defect B: optional-key reads must never throw ----
        var sparse = new Dictionary<string, object?> { ["Symbol"] = "X" };

        var noThrow = true;

        try
        {
            _ = Get(sparse, "Missing");
            _ = Flag(sparse, "Missing");
            _ = Num(sparse, "Missing");
        }
        catch (Exception ex)
        {
            noThrow = false;
            Console.WriteLine("      " + ex.GetType().Name);
        }

        Check("a member dictionary missing its verdict keys does not throw", noThrow, noThrow ? "no throw" : "threw");

        return failures;
    }

    // ================================================================= main

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   CATEGORY E SIX-MEMBER LOCAL REPLAY PREFLIGHT  v2   (READ ONLY)");
        Console.WriteLine("   No replay. No provider. No acquisition. No database write.");
        Console.WriteLine("   Gate 6 is NOT modified, NOT re-evaluated and NOT resealed.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        Console.WriteLine("--- self test of this tool's own logic (no database, no archive)");

        var selfTestFailures = SelfTest();

        Console.WriteLine();

        if (selfTestFailures.Count > 0)
        {
            Console.WriteLine("  STOPPING. The tool's own self test failed; its measurements cannot be trusted.");

            foreach (var f in selfTestFailures) { Console.WriteLine("    - " + f); }

            return 9;
        }

        Console.WriteLine("  PASS  self test.");
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

        var normalizer = sp.GetServices<INormalizer>()
            .FirstOrDefault(n => n.CanNormalize(EodhdProvider.Id, DataCategory.MarketPrices));

        if (normalizer is null)
        {
            Console.WriteLine("  STOPPING. No normaliser claims eodhd-eod / MarketPrices.");
            return 8;
        }

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Version"] = 2,
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["Mode"] = "READ ONLY - no replay service constructed, no write path exists in this tool",
            ["ArchiveRoot"] = archiveRoot,
            ["Normalizer"] = normalizer.GetType().FullName,
            ["SelfTestPassed"] = true,
        };

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string liveDatabase;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT current_database()";
            liveDatabase = Convert.ToString(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) ?? "";
        }

        report["LiveDatabase"] = liveDatabase;
        report["ServerVersion"] = connection.ServerVersion;

        Console.WriteLine("--- live database identity");
        Console.WriteLine("  current_database() : " + liveDatabase);
        Console.WriteLine("  server             : " + connection.ServerVersion);
        Console.WriteLine();

        if (!string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal))
        {
            Console.WriteLine("  STOPPING. The LIVE database is '" + liveDatabase + "'.");
            return 5;
        }

        var counts = new Dictionary<string, long>
        {
            ["observations"] = await context.Observations.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["ingestion_runs"] = await context.IngestionRuns.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["processed_actions"] = await context.ProcessedActions.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["quarantined_payloads"] = await context.QuarantinedPayloads.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["action_executions"] = await context.ActionExecutions.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["audit_records"] = await context.AuditRecords.AsNoTracking().LongCountAsync().ConfigureAwait(false),
        };

        report["LiveCounts"] = counts;

        Console.WriteLine("--- live counts, as found");

        foreach (var kv in counts)
        {
            Console.WriteLine("  " + kv.Key.PadRight(22) + kv.Value.ToString(CultureInfo.InvariantCulture).PadLeft(10));
        }

        Console.WriteLine();

        var replayCtor = typeof(ArchivedPayloadReplayService).GetConstructors()[0]
            .GetParameters().Select(p => p.ParameterType.Name).ToArray();

        report["ReplayServiceDependencies"] = replayCtor;
        report["ReplayServiceConstructed"] = false;
        report["ProviderEnabledEodhd"] = configuration.GetValue<bool>("Providers:Eodhd:Enabled");
        report["ProviderEnabledSecEdgar"] = configuration.GetValue<bool>("Providers:SecEdgar:Enabled");

        Console.WriteLine("--- authorisation relevance (reflection only, nothing constructed)");
        Console.WriteLine("  ArchivedPayloadReplayService(" + string.Join(", ", replayCtor) + ")");
        Console.WriteLine("  no provider dependency, no authorisation dependency -> replay needs neither.");
        Console.WriteLine();

        var tolerance = ReadInteriorGapTolerance(root);
        report["InteriorGapTolerance"] = tolerance;

        var members = new List<Dictionary<string, object?>>();
        var identityBlockers = new List<string>();
        var stateBlockers = new List<string>();
        var unexpected = new List<string>();

        foreach (var symbol in Six)
        {
            Console.WriteLine("================ " + symbol);

            var m = new Dictionary<string, object?>
            {
                ["Symbol"] = symbol,
                ["ReadyForReplay"] = false,   // set once, up front: no path can leave it absent
                ["Blockers"] = new List<string>(),
            };

            var memberBlockers = new List<string>();

            var candidates = await context.IngestionRuns.AsNoTracking()
                .Where(r => r.Request.Subject.Identifier == symbol)
                .ToListAsync().ConfigureAwait(false);

            var priceRuns = candidates
                .Where(r => string.Equals(r.Request.SourceId.Value, PriceSource, StringComparison.Ordinal)
                         && string.Equals(r.Request.Category.ToString(), PriceCategory, StringComparison.Ordinal))
                .ToList();

            var facts = priceRuns
                .Select(r => new RunFacts(r.Id.Value.ToString(), r.Outcome == IngestionOutcome.Succeeded,
                    r.Artifacts.Count, r.Request.Fingerprint()))
                .ToList();

            var resolution = ResolveOriginal(facts);

            m["RunsForSubjectAnySource"] = candidates.Count;
            m["PriceRunsForSubject"] = priceRuns.Count;
            m["OtherSourcesSeen"] = candidates.Select(r => r.Request.SourceId.Value + "/" + r.Request.Category)
                .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            m["IdentityRule"] = resolution.Rule;
            m["IdentityRefusal"] = resolution.Refusal;
            m["CandidatePriceRuns"] = facts.Select(f => new Dictionary<string, object?>
            {
                ["RunId"] = f.RunId,
                ["Succeeded"] = f.Succeeded,
                ["Artifacts"] = f.Artifacts,
                ["RequestFingerprint"] = f.Fingerprint,
            }).ToList();
            m["AllPriceRunsShareOneFingerprint"] =
                facts.Select(f => f.Fingerprint).Distinct(StringComparer.Ordinal).Count() <= 1;

            Console.WriteLine("  price runs   " + priceRuns.Count
                + "   (" + string.Join(", ", facts.Select(f => (f.Succeeded ? "Succeeded" : "Failed") + "/" + f.Artifacts + "art")) + ")");
            Console.WriteLine("  fingerprints " + facts.Select(f => f.Fingerprint).Distinct(StringComparer.Ordinal).Count() + " distinct");

            if (resolution.RunId is null)
            {
                memberBlockers.Add("identity: " + resolution.Refusal);
                m["Blockers"] = memberBlockers;
                identityBlockers.AddRange(memberBlockers.Select(b => symbol + ": " + b));
                members.Add(m);
                Console.WriteLine("  BLOCKED      " + resolution.Refusal);
                Console.WriteLine();
                continue;
            }

            var run = priceRuns.Single(r => r.Id.Value.ToString() == resolution.RunId);

            m["RunId"] = run.Id.Value.ToString();
            m["SubjectKind"] = run.Request.Subject.Kind;
            m["SubjectIdentifier"] = run.Request.Subject.Identifier;
            m["SubjectDisplay"] = run.Request.Subject.Kind + ":" + run.Request.Subject.Identifier;
            m["SourceId"] = run.Request.SourceId.Value;
            m["Category"] = run.Request.Category.ToString();
            m["Outcome"] = run.Outcome.ToString();
            m["ArtifactCount"] = run.Artifacts.Count;
            m["CorrelationId"] = run.Request.CorrelationId.ToString();
            m["RequestFingerprint"] = run.Request.Fingerprint();

            Console.WriteLine("  resolved     " + run.Id.Value + "  " + run.Outcome
                + "  artifacts " + run.Artifacts.Count + "  " + run.Request.CorrelationId);
            Console.WriteLine("  subject      " + run.Request.Subject.Kind + ":" + run.Request.Subject.Identifier);

            if (run.Artifacts.Count != 1)
            {
                memberBlockers.Add("the resolved run holds " + run.Artifacts.Count + " artifacts, not exactly 1");
                m["Blockers"] = memberBlockers;
                stateBlockers.AddRange(memberBlockers.Select(b => symbol + ": " + b));
                members.Add(m);
                Console.WriteLine("  BLOCKED      cannot resolve a single payload.");
                Console.WriteLine();
                continue;
            }

            var hashValue = run.Artifacts[0].Value;
            var contentHash = ContentHash.Create(hashValue);

            m["ContentHash"] = hashValue;

            var holders = await lookup.RunsForArchivedPayloadAsync(contentHash).ConfigureAwait(false);
            var soleHolder = holders.Count == 1 && holders[0].Id.Value == run.Id.Value;

            m["RunsHoldingPayload"] = holders.Count;
            m["OriginalRunIsSoleHolder"] = soleHolder;

            if (!soleHolder)
            {
                memberBlockers.Add("payload ambiguity: " + holders.Count + " runs hold this content hash");
            }

            var payloadPath = Path.Combine(archiveRoot, hashValue[..2], hashValue.Substring(2, 2), hashValue + ".bin");
            var exists = File.Exists(payloadPath);
            byte[] bytes = exists ? await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false) : [];
            var recomputed = exists ? ContentHash.Compute(bytes).Value : "(missing)";
            var bytesOk = exists && string.Equals(recomputed, hashValue, StringComparison.OrdinalIgnoreCase);

            m["ArchiveFileExists"] = exists;
            m["ArchiveByteLength"] = bytes.Length;
            m["RecomputedContentHash"] = recomputed;
            m["ArchiveBytesMatchStoredHash"] = bytesOk;

            if (!bytesOk) { memberBlockers.Add(exists ? "archived bytes no longer hash to the stored address" : "archived payload file is missing"); }

            var described = await archive.DescribeAsync(contentHash).ConfigureAwait(false);
            var sidecar = described?.RetrievedAtUtc;

            m["SidecarRetrievedAtUtc"] = sidecar?.ToString("o");
            m["SidecarByteLength"] = described?.ByteLength;
            m["SidecarByteLengthMatchesFile"] = described is not null && described.ByteLength == bytes.Length;
            m["SidecarRetrievedAtUtcAtPersistencePrecision"] = sidecar.HasValue ? ToPersistencePrecision(sidecar.Value).ToString("o") : null;
            m["PersistencePrecision"] = "1 microsecond (PostgreSQL timestamp); normalise BOTH sides before comparing";

            if (described is null) { memberBlockers.Add("the archive does not describe this payload (no sidecar)"); }

            Console.WriteLine("  payload      " + hashValue);
            Console.WriteLine("  bytes        " + bytes.Length + "  file " + (exists ? "present" : "MISSING")
                + "  hash " + (bytesOk ? "ok" : "MISMATCH") + "  holders " + holders.Count);

            NormalizationResult? result = null;
            string? normaliserError = null;

            if (bytesOk && sidecar.HasValue)
            {
                try
                {
                    result = await normalizer.NormalizeAsync(new NormalizationInput(
                        run.Request.SourceId, run.Request.Category, run.Request.Subject,
                        contentHash, bytes, sidecar.Value)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    normaliserError = ex.GetType().Name + ": " + ex.Message;
                }
            }

            m["NormalizerThrew"] = normaliserError;

            if (normaliserError is not null) { memberBlockers.Add("the normaliser threw: " + normaliserError); }

            var totalRows = CountRows(bytes, out var rowsError);

            m["RawRowCount"] = totalRows;
            m["RawRowCountError"] = rowsError;

            var admitted = 0;
            int[] rejectedRowNumbers = [];
            List<DateTime> admittedSessions = [];
            List<ClosingPrice> admittedPrices = [];

            if (result is not null)
            {
                m["NormalizationShape"] = result.IsQuarantined ? "Quarantined"
                    : result.HadRejectedRows ? "Partial" : "Normalized";
                m["QuarantineRuleId"] = result.RuleId;
                m["QuarantineReason"] = result.Reason;

                // AUTHORITATIVE: the normaliser's own count. Never replaced by the parser's.
                m["RejectedRows"] = result.RejectedRows;
                m["RejectedRowCountSource"] = "NormalizationResult.RejectedRows (normaliser)";
                m["RejectedRowRuleId"] = result.RejectedRowRuleId;
                m["RejectedRowReason"] = result.RejectedRowReason;

                admitted = result.Observations.Count;

                admittedSessions = result.Observations.Select(o => o.Provenance.AsOfUtc).OrderBy(d => d).ToList();
                admittedPrices = result.Observations.OrderBy(o => o.Provenance.AsOfUtc)
                    .Select(o => new ClosingPrice(o.Provenance.AsOfUtc,
                        decimal.Parse(o.Value.Canonical, CultureInfo.InvariantCulture)))
                    .ToList();

                rejectedRowNumbers = ParseRefusedRowNumbers(result.RejectedRowReason);

                m["RejectedRowNumbersFromNormalizerReason"] = rejectedRowNumbers;
                m["RejectedRowNumbersAgreeWithAuthoritativeCount"] = rejectedRowNumbers.Length == result.RejectedRows;

                if (rejectedRowNumbers.Length != result.RejectedRows)
                {
                    unexpected.Add(symbol + ": the normaliser reported " + result.RejectedRows
                        + " rejected rows but its reason names " + rejectedRowNumbers.Length
                        + " (the normaliser's count stands; the row list is incomplete)");
                }

                if (result.IsQuarantined)
                {
                    memberBlockers.Add("the CURRENT normaliser still quarantines this payload whole: "
                        + result.RuleId + " - " + result.Reason);
                }
            }

            m["ObservationsThatWouldBeRecorded"] = admitted;
            m["FirstAdmittedSession"] = admittedSessions.Count > 0 ? admittedSessions[0].ToString("o") : null;
            m["LastAdmittedSession"] = admittedSessions.Count > 0 ? admittedSessions[^1].ToString("o") : null;

            var refusedDetail = DescribeRows(bytes, rejectedRowNumbers, totalRows);
            m["RejectedRowDetail"] = refusedDetail;
            m["RejectedRowsIncludeInterior"] = refusedDetail.Any(r => r["Position"] as string == "interior");
            m["RejectedRowsIncludeTrailing"] = refusedDetail.Any(r => r["Position"] as string == "trailing");
            m["RejectedRowsIncludeLeading"] = refusedDetail.Any(r => r["Position"] as string == "leading");

            Console.WriteLine("  rows         " + totalRows + " raw   " + admitted + " admitted   "
                + m["RejectedRows"] + " rejected (normaliser)   list names " + rejectedRowNumbers.Length);
            Console.WriteLine("  shape        " + m["NormalizationShape"]
                + (admittedSessions.Count > 0 ? "   " + admittedSessions[0].ToString("yyyy-MM-dd") + " .. " + admittedSessions[^1].ToString("yyyy-MM-dd") : ""));

            var stored = await context.Observations.AsNoTracking()
                .Where(o => o.Subject.Identifier == symbol)
                .Where(o => o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute)
                .ToListAsync().ConfigureAwait(false);

            var storedDuplicateGroups = stored
                .GroupBy(o => string.Join('|', o.Subject.Kind, o.Subject.Identifier ?? "", o.Attribute,
                    o.Provenance.AsOfUtc.ToString("O", CultureInfo.InvariantCulture),
                    o.Provenance.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    o.Value.Canonical))
                .Count(g => g.Count() > 1);

            m["ObservationsCurrentlyHeld"] = stored.Count;
            m["Gate12DuplicateGroupsNow"] = storedDuplicateGroups;
            m["ObservationsAlreadyFromThisPayload"] = sidecar.HasValue
                ? stored.Count(o => ToPersistencePrecision(o.Provenance.RetrievedAtUtc) == ToPersistencePrecision(sidecar.Value))
                : 0;

            if (stored.Count != 0)
            {
                memberBlockers.Add("the subject already holds " + stored.Count + " close observations; replay expects zero");
            }

            if (storedDuplicateGroups != 0)
            {
                unexpected.Add(symbol + ": " + storedDuplicateGroups + " duplicate groups already present");
            }

            var key = "normalization.record:" + run.Id.Value;
            var claimed = await context.ProcessedActions.AsNoTracking()
                .AnyAsync(p => p.IdempotencyKey == key).ConfigureAwait(false);

            m["IdempotencyKey"] = key;
            m["IdempotencyKeyClaimed"] = claimed;

            if (claimed) { memberBlockers.Add("the replay idempotency key is ALREADY claimed"); }

            var quarantine = await context.QuarantinedPayloads.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == contentHash).ConfigureAwait(false);

            m["HistoricalQuarantineRuleId"] = quarantine?.RuleId;
            m["HistoricalQuarantineReason"] = quarantine?.Reason;
            m["HistoricalQuarantineAtUtc"] = quarantine?.QuarantinedAtUtc.ToString("o");

            Console.WriteLine("  store now    " + stored.Count + " observations, " + storedDuplicateGroups + " dup groups");
            Console.WriteLine("  idem key     " + (claimed ? "CLAIMED" : "free"));

            // ---- projection from CURRENT live state ----
            var worst = 0;
            var worstAt = "";

            for (var i = 1; i < admittedSessions.Count; i++)
            {
                var missing = Weekdays(admittedSessions[i - 1].AddDays(1), admittedSessions[i].AddDays(-1));

                if (missing > worst)
                {
                    worst = missing;
                    worstAt = admittedSessions[i - 1].ToString("yyyy-MM-dd") + " -> " + admittedSessions[i].ToString("yyyy-MM-dd");
                }
            }

            var splitsHeld = await context.Observations.AsNoTracking()
                .CountAsync(o => o.Subject.Identifier == symbol
                              && o.Attribute == EodhdSplitsNormalizer.SplitAttribute).ConfigureAwait(false);

            AdjustedSeries? adjusted = null;
            string splitStatus;

            if (admittedPrices.Count == 0)
            {
                splitStatus = "not evaluated: no admitted prices";
            }
            else if (splitsHeld != 0)
            {
                // Deliberately UNMEASURED rather than guessed. Passing an empty split list when splits
                // are actually held would test a series the platform does not believe in.
                splitStatus = "UNMEASURED: " + splitsHeld + " split observation(s) held, and this "
                    + "preflight will not evaluate SplitAdjustment with a split list it did not load";
            }
            else
            {
                adjusted = SplitAdjustment.Apply(admittedPrices, [], SplitAdjustment.DefaultMaxUnexplainedMove);
                splitStatus = adjusted.IsUsable ? "usable" : "REFUSED: " + adjusted.Refusal;
            }

            var faultZero = admitted == 0;
            var faultGap = worst > tolerance;
            var faultDiscontinuity = adjusted is not null && !adjusted.IsUsable;
            var discontinuityUnmeasured = adjusted is null && admittedPrices.Count > 0;

            var wouldClear = !faultZero && !faultGap && !faultDiscontinuity && !discontinuityUnmeasured;

            m["Projected"] = new Dictionary<string, object?>
            {
                ["ObservationsGreaterThanZero"] = admitted > 0,
                ["WorstInteriorGap"] = worst,
                ["WorstInteriorGapAt"] = worstAt,
                ["InteriorGapTolerance"] = tolerance,
                ["ExceedsGapTolerance"] = faultGap,
                ["SplitObservationsHeld"] = splitsHeld,
                ["SplitAdjustmentStatus"] = splitStatus,
                ["SplitAdjustmentEvaluated"] = adjusted is not null,
                ["SplitAdjustmentUsable"] = adjusted?.IsUsable,
                ["SplitAdjustmentRefusal"] = adjusted?.Refusal.ToString(),
                ["SplitAdjustmentExplanation"] = adjusted?.Explanation,
                ["FaultZeroObservations"] = faultZero,
                ["FaultInteriorGap"] = faultGap,
                ["FaultUnexplainedDiscontinuity"] = faultDiscontinuity,
                ["DiscontinuityUnmeasured"] = discontinuityUnmeasured,
                ["WouldClearAllThreeFaultConditions"] = wouldClear,
            };

            Console.WriteLine("  projection   worst gap " + worst + " (tol " + tolerance + ")"
                + (worstAt.Length > 0 ? " at " + worstAt : "")
                + "   splitAdj " + splitStatus);
            Console.WriteLine("               would clear: " + wouldClear
                + (discontinuityUnmeasured ? "  (discontinuity UNMEASURED)" : ""));

            m["Blockers"] = memberBlockers;
            m["ReadyForReplay"] = memberBlockers.Count == 0;

            stateBlockers.AddRange(memberBlockers.Select(b => symbol + ": " + b));

            foreach (var b in memberBlockers) { Console.WriteLine("  BLOCKER      " + b); }

            members.Add(m);
            Console.WriteLine();
        }

        report["Members"] = members;

        // ================================================================= the splits payloads
        Console.WriteLine("================ eodhd-splits payloads (the [] hypothesis, tested)");
        Console.WriteLine();

        var splitFindings = new List<Dictionary<string, object?>>();

        foreach (var symbol in SplitsToCheck)
        {
            var f = new Dictionary<string, object?> { ["Symbol"] = symbol };

            var splitRuns = (await context.IngestionRuns.AsNoTracking()
                    .Where(r => r.Request.Subject.Identifier == symbol)
                    .ToListAsync().ConfigureAwait(false))
                .Where(r => string.Equals(r.Request.SourceId.Value, SplitsSource, StringComparison.Ordinal)
                         && string.Equals(r.Request.Category.ToString(), SplitsCategory, StringComparison.Ordinal))
                .ToList();

            f["MatchingRuns"] = splitRuns.Count;

            var rows = new List<Dictionary<string, object?>>();

            foreach (var r in splitRuns)
            {
                foreach (var a in r.Artifacts)
                {
                    var ah = ContentHash.Create(a.Value);
                    var ap = Path.Combine(archiveRoot, a.Value[..2], a.Value.Substring(2, 2), a.Value + ".bin");
                    var ab = File.Exists(ap) ? await File.ReadAllBytesAsync(ap).ConfigureAwait(false) : [];
                    var holdersOfSplit = await lookup.RunsForArchivedPayloadAsync(ah).ConfigureAwait(false);
                    var text = ab.Length <= 64 ? System.Text.Encoding.UTF8.GetString(ab) : "(" + ab.Length + " B)";

                    rows.Add(new Dictionary<string, object?>
                    {
                        ["RunId"] = r.Id.Value.ToString(),
                        ["Outcome"] = r.Outcome.ToString(),
                        ["ContentHash"] = a.Value,
                        ["FileExists"] = File.Exists(ap),
                        ["ByteLength"] = ab.Length,
                        ["Content"] = text,
                        ["IsEmptyArray"] = ab.Length == 2 && System.Text.Encoding.UTF8.GetString(ab) == "[]",
                        ["RunsSharingThisPayload"] = holdersOfSplit.Count,
                    });

                    Console.WriteLine("  " + symbol.PadRight(9) + a.Value[..16] + "…  " + ab.Length + " B  "
                        + (ab.Length == 2 ? "[]  " : "non-empty  ") + "shared by " + holdersOfSplit.Count + " runs");
                }

                if (r.Artifacts.Count == 0)
                {
                    Console.WriteLine("  " + symbol.PadRight(9) + "(run " + r.Id.Value + " holds no artifact, " + r.Outcome + ")");
                }
            }

            f["Artifacts"] = rows;
            f["AllEmptyArray"] = rows.Count > 0 && rows.All(x => x["IsEmptyArray"] is true);
            splitFindings.Add(f);
        }

        report["SplitsPayloadFindings"] = splitFindings;
        Console.WriteLine();

        // ================================================================= recommendation
        var projectionBlocked = members.Any(x =>
            (Get(x, "Projected") as Dictionary<string, object?>) is { } p
            && p.TryGetValue("WouldClearAllThreeFaultConditions", out var wc) && wc is false);

        // Every member's splits payload being the shared empty array means the discontinuity faults
        // below cannot be resolved by replaying prices - the missing corporate action lives behind the
        // unresolved [] policy.
        var emptyPolicyBlocked = splitFindings.Any(f => f.TryGetValue("AllEmptyArray", out var e) && e is true);

        // SAFE TO REPLAY and WORTH REPLAYING are different questions, and the recommendation must not
        // conflate them. v2's first run answered only the first and emitted READY while simultaneously
        // reporting that replay improves Gate 6 by nothing - a contradiction. Safety readiness is kept
        // as its own field; the recommendation requires both.
        var safeToReplay = members.Count == Six.Length
            && members.All(x => Flag(x, "ReadyForReplay"))
            && identityBlockers.Count == 0
            && stateBlockers.Count == 0
            && unexpected.Count == 0;

        var ready = safeToReplay && !projectionBlocked;

        var order = members
            .Where(x => Flag(x, "ReadyForReplay"))
            .OrderBy(x => Flag(x, "RejectedRowsIncludeInterior") ? 1 : 0)
            .ThenBy(x => Num(x, "RejectedRows"))
            .ThenBy(x => Convert.ToString(Get(x, "Symbol")), StringComparer.Ordinal)
            .Select(x => new Dictionary<string, object?>
            {
                ["Symbol"] = Get(x, "Symbol"),
                ["RunId"] = Get(x, "RunId"),
                ["ContentHash"] = Get(x, "ContentHash"),
                ["RejectedRows"] = Get(x, "RejectedRows"),
                ["Interior"] = Get(x, "RejectedRowsIncludeInterior"),
                ["WouldClear"] = (Get(x, "Projected") as Dictionary<string, object?>) is { } pr
                    && pr.TryGetValue("WouldClearAllThreeFaultConditions", out var w) ? w : null,
            })
            .ToList();

        report["IdentityBlockers"] = identityBlockers;
        report["StateBlockers"] = stateBlockers;
        report["Unexpected"] = unexpected;
        report["SafeToReplay"] = safeToReplay;
        report["BlockedBecauseReplayDoesNotImproveGate6"] = projectionBlocked;
        report["BlockedBecauseToolDefects"] = false;
        report["BlockedBecauseIdentityAmbiguity"] = identityBlockers.Count > 0;
        report["BlockedBecauseUnresolvedEmptyPolicy"] = emptyPolicyBlocked;
        report["RecommendedReplayOrder"] = order;
        report["Recommendation"] = ready ? "READY FOR ONE-BY-ONE REPLAY" : "BLOCKED";
        report["ReplayExecuted"] = false;

        report["Gate6"] = new Dictionary<string, object?>
        {
            ["SealedArtifactModified"] = false,
            ["ActualSealedFaultMembers"] = 24,
            ["ActualSealedFaultEvents"] = 130,
            ["CurrentProjectionAfterFiveRecoveries"] = 19,
            ["MembersThatWouldClearIfAllSixReplayed"] = members.Count(x =>
                (Get(x, "Projected") as Dictionary<string, object?>) is { } p2
                && p2.TryGetValue("WouldClearAllThreeFaultConditions", out var w2) && w2 is true),
            ["Note"] = "Not evaluated, not modified, not resealed by this tool.",
        };

        var outPath = Path.Combine(root, "artifacts", "verify", "category-e-six-member-local-replay-preflight-v2.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        Console.WriteLine("================ RECOMMENDATION");
        Console.WriteLine();
        Console.WriteLine("  " + (ready ? "READY FOR ONE-BY-ONE REPLAY" : "BLOCKED"));
        Console.WriteLine();
        Console.WriteLine("  blocked because replay does not improve Gate 6 : " + projectionBlocked);
        Console.WriteLine("  blocked because of tool/preflight defects      : False  (self test passed)");
        Console.WriteLine("  blocked because of live identity ambiguity     : " + (identityBlockers.Count > 0));
        Console.WriteLine("  blocked because of unresolved [] policy        : " + emptyPolicyBlocked);
        Console.WriteLine();
        Console.WriteLine("  every safety precondition met (safe to replay) : " + safeToReplay);
        Console.WriteLine();

        foreach (var b in identityBlockers) { Console.WriteLine("    identity  - " + b); }
        foreach (var b in stateBlockers) { Console.WriteLine("    state     - " + b); }
        foreach (var u in unexpected) { Console.WriteLine("    unexpected- " + u); }

        Console.WriteLine();

        if (order.Count > 0)
        {
            Console.WriteLine("  order, if the remaining blockers are cleared:");

            foreach (var o in order)
            {
                Console.WriteLine("    " + Convert.ToString(o["Symbol"])!.PadRight(9) + o["RunId"]
                    + "   rejected " + o["RejectedRows"] + (o["Interior"] is true ? " (interior)" : "")
                    + "   would clear: " + o["WouldClear"]);
            }

            Console.WriteLine();
        }

        Console.WriteLine("=== written: " + outPath);
        Console.WriteLine();
        Console.WriteLine("  NOTHING WAS REPLAYED. NOTHING WAS WRITTEN TO THE DATABASE.");
        Console.WriteLine("  NO PROVIDER WAS CONTACTED. NO AUTHORISATION WAS CREATED OR CONSUMED.");
        Console.WriteLine("  GATE 6 WAS NOT MODIFIED, NOT RE-EVALUATED AND NOT RESEALED.");
        Console.WriteLine("  [] SEMANTICS WERE NOT CHANGED - THE PAYLOADS WERE ONLY READ.");

        await connection.CloseAsync().ConfigureAwait(false);

        return ready ? 0 : 1;
    }

    // ================================================================= helpers

    private static object? Get(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v : null;

    private static bool Flag(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is true;

    private static int Num(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is not null
            ? Convert.ToInt32(v, CultureInfo.InvariantCulture)
            : 0;

    private static int CountRows(byte[] bytes, out string? error)
    {
        error = null;

        try
        {
            using var doc = JsonDocument.Parse(bytes);

            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : -1;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;

            return -1;
        }
    }

    private static List<Dictionary<string, object?>> DescribeRows(byte[] bytes, int[] rowNumbers, int totalRows)
    {
        var output = new List<Dictionary<string, object?>>();

        if (rowNumbers.Length == 0) { return output; }

        try
        {
            using var doc = JsonDocument.Parse(bytes);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) { return output; }

            var all = doc.RootElement.EnumerateArray().ToArray();
            var refusedSet = rowNumbers.ToHashSet();

            foreach (var n in rowNumbers.OrderBy(x => x))
            {
                if (n < 1 || n > all.Length) { continue; }

                var row = all[n - 1];

                string? date = row.TryGetProperty("date", out var d) ? d.GetString() : null;
                string? close = row.TryGetProperty("close", out var c) ? c.ToString() : null;

                var trailing = Enumerable.Range(n, totalRows - n + 1).All(refusedSet.Contains);
                var leading = Enumerable.Range(1, n).All(refusedSet.Contains);

                output.Add(new Dictionary<string, object?>
                {
                    ["Row"] = n,
                    ["Date"] = date,
                    ["Close"] = close,
                    ["Position"] = trailing ? "trailing" : leading ? "leading" : "interior",
                });
            }
        }
        catch
        {
            // A document that will not parse is already reported by CountRows.
        }

        return output;
    }

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
