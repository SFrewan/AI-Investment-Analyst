using System.Globalization;
using System.Text.Json;
using AI.Investment.Application;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AI.Investment.Tools.VldrPilot;

/// <summary>
/// The host environment the production policy provider reads its environment name from.
/// </summary>
/// <remarks>
/// Development, deliberately, and for the same reason replay-recover chose it: that is the
/// environment whose appsettings define the DataIngestion capability policy. Choosing a different
/// one here would be choosing a different safety posture than the one that governs acquisition.
/// </remarks>
internal sealed class ToolEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";

    public string ApplicationName { get; set; } = "vldr-pilot";

    public string ContentRootPath { get; set; } = "";

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

internal static class Program
{
    // The inverse of replay-recover's pair. Stated as constants so the direction of the guard is
    // readable in one place rather than inferred from a comparison somewhere below.
    private const string RequiredDatabase = "ai_investment_tests";
    private const string ForbiddenDatabase = "ai_investment";

    private const string Symbol = "VLDR.US";
    private const string SubjectKind = "Security";
    private const string PriceSourceId = "eodhd-eod";

    /// <summary>The API's user-secrets id, from AI.Investment.Api.csproj.</summary>
    private const string ApiUserSecretsId = "ai-investment-analyst-api-3d4afd62-d197-469b-b5f4-3d72f327e600";

    /// <summary>
    /// The widest window this tool will send, in calendar days.
    /// </summary>
    /// <remarks>
    /// The pilot is meant to be the smallest request that produces a meaningful answer. A cap makes
    /// that a property of the tool rather than of whoever typed the arguments: a fat-fingered year
    /// is refused instead of fetched.
    /// </remarks>
    private const int MaxWindowCalendarDays = 10;

    private static readonly string[] TrackedTables =
    [
        "observations", "ingestion_runs", "processed_actions", "action_executions",
        "quarantined_payloads", "provider_exchanges", "audit_records", "data_sources",
    ];

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   CONTROLLED LIVE EODHD PRICE PILOT - VLDR.US");
        Console.WriteLine("   TEST DATABASE ONLY. AT MOST ONE PROVIDER REQUEST.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        if (args.Length < 2)
        {
            Console.WriteLine("  STOPPING. Usage:");
            Console.WriteLine("    vldr-pilot <repository-root> preflight");
            Console.WriteLine("    vldr-pilot <repository-root> execute --from yyyy-MM-dd --to yyyy-MM-dd");

            return 2;
        }

        var root = args[0];
        var mode = args[1].ToLowerInvariant();

        if (mode != "preflight" && mode != "execute")
        {
            Console.WriteLine("  STOPPING. Mode must be 'preflight' or 'execute'.");

            return 2;
        }

        DateTime? from = null;
        DateTime? to = null;

        for (var i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == "--from") { from = ParseDate(args[i + 1]); }
            if (args[i] == "--to") { to = ParseDate(args[i + 1]); }
        }

        if (mode == "execute" && (from is null || to is null))
        {
            // The window is an argument rather than something this tool derives, on purpose. It is
            // a decision that must be visible in the command that was run and in the report, and
            // the tool's job is to REFUSE a window that overlaps rather than to invent a safe one.
            Console.WriteLine("  STOPPING. execute requires --from and --to as yyyy-MM-dd.");

            return 2;
        }

        var report = new Dictionary<string, object?>
        {
            ["Mode"] = mode,
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["Symbol"] = Symbol,
            ["RequiredDatabase"] = RequiredDatabase,
            ["ForbiddenDatabase"] = ForbiddenDatabase,
            ["ProviderRequestsMade"] = 0,
        };

        var checks = new List<Dictionary<string, object?>>();

        // ---- connection ---------------------------------------------------------------------
        var raw = Environment.GetEnvironmentVariable("AIINV_TEST_POSTGRES");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Abort(report, checks, root, mode, 3, "AIINV_TEST_POSTGRES is not set.");
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(raw);
        var configuredDatabase = builder.Database;

        if (!string.Equals(builder.Database, RequiredDatabase, StringComparison.Ordinal))
        {
            builder.Database = RequiredDatabase;
        }

        Console.WriteLine("--- connection (password never printed)");
        Console.WriteLine("  host                : " + builder.Host);
        Console.WriteLine("  port                : " + builder.Port);
        Console.WriteLine("  configured database : " + configuredDatabase);
        Console.WriteLine("  resolved database   : " + builder.Database);

