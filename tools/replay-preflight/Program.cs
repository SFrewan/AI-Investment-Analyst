using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AI.Investment.Tools.ReplayPreflight;

/// <summary>
/// Write authorisation that never authorises anything.
/// </summary>
/// <remarks>
/// The persistence layer consults this before it commits. Supplying a permissive one would have
/// made "nothing was written" a claim about this program's intentions; supplying this one makes it
/// a property of the context: SaveChanges on this session throws, and an attempt to open an
/// authorisation window throws before it can be opened.
/// </remarks>
internal sealed class DenyAllWrites : IWriteAuthorization
{
    public bool IsAuthorized => false;

    public Guid? AuthorizingDecisionId => null;

    public IDisposable Authorize(PolicyDecision decision) =>
        throw new InvalidOperationException(
            "READ-ONLY PREFLIGHT: an authorisation window was requested. Refused.");
}

/// <summary>
/// A normalisation pipeline that refuses to run and remembers that it was asked.
/// </summary>
/// <remarks>
/// The replay service is constructed with this so that "the ambiguous payload never reaches
/// normalisation" is demonstrated rather than asserted. If the service's ordering were ever changed
/// so that it normalised before checking cardinality, this throws and the preflight fails loudly.
/// </remarks>
internal sealed class RefusingPipeline : INormalizationPipeline
{
    public bool WasCalled { get; private set; }

    public Task<NormalizationSummary> NormalizeAsync(
        IngestionRun run,
        CancellationToken cancellationToken = default)
    {
        WasCalled = true;

        throw new InvalidOperationException(
            "READ-ONLY PREFLIGHT: normalisation was attempted. Refused.");
    }
}

internal static class Program
{
    private const string RequiredDatabase = "ai_investment";
    private const string ForbiddenDatabase = "ai_investment_tests";
    private const string PriceSource = "eodhd-eod";
    private const string PriceCategory = "MarketPrices";
    private const string SharedHash = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945";
    private const int ExpectedSharedRuns = 324;

