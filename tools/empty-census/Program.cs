using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Investment.Tools.EmptyCensus;

internal sealed class ToolEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "empty-census";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>
/// READ-ONLY evidence census for the empty-series ([]) policy question.
/// </summary>
/// <remarks>
/// Reads only. It never resolves the replay service, never normalises anything into the store, and
/// never writes to the database. Its single output file is a JSON record under artifacts/verify.
/// It does not decide the policy; it measures what evidence exists to decide it with.
/// </remarks>
internal static class Program
{
    private const string RequiredDatabase = "ai_investment";
    private const string ForbiddenDatabase = "ai_investment_tests";

    private static readonly string[] PriceEmptyMembers = ["BPYU.US", "KIN.US", "LBRA.US", "MSOF.US", "USCR.US"];
    private static readonly string[] SplitEmptyMembers = ["NGM.US", "ONEM.US", "QUMU.US", "SDC.US"];

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   EMPTY-SERIES ([]) EVIDENCE CENSUS   (READ ONLY)");
        Console.WriteLine("   No replay. No provider. No write. No policy change.");
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

        if (!string.Equals(builder.Database, RequiredDatabase, StringComparison.Ordinal))
        {
            builder.Database = RequiredDatabase;
        }

        if (string.Equals(builder.Database, ForbiddenDatabase, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  STOPPING. The resolved database is " + ForbiddenDatabase + ".");
            return 5;
        }

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

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string liveDatabase;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT current_database()";
            liveDatabase = Convert.ToString(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) ?? "";
        }

        Console.WriteLine("--- live database: " + liveDatabase + "  server " + connection.ServerVersion);