        if (string.Equals(builder.Database, ForbiddenDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return Abort(report, checks, root, mode, 4,
                "The resolved database is " + ForbiddenDatabase + ". This tool never writes there.");
        }

        report["Host"] = builder.Host;
        report["Port"] = builder.Port;
        report["ConfiguredDatabase"] = configuredDatabase;
        report["ResolvedDatabase"] = builder.Database;

        // ---- composition --------------------------------------------------------------------
        var apiDirectory = Path.Combine(root, "src", "AI.Investment.Api");
        var archiveRoot = Path.Combine(root, "artifacts", "verify", "pilot-archive");

        Directory.CreateDirectory(archiveRoot);

        // A dedicated archive root, NOT the production one.
        //
        // The archive is content-addressed and additive, so writing into the production root would
        // damage nothing - but it would leave a payload there that no run in ai_investment refers
        // to, which is precisely the kind of unexplained artefact this whole arc has been
        // reconciling. A pilot that writes to the test database archives beside it.
        var secrets = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "UserSecrets", ApiUserSecretsId, "secrets.json");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile(secrets, optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = builder.ConnectionString,
                ["RawArchive:RootPath"] = archiveRoot,

                // SEC stays off. The pilot is a price request and nothing else; leaving another
                // connector registered would be leaving a second way for something to be fetched.
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

        // ---- live database identity ----------------------------------------------------------
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string liveDatabase;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT current_database()";
            liveDatabase = Convert.ToString(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) ?? "";
        }

        Console.WriteLine("  current_database()  : " + liveDatabase);
        Console.WriteLine("  server version      : " + connection.ServerVersion);
        Console.WriteLine("  archive root        : " + archiveRoot);
        Console.WriteLine();

        report["LiveDatabase"] = liveDatabase;
        report["ServerVersion"] = connection.ServerVersion;
        report["ArchiveRoot"] = archiveRoot;