    private static readonly (string Symbol, string Hash, string RunId)[] Targets =
    [
        ("CCF.US",  "ae79be18dc5534772905b3d184472916a45d0446b090b0f29af64237086dd679", "45d3fe5c-69ca-48a8-a7b2-4ad4dd2c8552"),
        ("EVBG.US", "438650fad031491550ee2f1c2d673d6da4a716d03cd2ab74f1124ce6212d270d", "e2f77177-4827-43bf-8951-2f56a28c0f2e"),
        ("GPP.US",  "626806275c359cc70dc7bdc9021375b4109556abeefca6190d2b468c4c487fc0", "4c1bc92f-ade8-4535-8322-053e1dcafbbb"),
        ("VLDR.US", "a2b139e5e2c068b1c031c5ed0ca233550fdd9787f73f4b1fa63ada98771ac711", "e4097a2d-1db7-4c85-9388-6b45cad106c4"),
        ("WIRE.US", "6d3c18170963aab334c176d8bbfee4927b93d28429a0d150479d907469578a2f", "bc0eb767-473d-481d-ad3d-d9d9bc491c50"),
    ];

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   FINAL PRE-RECOVERY PREFLIGHT - net8.0 tool, real database");
        Console.WriteLine("   READ ONLY. Writes are refused by construction, not by intent.");
        Console.WriteLine("   NO RECOVERY. NO NORMALISATION. NO REPLAY OF ANY TARGET.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        if (args.Length < 1)
        {
            Console.WriteLine("  STOPPING. Usage: replay-preflight <repository-root>");

            return 2;
        }

        var root = args[0];

        Console.WriteLine("--- runtime");
        Console.WriteLine("  framework          : " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("  target framework   : " + (AppContext.TargetFrameworkName ?? "(unknown)"));
        Console.WriteLine("  process            : " + Environment.Version);
        Console.WriteLine("  64-bit             : " + Environment.Is64BitProcess);
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

        // The configured connection points at the test database, which holds no evidence. The
        // database is switched here EXPLICITLY, the switch is reported, and the live identity is
        // read back from the server afterwards - the connection is never trusted for being named
        // what we hoped.
        var builder = new NpgsqlConnectionStringBuilder(raw);

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

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["TargetFramework"] = AppContext.TargetFrameworkName,
            ["ArchiveRoot"] = archiveRoot,
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        await using var context = new AppDbContext(options, new DenyAllWrites());

        // ---- live identity, read from the server rather than from configuration ----
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string liveDatabase;
        string liveUserSchema;
        string liveVersion;

        await using (var identityCommand = connection.CreateCommand())
        {
            identityCommand.CommandText =
                "SELECT current_database() || '|' || current_schema() || '|' || version()";

            var scalar = Convert.ToString(await identityCommand.ExecuteScalarAsync().ConfigureAwait(false)) ?? "";
            var parts = scalar.Split('|');

            liveDatabase = parts.Length > 0 ? parts[0] : "";
            liveUserSchema = parts.Length > 1 ? parts[1] : "";
            liveVersion = parts.Length > 2 ? parts[2] : "";
        }

        Console.WriteLine("--- live database identity, as the server reports it");
        Console.WriteLine("  current_database() : " + liveDatabase);
        Console.WriteLine("  current_schema()   : " + liveUserSchema);
        Console.WriteLine("  server             : " + connection.ServerVersion);
        Console.WriteLine("  version()          : " + liveVersion);
        Console.WriteLine();

        report["LiveDatabase"] = liveDatabase;
        report["LiveSchema"] = liveUserSchema;
        report["ServerVersion"] = connection.ServerVersion;
        report["ServerVersionString"] = liveVersion;
        report["Host"] = builder.Host;
        report["Port"] = builder.Port;

        if (!string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal))
        {
            Console.WriteLine("  STOPPING. The LIVE database is '" + liveDatabase + "', not " + RequiredDatabase + ".");

            return 5;
        }

        // ---- before ----
        var before = await CountsAsync(context).ConfigureAwait(false);
        report["Before"] = before;

        Console.WriteLine("--- row counts as found");
        foreach (var pair in before)
        {
            Console.WriteLine("  " + pair.Key.PadRight(24) + pair.Value);
        }

        Console.WriteLine();

        var archive = new FileSystemRawResponseArchive(
            Options.Create(new RawArchiveOptions { RootPath = archiveRoot }));

        var lookup = new EfIngestionRunStore(context);
        var pipeline = new RefusingPipeline();
        var service = new ArchivedPayloadReplayService(lookup, archive, pipeline);

        // ---- the five targets ----
        Console.WriteLine("--- the five targets, through the real lookup and EF materialisation");
        Console.WriteLine();

        var targetRows = new List<Dictionary<string, object?>>();
        var allTargetsPass = true;

        foreach (var target in Targets)
        {
            var row = await ExamineAsync(context, lookup, archive, target).ConfigureAwait(false);
            targetRows.Add(row);

            var pass = row["AllPreconditionsPass"] is true;
            if (!pass) { allTargetsPass = false; }

            Console.WriteLine("  " + target.Symbol.PadRight(9) + (pass ? "ALL PRECONDITIONS PASS" : "*** FAILED ***"));
            Console.WriteLine("      runs returned      " + row["RunsReturned"] + "   materialised: " + row["Materialised"]);

            if (row["Materialised"] is true)
            {
                Console.WriteLine("      run id             " + row["RunId"] + "   matches expected: " + row["RunIdMatchesExpected"]);
                Console.WriteLine("      source / category  " + row["SourceId"] + " / " + row["Category"] + "   (" + row["Region"] + ")");
                Console.WriteLine("      subject            " + row["SubjectKind"] + ":" + row["SubjectIdentifier"] + "   matches: " + row["SubjectMatchesSymbol"]);
                Console.WriteLine("      outcome            " + row["Outcome"]);
                Console.WriteLine("      artifacts          " + row["ArtifactCount"] + "   is the expected payload: " + row["ArtifactIsTheExpectedPayload"]);
                Console.WriteLine("      archive describes  " + row["ArchiveDescribesEveryArtifact"] + "   " + row["ArchivedPayloadSummary"]);
                Console.WriteLine("      observations held  " + row["ObservationsHeldForSubject"] + "   holds none: " + row["HoldsNoObservations"]);
                Console.WriteLine("      idempotency key    " + row["IdempotencyKey"]);
                Console.WriteLine("      key is free        " + row["IdempotencyKeyIsFree"]);
                Console.WriteLine("      replay invoked     NO - deliberately not called for a target");
            }

            Console.WriteLine();
        }

        report["Targets"] = targetRows;

        // ---- the shared empty array, through the actual replay service ----
        Console.WriteLine("--- the shared empty array, through the actual replay service");

        var sharedContentHash = ContentHash.Create(SharedHash);
        var sharedRuns = await lookup.RunsForArchivedPayloadAsync(sharedContentHash).ConfigureAwait(false);

        var sharedPriceRuns = sharedRuns
            .Where(r => string.Equals(r.Request.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .ToList();

        var sharedResult = await service.ReplayAsync(sharedContentHash).ConfigureAwait(false);

        var shared = new Dictionary<string, object?>
        {
            ["ContentHash"] = SharedHash,
            ["RunsReturned"] = sharedRuns.Count,
            ["ExpectedRuns"] = ExpectedSharedRuns,
            ["RunsMatchExpected"] = sharedRuns.Count == ExpectedSharedRuns,
            ["PriceRuns"] = sharedPriceRuns.Count,
            ["PriceRunSubjects"] = sharedPriceRuns
                .Select(r => r.Request.Subject.Identifier)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray(),
            ["ServiceStatus"] = sharedResult.Status.ToString(),
            ["ServiceChoseARun"] = sharedResult.RunId.HasValue,
            ["ServiceRecordedObservations"] = sharedResult.RecordedObservations,
            ["ObservationsRecorded"] = sharedResult.ObservationsRecorded,
            ["NormalizationWasAttempted"] = pipeline.WasCalled,
            ["Reason"] = sharedResult.Reason,
        };

        report["SharedEmptyArray"] = shared;

        Console.WriteLine("  runs returned            " + sharedRuns.Count + "   expected " + ExpectedSharedRuns + "   matches: " + (sharedRuns.Count == ExpectedSharedRuns));
        Console.WriteLine("  price runs               " + sharedPriceRuns.Count);
        Console.WriteLine("  price run subjects       " + string.Join(", ", (string[])shared["PriceRunSubjects"]!));
        Console.WriteLine("  service status           " + shared["ServiceStatus"]);
        Console.WriteLine("  chose a run              " + shared["ServiceChoseARun"]);
        Console.WriteLine("  recorded observations    " + shared["ObservationsRecorded"]);
        Console.WriteLine("  normalisation attempted  " + pipeline.WasCalled);
        Console.WriteLine("  reason                   " + sharedResult.Reason);
        Console.WriteLine();

        // ---- determinism ----
        var first = (await lookup.RunsForArchivedPayloadAsync(sharedContentHash).ConfigureAwait(false))
            .Select(r => r.Id.Value.ToString()).ToArray();
        var second = (await lookup.RunsForArchivedPayloadAsync(sharedContentHash).ConfigureAwait(false))
            .Select(r => r.Id.Value.ToString()).ToArray();

        var deterministic = first.SequenceEqual(second, StringComparer.Ordinal);
        report["LookupIsDeterministic"] = deterministic;

        Console.WriteLine("--- lookup is deterministic: " + deterministic);
        Console.WriteLine();

        // ---- after ----
        var after = await CountsAsync(context).ConfigureAwait(false);
        report["After"] = after;

        var drift = new List<string>();

        Console.WriteLine("--- row counts, before and after");

        foreach (var pair in before)
        {
            var a = after[pair.Key];
            var same = a == pair.Value;

            if (!same) { drift.Add(pair.Key + ": " + pair.Value + " -> " + a); }

            Console.WriteLine("  " + pair.Key.PadRight(24) +
                pair.Value.ToString().PadLeft(10) + " -> " + a.ToString().PadLeft(10) +
                "   " + (same ? "identical" : "*** CHANGED ***"));
        }

        report["Drift"] = drift;
        report["NothingChanged"] = drift.Count == 0;
        report["NormalizationEverAttempted"] = pipeline.WasCalled;

        await connection.CloseAsync().ConfigureAwait(false);

        var sharedOk =
            sharedResult.Status == ArchivedReplayStatus.MoreThanOneRunHoldsThisPayload
            && !sharedResult.RunId.HasValue
            && sharedResult.ObservationsRecorded == 0
            && !pipeline.WasCalled
            && sharedRuns.Count == ExpectedSharedRuns;

        var ready = allTargetsPass && sharedOk && drift.Count == 0 && deterministic;

        report["FiveTargetsPass"] = allTargetsPass;
        report["SharedPayloadRefusedAsAmbiguous"] = sharedOk;
        report["Verdict"] = ready ? "READY FOR RECOVERY" : "NOT READY - see the entries above";

        var outPath = Path.Combine(root, "artifacts", "verify", "replay-final-preflight.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(
            outPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(ready ? "=== PREFLIGHT PASSED - READY FOR RECOVERY" : "=== PREFLIGHT FAILED - do not recover.");
        Console.WriteLine("=== written: " + outPath);
        Console.WriteLine();
        Console.WriteLine("  NO RECOVERY WAS PERFORMED. NO TARGET WAS REPLAYED. NORMALISATION WAS NEVER CALLED.");
        Console.WriteLine("  THE CONTEXT WAS BUILT WITH A WRITE AUTHORISATION THAT NEVER AUTHORISES.");
        Console.WriteLine("  NO OBSERVATION WAS WRITTEN. NO AUTHORISATION WAS CONSUMED. NO PROVIDER WAS CONTACTED.");
        Console.WriteLine("  NO CONNECTION STRING, USERNAME OR PASSWORD WAS PRINTED OR WRITTEN.");

        return ready ? 0 : 1;
    }

    private static async Task<Dictionary<string, long>> CountsAsync(AppDbContext context) =>
        new()
        {
            ["observations"] = await context.Observations.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["ingestion_runs"] = await context.IngestionRuns.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["processed_actions"] = await context.ProcessedActions.AsNoTracking().LongCountAsync().ConfigureAwait(false),
            ["quarantined_payloads"] = await context.QuarantinedPayloads.AsNoTracking().LongCountAsync().ConfigureAwait(false),
        };

    /// <summary>
    /// Everything the replay service checks, evaluated in its order, without invoking it.
    /// </summary>
    /// <remarks>
    /// The service is deliberately NOT called for a target: for these five every precondition
    /// passes, so it would go on to normalise and write - which is exactly what this block forbids.
    /// The same components are used in the same sequence instead.
    /// </remarks>
    private static async Task<Dictionary<string, object?>> ExamineAsync(
        AppDbContext context,
        EfIngestionRunStore lookup,
        FileSystemRawResponseArchive archive,
        (string Symbol, string Hash, string RunId) target)
    {
        var row = new Dictionary<string, object?>
        {
            ["Symbol"] = target.Symbol,
            ["ContentHash"] = target.Hash,
            ["ExpectedRunId"] = target.RunId,
        };

        var contentHash = ContentHash.Create(target.Hash);
        var runs = await lookup.RunsForArchivedPayloadAsync(contentHash).ConfigureAwait(false);

        row["RunsReturned"] = runs.Count;
        row["ResolvesToExactlyOneRun"] = runs.Count == 1;

        if (runs.Count != 1)
        {
            row["Materialised"] = false;
            row["AllPreconditionsPass"] = false;

            return row;
        }

        var run = runs[0];

        row["Materialised"] = true;
        row["RunId"] = run.Id.Value.ToString();
        row["RunIdMatchesExpected"] = string.Equals(run.Id.Value.ToString(), target.RunId, StringComparison.OrdinalIgnoreCase);
        row["SourceId"] = run.Request.SourceId.Value;
        row["Category"] = run.Request.Category.ToString();
        row["Region"] = run.Request.Region.ToString();
        row["SubjectKind"] = run.Request.Subject.Kind;
        row["SubjectIdentifier"] = run.Request.Subject.Identifier;
        row["Outcome"] = run.Outcome.ToString();
        row["StartedAtUtc"] = run.StartedAtUtc.ToString("o");
        row["CompletedAtUtc"] = run.CompletedAtUtc?.ToString("o");
        row["RequestFingerprint"] = run.Request.Fingerprint();
        row["ArtifactCount"] = run.Artifacts.Count;
        row["Artifacts"] = run.Artifacts.Select(a => a.Value).ToArray();

        row["SourceIsEodhdEod"] = string.Equals(run.Request.SourceId.Value, PriceSource, StringComparison.Ordinal);
        row["CategoryIsMarketPrices"] = string.Equals(run.Request.Category.ToString(), PriceCategory, StringComparison.Ordinal);
        row["SubjectMatchesSymbol"] = string.Equals(run.Request.Subject.Identifier, target.Symbol, StringComparison.Ordinal);
        row["ExactlyOneArtifact"] = run.Artifacts.Count == 1;
        row["ArtifactIsTheExpectedPayload"] =
            run.Artifacts.Count == 1 &&
            string.Equals(run.Artifacts[0].Value, target.Hash, StringComparison.OrdinalIgnoreCase);
        row["OutcomeIsSucceeded"] = run.Outcome == IngestionOutcome.Succeeded;

        var described = true;
        var payloadSummaries = new List<Dictionary<string, object?>>();
        var summaryLine = "";

        foreach (var artifact in run.Artifacts)
        {
            var payload = await archive.DescribeAsync(artifact).ConfigureAwait(false);

            if (payload is null)
            {
                described = false;
                payloadSummaries.Add(new Dictionary<string, object?>
                {
                    ["Artifact"] = artifact.Value,
                    ["Described"] = false,
                });

                continue;
            }

            payloadSummaries.Add(new Dictionary<string, object?>
            {
                ["Artifact"] = artifact.Value,
                ["Described"] = true,
                ["SourceId"] = payload.SourceId.Value,
                ["MediaType"] = payload.MediaType,
                ["RetrievedAtUtc"] = payload.RetrievedAtUtc.ToString("o"),
                ["ByteLength"] = payload.ByteLength,
            });

            summaryLine = payload.ByteLength + " B, retrieved " + payload.RetrievedAtUtc.ToString("o");
        }

        row["ArchiveDescribesEveryArtifact"] = described;
        row["ArchivedPayloads"] = payloadSummaries;
        row["ArchivedPayloadSummary"] = summaryLine;

        var subject = run.Request.Subject.Identifier;

        var observations = await context.Observations
            .AsNoTracking()
            .CountAsync(o => o.Subject.Identifier == subject)
            .ConfigureAwait(false);

        row["ObservationsHeldForSubject"] = observations;
        row["HoldsNoObservations"] = observations == 0;

        var key = "normalization.record:" + run.Id;

        var claimed = await context.ProcessedActions
            .AsNoTracking()
            .AnyAsync(p => p.IdempotencyKey == key)
            .ConfigureAwait(false);

        row["IdempotencyKey"] = key;
        row["IdempotencyKeyClaimed"] = claimed;
        row["IdempotencyKeyIsFree"] = !claimed;
        row["ReplayInvoked"] = false;

        row["AllPreconditionsPass"] =
            runs.Count == 1
            && row["RunIdMatchesExpected"] is true
            && row["SourceIsEodhdEod"] is true
            && row["CategoryIsMarketPrices"] is true
            && row["SubjectMatchesSymbol"] is true
            && row["ExactlyOneArtifact"] is true
            && row["ArtifactIsTheExpectedPayload"] is true
            && row["OutcomeIsSucceeded"] is true
            && described
            && observations == 0
            && !claimed;

        return row;
    }
}