        if (!string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal))
        {
            Console.WriteLine("  STOPPING. The LIVE database is '" + liveDatabase + "'.");
            return 5;
        }

        // ---- the payload, from the bytes themselves ----
        var emptyBytes = Encoding.UTF8.GetBytes("[]");
        var emptyHash = ContentHash.Compute(emptyBytes);

        Console.WriteLine("--- the empty payload");
        Console.WriteLine("  computed hash of the 2 bytes \"[]\" : " + emptyHash.Value);

        var payloadPath = Path.Combine(archiveRoot, emptyHash.Value[..2], emptyHash.Value.Substring(2, 2), emptyHash.Value + ".bin");
        var onDisk = File.Exists(payloadPath) ? await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false) : [];
        var described = await archive.DescribeAsync(emptyHash).ConfigureAwait(false);

        Console.WriteLine("  archive file                      : " + (File.Exists(payloadPath) ? onDisk.Length + " B, content " + Encoding.UTF8.GetString(onDisk) : "MISSING"));
        Console.WriteLine("  sidecar SourceId                  : " + (described is null ? "(none)" : Describe(described)));
        Console.WriteLine();

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["Mode"] = "READ ONLY - evidence census only, no policy applied",
            ["LiveDatabase"] = liveDatabase,
            ["ServerVersion"] = connection.ServerVersion,
            ["EmptyPayloadHash"] = emptyHash.Value,
            ["EmptyPayloadBytesOnDisk"] = onDisk.Length,
            ["EmptyPayloadContent"] = onDisk.Length > 0 ? Encoding.UTF8.GetString(onDisk) : null,
            ["EmptyPayloadSidecar"] = described is null ? null : Describe(described),
        };

        // ---- the quarantine store, keyed by content hash ----
        var quarantine = await context.QuarantinedPayloads.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == emptyHash).ConfigureAwait(false);

        report["EmptyPayloadQuarantineRuleId"] = quarantine?.RuleId;
        report["EmptyPayloadQuarantineReason"] = quarantine?.Reason;
        report["EmptyPayloadQuarantinedAtUtc"] = quarantine?.QuarantinedAtUtc.ToString("o");
        report["QuarantineStoreIsKeyedByContentHash"] = true;

        Console.WriteLine("--- quarantine record for the shared [] payload");
        Console.WriteLine("  " + (quarantine is null ? "(no quarantine row)" : quarantine.RuleId + "  @ " + quarantine.QuarantinedAtUtc.ToString("o")));

        if (quarantine is not null) { Console.WriteLine("  reason: " + quarantine.Reason); }

        Console.WriteLine();

        // ---- every run holding it ----
        var holders = await lookup.RunsForArchivedPayloadAsync(emptyHash).ConfigureAwait(false);

        Console.WriteLine("--- runs holding the shared [] payload: " + holders.Count);
        Console.WriteLine();

        var rows = holders.Select(r => new
        {
            RunId = r.Id.Value.ToString(),
            Source = r.Request.SourceId.Value,
            Category = r.Request.Category.ToString(),
            Region = r.Request.Region.Code,
            Subject = r.Request.Subject.Kind + ":" + r.Request.Subject.Identifier,
            SubjectId = r.Request.Subject.Identifier,
            Fingerprint = r.Request.Fingerprint(),
            WindowStart = r.Request.Window?.StartUtc.ToString("o"),
            WindowEnd = r.Request.Window?.EndUtc.ToString("o"),
            HasWindow = r.Request.Window is not null,
            Outcome = r.Outcome.ToString(),
            RequestedAtUtc = r.Request.RequestedAtUtc.ToString("o"),
            CorrelationId = r.Request.CorrelationId.ToString(),
            Artifacts = r.Artifacts.Count,
        }).ToList();

        report["HolderCount"] = holders.Count;

        void Group(string name, Func<dynamic, string> key)
        {
            var g = rows.GroupBy(r => key(r))
                .Select(x => new Dictionary<string, object?> { ["Value"] = x.Key, ["Runs"] = x.Count() })
                .OrderByDescending(x => (int)x["Runs"]!)
                .ToList();

            report["By" + name] = g;

            Console.WriteLine("  by " + name + ":");

            foreach (var x in g.Take(12))
            {
                Console.WriteLine("    " + Convert.ToString(x["Value"])!.PadRight(46) + x["Runs"]);
            }

            if (g.Count > 12) { Console.WriteLine("    ... " + (g.Count - 12) + " more"); }

            Console.WriteLine();
        }

        Group("Source", r => (string)r.Source);
        Group("Category", r => (string)r.Category);
        Group("SourceAndCategory", r => r.Source + "/" + r.Category);
        Group("Outcome", r => (string)r.Outcome);
        Group("Region", r => (string)r.Region);
        Group("HasWindow", r => ((bool)r.HasWindow) ? "window present" : "NO WINDOW");
        Group("Window", r => r.WindowStart is null ? "(none)" : r.WindowStart + " .. " + r.WindowEnd);

        var distinctSubjects = rows.Select(r => r.Subject).Distinct(StringComparer.Ordinal).Count();
        var distinctFingerprints = rows.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).Count();

        report["DistinctSubjects"] = distinctSubjects;
        report["DistinctFingerprints"] = distinctFingerprints;
        report["SameBytesAcrossMateriallyDifferentRequests"] = distinctFingerprints > 1;

        Console.WriteLine("  distinct subjects     : " + distinctSubjects);
        Console.WriteLine("  distinct fingerprints : " + distinctFingerprints);
        Console.WriteLine("  -> the same 2 bytes answer " + distinctFingerprints + " materially different requests.");
        Console.WriteLine();

        // ---- the nine members, in detail ----
        var nine = new List<Dictionary<string, object?>>();

        foreach (var symbol in PriceEmptyMembers.Concat(SplitEmptyMembers))
        {
            var isPrice = PriceEmptyMembers.Contains(symbol);

            var mine = rows.Where(r => string.Equals(r.SubjectId, symbol, StringComparison.Ordinal)).ToList();

            var observationsHeld = await context.Observations.AsNoTracking()
                .CountAsync(o => o.Subject.Identifier == symbol).ConfigureAwait(false);

            var closesHeld = await context.Observations.AsNoTracking()
                .CountAsync(o => o.Subject.Identifier == symbol
                              && o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute).ConfigureAwait(false);

            var splitsHeld = await context.Observations.AsNoTracking()
                .CountAsync(o => o.Subject.Identifier == symbol
                              && o.Attribute == EodhdSplitsNormalizer.SplitAttribute).ConfigureAwait(false);

            var entry = new Dictionary<string, object?>
            {
                ["Symbol"] = symbol,
                ["Group"] = isPrice ? "price-[] (Category F)" : "splits-[] (price is Category E)",
                ["RunsHoldingEmptyPayload"] = mine.Count,
                ["Runs"] = mine.Select(r => new Dictionary<string, object?>
                {
                    ["RunId"] = r.RunId,
                    ["Source"] = r.Source,
                    ["Category"] = r.Category,
                    ["Region"] = r.Region,
                    ["Subject"] = r.Subject,
                    ["Fingerprint"] = r.Fingerprint,
                    ["WindowStart"] = r.WindowStart,
                    ["WindowEnd"] = r.WindowEnd,
                    ["HasWindow"] = r.HasWindow,
                    ["Outcome"] = r.Outcome,
                    ["RequestedAtUtc"] = r.RequestedAtUtc,
                    ["CorrelationId"] = r.CorrelationId,
                }).ToList(),
                ["ObservationsHeldAnyAttribute"] = observationsHeld,
                ["ClosePriceObservationsHeld"] = closesHeld,
                ["SplitObservationsHeld"] = splitsHeld,
            };

            nine.Add(entry);

            Console.WriteLine("  " + symbol.PadRight(9) + (isPrice ? "price-[]  " : "splits-[] ")
                + mine.Count + " run(s) hold []   obs " + observationsHeld
                + " (closes " + closesHeld + ", splits " + splitsHeld + ")");

            foreach (var r in mine)
            {
                Console.WriteLine("      " + r.Source + "/" + r.Category + "  " + r.Outcome
                    + "  window " + (r.HasWindow ? r.WindowStart![..10] + ".." + r.WindowEnd![..10] : "NONE")
                    + "  fp " + r.Fingerprint[..12] + "…");
            }
        }

        report["NineMembers"] = nine;
        Console.WriteLine();

        // ---- what the archive sidecar can and cannot say ----
        var sidecarFields = described is null
            ? []
            : described.GetType().GetProperties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        report["ArchiveSidecarFields"] = sidecarFields;

        var requestFields = typeof(IngestionRequest).GetProperties()
            .Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        report["IngestionRequestFields"] = requestFields;

        var runFields = typeof(IngestionRun).GetProperties()
            .Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        report["IngestionRunFields"] = runFields;

        Console.WriteLine("--- what the model retains");
        Console.WriteLine("  IngestionRequest : " + string.Join(", ", requestFields));
        Console.WriteLine("  IngestionRun     : " + string.Join(", ", runFields));
        Console.WriteLine("  Archive sidecar  : " + string.Join(", ", sidecarFields));
        Console.WriteLine();

        var outPath = Path.Combine(root, "artifacts", "verify", "empty-series-policy-analysis.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        Console.WriteLine("=== written: " + outPath);
        Console.WriteLine();
        Console.WriteLine("  READ ONLY. Nothing was replayed, normalised, written or authorised.");
        Console.WriteLine("  NO POLICY WAS CHANGED. [] SEMANTICS ARE EXACTLY AS THEY WERE.");

        await connection.CloseAsync().ConfigureAwait(false);

        return 0;
    }

    private static string Describe(object described)
    {
        var t = described.GetType();

        return string.Join("  ", t.GetProperties()
            .Select(p => p.Name + "=" + Convert.ToString(p.GetValue(described), CultureInfo.InvariantCulture)));
    }
}