        Check(checks, "live database is " + RequiredDatabase,
            string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal), liveDatabase);

        Check(checks, "live database is NOT " + ForbiddenDatabase,
            !string.Equals(liveDatabase, ForbiddenDatabase, StringComparison.OrdinalIgnoreCase), liveDatabase);

        if (!string.Equals(liveDatabase, RequiredDatabase, StringComparison.Ordinal))
        {
            return Abort(report, checks, root, mode, 5,
                "current_database() is '" + liveDatabase + "', not " + RequiredDatabase + ".");
        }

        // ---- preflight -----------------------------------------------------------------------
        Console.WriteLine("--- preflight (read only)");

        var before = await CountsAsync(connection).ConfigureAwait(false);

        foreach (var table in TrackedTables)
        {
            Console.WriteLine(("  " + table).PadRight(24) + ": " + before[table]);
        }

        report["CountsBefore"] = before;

        var vldrBefore = await SymbolFactsAsync(connection).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("  VLDR observations   : " + vldrBefore.Count);
        Console.WriteLine("  earliest as_of_utc  : " + (vldrBefore.MinAsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(none)"));
        Console.WriteLine("  latest   as_of_utc  : " + (vldrBefore.MaxAsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(none)"));

        report["VldrObservationsBefore"] = vldrBefore.Count;
        report["VldrEarliestBefore"] = vldrBefore.MinAsOf?.ToString("o", CultureInfo.InvariantCulture);
        report["VldrLatestBefore"] = vldrBefore.MaxAsOf?.ToString("o", CultureInfo.InvariantCulture);

        // ---- source registration and admission ------------------------------------------------
        var (sourceExists, sourceActive) = await SourceFactsAsync(connection).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("  source registered   : " + sourceExists);
        Console.WriteLine("  source active       : " + sourceActive);

        report["SourceRegisteredBefore"] = sourceExists;
        report["SourceActiveBefore"] = sourceActive;

        // ---- connector configuration -----------------------------------------------------------
        var eodhdEnabled = configuration.GetValue<bool>("Providers:Eodhd:Enabled");
        var apiKeyPresent = !string.IsNullOrWhiteSpace(configuration["Providers:Eodhd:ApiKey"]);
        var perMinute = configuration.GetValue<int>("Providers:Eodhd:MaxRequestsPerMinute");

        var exchanges = configuration.GetSection("Providers:Eodhd:Exchanges")
            .GetChildren()
            .Select(c => c["Code"] ?? "")
            .Where(c => c.Length > 0)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("  EODHD enabled       : " + eodhdEnabled);
        Console.WriteLine("  EODHD key present   : " + apiKeyPresent + "   (the value is never read out or printed)");
        Console.WriteLine("  EODHD exchanges     : " + string.Join(", ", exchanges));
        Console.WriteLine("  EODHD per minute    : " + perMinute);
        Console.WriteLine("  SEC enabled         : " + configuration.GetValue<bool>("Providers:SecEdgar:Enabled"));

        report["EodhdEnabled"] = eodhdEnabled;
        report["EodhdApiKeyPresent"] = apiKeyPresent;
        report["EodhdExchanges"] = exchanges;
        report["EodhdMaxRequestsPerMinute"] = perMinute;
        report["SecEdgarEnabled"] = configuration.GetValue<bool>("Providers:SecEdgar:Enabled");

        Check(checks, "the EODHD price connector is enabled", eodhdEnabled, eodhdEnabled.ToString());
        Check(checks, "an EODHD key is configured", apiKeyPresent, apiKeyPresent.ToString());
        Check(checks, "the US exchange is configured", exchanges.Contains("US"), string.Join(",", exchanges));
        Check(checks, "SEC EDGAR is off", !configuration.GetValue<bool>("Providers:SecEdgar:Enabled"), "false");

        // ---- the window -------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- window");

        if (from is null || to is null)
        {
            Console.WriteLine("  (no window supplied - preflight only)");
        }
        else
        {
            var span = (to.Value - from.Value).Days + 1;

            Console.WriteLine("  requested from      : " + from.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Console.WriteLine("  requested to        : " + to.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Console.WriteLine("  calendar span       : " + span + " day(s)");

            report["RequestedFromUtc"] = from.Value.ToString("o", CultureInfo.InvariantCulture);
            report["RequestedToUtc"] = to.Value.ToString("o", CultureInfo.InvariantCulture);
            report["WindowCalendarDays"] = span;

            Check(checks, "the window is ordered", to.Value >= from.Value, span.ToString(CultureInfo.InvariantCulture));

            Check(checks, "the window is at most " + MaxWindowCalendarDays + " calendar days",
                span <= MaxWindowCalendarDays, span.ToString(CultureInfo.InvariantCulture));

            // THE RULE. The observation store is append-only with no natural key, so an overlapping
            // window duplicates rather than upserts. When the store holds nothing for this symbol
            // there is nothing to overlap, and that is stated rather than assumed.
            var overlapFree = vldrBefore.MaxAsOf is null || from.Value.Date > vldrBefore.MaxAsOf.Value.Date;

            Check(checks, "the window begins strictly after the latest stored VLDR observation",
                overlapFree,
                vldrBefore.MaxAsOf is null
                    ? "no VLDR observations are stored, so no window can overlap"
                    : "latest=" + vldrBefore.MaxAsOf.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                      + " requested from=" + from.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        Console.WriteLine();
        Console.WriteLine("--- precondition summary");

        foreach (var check in checks)
        {
            Console.WriteLine("  " + ((bool)check["Pass"]! ? "PASS  " : "FAIL  ") + check["Name"] + "   [" + check["Actual"] + "]");
        }

        var failed = checks.Count(c => !(bool)c["Pass"]!);

        Console.WriteLine();
        Console.WriteLine("  " + checks.Count + " precondition(s), " + failed + " failed.");
        Console.WriteLine();

        if (mode == "preflight")
        {
            Console.WriteLine("  PREFLIGHT ONLY. No provider was contacted. Nothing was written.");
            Console.WriteLine();

            return Finish(report, checks, root, mode, failed == 0 ? 0 : 6);
        }

        if (failed != 0)
        {
            return Abort(report, checks, root, mode, 7,
                "A precondition failed. The provider was NOT contacted.");
        }

        // ========================================================================================
        //  EVERYTHING BELOW THIS LINE RUNS ONLY WHEN EVERY PRECONDITION PASSED.
        // ========================================================================================

        // ---- registration and activation, through the existing mechanism ------------------------
        //
        // Not a shortcut around authorisation - it IS the authorisation. Gate 1 of the ingestion
        // gateway refuses an unregistered source and Gate 2 refuses an unadmitted one, and both
        // read the registry. Registration goes through RegisterKnownSourcesHandler and activation
        // through ActivateSourceHandler, each of which proposes through the same safety seam under
        // ReferenceDataManagement. No capability policy is widened and no grant is created.
        if (!sourceExists)
        {
            Console.WriteLine("--- registering known sources (existing mechanism, test database)");

            var registrar = sp.GetRequiredService<RegisterKnownSourcesHandler>();
            var registered = await registrar.HandleAsync().ConfigureAwait(false);

            foreach (var result in registered)
            {
                Console.WriteLine("  " + result);
            }

            report["Registration"] = registered.Select(r => r.ToString()).ToList();
            Console.WriteLine();
        }

        if (!sourceActive)
        {
            Console.WriteLine("--- activating " + PriceSourceId + " (existing mechanism, test database)");

            var activator = sp.GetRequiredService<ActivateSourceHandler>();
            var activation = await activator.HandleAsync(SourceId.Create(PriceSourceId)).ConfigureAwait(false);

            Console.WriteLine("  " + activation.Status + ": " + activation.Reason);

            report["Activation"] = activation.Status.ToString();
            report["ActivationReason"] = activation.Reason;

            if (activation.Status is not (ActivateSourceStatus.Activated or ActivateSourceStatus.AlreadyActive))
            {
                return Abort(report, checks, root, mode, 8,
                    "The source could not be activated. The provider was NOT contacted.");
            }

            Console.WriteLine();
        }

        // ---- the archive path this payload would take, before the request -----------------------
        var archiveBefore = Directory.Exists(archiveRoot)
            ? Directory.GetFiles(archiveRoot, "*.bin", SearchOption.AllDirectories).Length
            : 0;

        report["ArchiveBinFilesBefore"] = archiveBefore;

        // ---- THE ONE REQUEST ---------------------------------------------------------------------
        Console.WriteLine("--- ONE live EODHD price request");
        Console.WriteLine("  symbol              : " + Symbol);
        Console.WriteLine("  from                : " + from!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Console.WriteLine("  to                  : " + to!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Console.WriteLine();

        var request = IngestionRequest.Create(
            SourceId.Create(PriceSourceId),
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create(SubjectKind, Symbol),
            CorrelationId.New(),
            DateTime.UtcNow,
            DateRange.Create(
                DateTime.SpecifyKind(from.Value, DateTimeKind.Utc),
                DateTime.SpecifyKind(to.Value, DateTimeKind.Utc)));

        report["RequestFingerprint"] = request.Fingerprint();
        report["CorrelationId"] = request.CorrelationId.ToString();

        var acquisition = sp.GetRequiredService<IDataAcquisition>();

        // IDataAcquisition.AcquireAsync is the existing acquisition contract: one IngestAsync, then
        // normalise whatever it archived. It is called exactly once and never in a loop.
        var outcome = await acquisition.AcquireAsync(request).ConfigureAwait(false);

        report["ProviderRequestsMade"] = 1;

        Console.WriteLine("  run id              : " + outcome.Run.Id.Value);
        Console.WriteLine("  outcome             : " + outcome.Run.Outcome);
        Console.WriteLine("  reason              : " + (outcome.Run.Reason ?? "(none)"));
        Console.WriteLine("  refusal rule        : " + (outcome.Run.RefusalRuleId ?? "(none)"));
        Console.WriteLine("  artifacts           : " + outcome.Run.Artifacts.Count);

        foreach (var artifact in outcome.Run.Artifacts)
        {
            Console.WriteLine("    artifact          : " + artifact.Value);
        }

        Console.WriteLine("  normalization       : " + (outcome.Normalization?.ToString() ?? "(not attempted)"));
        Console.WriteLine();

        report["RunId"] = outcome.Run.Id.Value.ToString();
        report["RunOutcome"] = outcome.Run.Outcome.ToString();
        report["RunReason"] = outcome.Run.Reason;
        report["RunRefusalRuleId"] = outcome.Run.RefusalRuleId;
        report["RunArtifacts"] = outcome.Run.Artifacts.Select(a => a.Value).ToList();
        report["RunStartedAtUtc"] = outcome.Run.StartedAtUtc.ToString("o", CultureInfo.InvariantCulture);
        report["RunCompletedAtUtc"] = outcome.Run.CompletedAtUtc?.ToString("o", CultureInfo.InvariantCulture);

        if (outcome.Normalization is { } summary)
        {
            report["PayloadsRead"] = summary.PayloadsRead;
            report["ObservationsRecorded"] = summary.ObservationsRecorded;
            report["PayloadsQuarantined"] = summary.PayloadsQuarantined;
            report["RowsRejected"] = summary.RowsRejected;
        }

        // ---- verification -------------------------------------------------------------------------
        Console.WriteLine("--- verification");

        var after = await CountsAsync(connection).ConfigureAwait(false);
        report["CountsAfter"] = after;

        var deltas = TrackedTables.ToDictionary(t => t, t => after[t] - before[t]);
        report["CountDeltas"] = deltas;

        foreach (var table in TrackedTables)
        {
            Console.WriteLine(("  " + table).PadRight(24) + ": " + before[table] + " -> " + after[table]
                + "   (" + (deltas[table] >= 0 ? "+" : "") + deltas[table] + ")");
        }

        Console.WriteLine();

        // Provenance rows for this run.
        var exchangeRows = await ExchangesAsync(connection, outcome.Run.Id.Value).ConfigureAwait(false);
        report["ProviderExchanges"] = exchangeRows;

        Console.WriteLine("  provenance rows for this run: " + exchangeRows.Count);

        foreach (var row in exchangeRows)
        {
            foreach (var pair in row)
            {
                Console.WriteLine(("    " + pair.Key).PadRight(34) + ": " + pair.Value);
            }

            Console.WriteLine();
        }

        Check(checks, "exactly one provenance row per archived exchange",
            exchangeRows.Count == outcome.Run.Artifacts.Count,
            exchangeRows.Count + " row(s), " + outcome.Run.Artifacts.Count + " artifact(s)");

        // Security. Asserted against what was actually persisted, not against what was intended.
        foreach (var row in exchangeRows)
        {
            var template = Convert.ToString(row["endpoint_template"]) ?? "";
            var parameters = Convert.ToString(row["redacted_request_parameters"]) ?? "";
            var headers = Convert.ToString(row["selected_response_headers"]) ?? "";
            var blob = template + " " + parameters + " " + headers;

            Check(checks, "no api_token in persisted provenance",
                !blob.Contains("api_token", StringComparison.OrdinalIgnoreCase), Trim(blob));

            Check(checks, "no authorization/bearer in persisted provenance",
                !blob.Contains("authorization", StringComparison.OrdinalIgnoreCase)
                && !blob.Contains("bearer", StringComparison.OrdinalIgnoreCase), Trim(blob));

            Check(checks, "no scheme or host in the endpoint template",
                !template.Contains("://", StringComparison.Ordinal)
                && !template.Contains("eodhd.com", StringComparison.OrdinalIgnoreCase), template);

            Check(checks, "no query string was persisted",
                !template.Contains('?', StringComparison.Ordinal), template);

            var hash = Convert.ToString(row["response_content_hash"]) ?? "";

            Check(checks, "provenance content hash matches a run artifact",
                outcome.Run.Artifacts.Any(a => string.Equals(a.Value, hash, StringComparison.Ordinal)),
                hash);

            // Archive.
            if (hash.Length == 64)
            {
                var payload = Path.Combine(archiveRoot, hash[..2], hash[2..4], hash + ".bin");
                var sidecar = Path.Combine(archiveRoot, hash[..2], hash[2..4], hash + ".json");

                Check(checks, "the archived payload exists", File.Exists(payload), payload);
                Check(checks, "the archive sidecar exists", File.Exists(sidecar), sidecar);

                if (File.Exists(payload))
                {
                    var length = new FileInfo(payload).Length;
                    var recorded = Convert.ToInt64(row["response_byte_length"]);

                    Check(checks, "archived byte length matches the provenance row",
                        length == recorded, length + " on disk, " + recorded + " recorded");

                    report["ArchivedPayloadBytes"] = length;

                    if (length <= 4096)
                    {
                        report["ArchivedPayloadPreview"] = Trim(await File.ReadAllTextAsync(payload).ConfigureAwait(false));
                    }
                }
            }
        }

        // Observations.
        var vldrAfter = await SymbolFactsAsync(connection).ConfigureAwait(false);

        report["VldrObservationsAfter"] = vldrAfter.Count;
        report["VldrEarliestAfter"] = vldrAfter.MinAsOf?.ToString("o", CultureInfo.InvariantCulture);
        report["VldrLatestAfter"] = vldrAfter.MaxAsOf?.ToString("o", CultureInfo.InvariantCulture);

        var inserted = await InsertedObservationsAsync(connection, from.Value, to.Value).ConfigureAwait(false);
        report["InsertedObservations"] = inserted;

        Console.WriteLine("  VLDR observations   : " + vldrBefore.Count + " -> " + vldrAfter.Count);
        Console.WriteLine("  inserted rows       : " + inserted.Count);

        foreach (var row in inserted.Take(25))
        {
            Console.WriteLine("    " + row["as_of_utc"] + "  " + row["attribute"] + "  " + row["value"]
                + "  retrieved=" + row["retrieved_at_utc"]);
        }

        Console.WriteLine();

        // Duplicate groups, the Gate 12 shape.
        var duplicates = await DuplicateGroupsAsync(connection).ConfigureAwait(false);
        report["DuplicateGroups"] = duplicates;

        Check(checks, "no duplicate observation groups for VLDR", duplicates.Count == 0,
            duplicates.Count + " group(s)");

        Check(checks, "no observation was inserted outside the requested window",
            inserted.Count == 0 || inserted.All(r =>
            {
                var asOf = (DateTime)r["as_of_utc"]!;

                return asOf.Date >= from.Value.Date && asOf.Date <= to.Value.Date;
            }),
            inserted.Count == 0 ? "no rows inserted" : "all within window");

        Check(checks, "exactly one new ingestion run", deltas["ingestion_runs"] == 1,
            deltas["ingestion_runs"].ToString(CultureInfo.InvariantCulture));

        Check(checks, "the observation delta equals what normalisation reported",
            deltas["observations"] == (outcome.Normalization?.ObservationsRecorded ?? 0),
            deltas["observations"] + " vs " + (outcome.Normalization?.ObservationsRecorded ?? 0));

        // ---- final -------------------------------------------------------------------------------
        Console.WriteLine("--- checks");

        foreach (var check in checks)
        {
            Console.WriteLine("  " + ((bool)check["Pass"]! ? "PASS  " : "FAIL  ") + check["Name"] + "   [" + check["Actual"] + "]");
        }

        var finalFailed = checks.Count(c => !(bool)c["Pass"]!);

        Console.WriteLine();
        Console.WriteLine("  " + checks.Count + " check(s), " + finalFailed + " failed.");
        Console.WriteLine();
        Console.WriteLine("  ai_investment was NOT touched. Exactly one provider request was made.");
        Console.WriteLine();

        return Finish(report, checks, root, mode, finalFailed == 0 ? 0 : 9);
    }

    // ---------------------------------------------------------------------------------------------

    private static DateTime? ParseDate(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    private static string Trim(string text) =>
        text.Length <= 400 ? text : text[..400] + "…";

    private static void Check(List<Dictionary<string, object?>> checks, string name, bool pass, string actual) =>
        checks.Add(new Dictionary<string, object?> { ["Name"] = name, ["Pass"] = pass, ["Actual"] = actual });

    private static async Task<Dictionary<string, long>> CountsAsync(System.Data.Common.DbConnection connection)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in TrackedTables)
        {
            await using var cmd = connection.CreateCommand();

            // The table names are a private static array of literals in this file; nothing from
            // outside reaches this string.
            cmd.CommandText = "SELECT count(*) FROM public." + table;
            counts[table] = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        }

        return counts;
    }

    private static async Task<(long Count, DateTime? MinAsOf, DateTime? MaxAsOf)> SymbolFactsAsync(
        System.Data.Common.DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT count(*), min(as_of_utc), max(as_of_utc) FROM public.observations " +
            "WHERE subject_identifier = @s";

        var p = cmd.CreateParameter();
        p.ParameterName = "s";
        p.Value = Symbol;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);

        var count = reader.GetInt64(0);
        var min = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
        var max = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);

        return (count, min, max);
    }

    private static async Task<List<Dictionary<string, object?>>> ExchangesAsync(
        System.Data.Common.DbConnection connection,
        Guid runId)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, ingestion_run_id, exchange_ordinal, source_id, request_fingerprint, " +
            "endpoint_template, redacted_request_parameters::text, requested_from_utc, " +
            "requested_to_utc, http_status_code, selected_response_headers::text, " +
            "retrieved_at_utc, response_content_hash, response_byte_length, " +
            "provider_correlation_id, recorded_at_utc " +
            "FROM public.provider_exchanges WHERE ingestion_run_id = @r ORDER BY exchange_ordinal";

        var p = cmd.CreateParameter();
        p.ParameterName = "r";
        p.Value = runId;
        cmd.Parameters.Add(p);

        return await ReadAllAsync(cmd).ConfigureAwait(false);
    }

    /// <summary>
    /// The rows this run added.
    /// </summary>
    /// <remarks>
    /// Identified by the window rather than by an id watermark, which is exact here precisely
    /// because a precondition already established that no stored observation for this symbol falls
    /// inside the requested window. If that precondition had failed the request would not have been
    /// sent, so there is no case in which this over-counts.
    /// </remarks>
    private static async Task<List<Dictionary<string, object?>>> InsertedObservationsAsync(
        System.Data.Common.DbConnection connection,
        DateTime from,
        DateTime to)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, subject_identifier, attribute, value, as_of_utc, retrieved_at_utc, " +
            "source_id, source_record_id, published_at_utc " +
            "FROM public.observations WHERE subject_identifier = @s " +
            "AND as_of_utc >= @f AND as_of_utc < @t ORDER BY as_of_utc, attribute";

        var p = cmd.CreateParameter();
        p.ParameterName = "s";
        p.Value = Symbol;
        cmd.Parameters.Add(p);

        var pf = cmd.CreateParameter();
        pf.ParameterName = "f";
        pf.Value = DateTime.SpecifyKind(from.Date, DateTimeKind.Unspecified);
        cmd.Parameters.Add(pf);

        var pt = cmd.CreateParameter();
        pt.ParameterName = "t";
        pt.Value = DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Unspecified);
        cmd.Parameters.Add(pt);

        return await ReadAllAsync(cmd).ConfigureAwait(false);
    }

    private static async Task<List<Dictionary<string, object?>>> DuplicateGroupsAsync(
        System.Data.Common.DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT subject_identifier, attribute, as_of_utc, source_id, count(*) AS n " +
            "FROM public.observations WHERE subject_identifier = @s " +
            "GROUP BY subject_identifier, attribute, as_of_utc, source_id HAVING count(*) > 1";

        var p = cmd.CreateParameter();
        p.ParameterName = "s";
        p.Value = Symbol;
        cmd.Parameters.Add(p);

        return await ReadAllAsync(cmd).ConfigureAwait(false);
    }

    private static async Task<(bool Exists, bool Active)> SourceFactsAsync(System.Data.Common.DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT is_active FROM public.data_sources WHERE id = @i";

        var p = cmd.CreateParameter();
        p.ParameterName = "i";
        p.Value = PriceSourceId;
        cmd.Parameters.Add(p);

        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);

        return value is null || value is DBNull ? (false, false) : (true, Convert.ToBoolean(value));
    }

    private static async Task<List<Dictionary<string, object?>>> ReadAllAsync(System.Data.Common.DbCommand cmd)
    {
        var rows = new List<Dictionary<string, object?>>();

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static int Abort(
        Dictionary<string, object?> report,
        List<Dictionary<string, object?>> checks,
        string root,
        string mode,
        int code,
        string message)
    {
        Console.WriteLine();
        Console.WriteLine("  STOPPING. " + message);
        Console.WriteLine();

        report["Aborted"] = true;
        report["AbortReason"] = message;

        return Finish(report, checks, root, mode, code);
    }

    private static int Finish(
        Dictionary<string, object?> report,
        List<Dictionary<string, object?>> checks,
        string root,
        string mode,
        int code)
    {
        report["Checks"] = checks;
        report["ExitCode"] = code;

        try
        {
            var directory = Path.Combine(root, "artifacts", "verify");
            Directory.CreateDirectory(directory);

            var name = mode == "preflight"
                ? "controlled-vldr-live-price-pilot-preflight.json"
                : "controlled-vldr-live-price-pilot-run.json";

            var path = Path.Combine(directory, name);

            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));

            Console.WriteLine("  Evidence: " + path);
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (could not write the evidence file: " + ex.Message + ")");
        }

        return code;
    }
}
