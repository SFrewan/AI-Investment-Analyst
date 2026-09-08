using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace AI.Investment.Tools.VldrForensics;

/// <summary>
/// What the one live pilot request actually left behind, read only.
/// </summary>
/// <remarks>
/// <para>
/// Raw Npgsql and SELECT only. No DbContext, no composition, no provider, no migrator - nothing
/// here can write a row or send a request. The pilot threw after its request had been made, so the
/// question this answers is what state that left, and a tool that could change that state is the
/// wrong tool to ask with.
/// </para>
/// <para>
/// It refuses ai_investment for the same reason the pilot does.
/// </para>
/// </remarks>
internal static class Program
{
    private const string RequiredDatabase = "ai_investment_tests";
    private const string ForbiddenDatabase = "ai_investment";
    private const string Symbol = "VLDR.US";

    private static readonly string[] TrackedTables =
    [
        "observations", "ingestion_runs", "processed_actions", "action_executions",
        "quarantined_payloads", "provider_exchanges", "audit_records", "data_sources",
    ];

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   VLDR PILOT FORENSICS - READ ONLY");
        Console.WriteLine("   SELECT only. No provider. No writes. No repair.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        var raw = Environment.GetEnvironmentVariable("AIINV_TEST_POSTGRES");

        if (string.IsNullOrWhiteSpace(raw))
        {
            Console.WriteLine("  STOPPING. AIINV_TEST_POSTGRES is not set.");

            return 3;
        }

        var builder = new NpgsqlConnectionStringBuilder(raw) { Database = RequiredDatabase };

        if (string.Equals(builder.Database, ForbiddenDatabase, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  STOPPING. The resolved database is " + ForbiddenDatabase + ".");

            return 4;
        }

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["Host"] = builder.Host,
            ["Database"] = builder.Database,
            ["ReadOnly"] = true,
        };

        Console.WriteLine("--- identity");
        Console.WriteLine("  current_database()  : " + await ScalarAsync(connection, "SELECT current_database()").ConfigureAwait(false));
        Console.WriteLine("  server              : " + connection.ServerVersion);
        Console.WriteLine();

        // ---- counts ----------------------------------------------------------------------------
        Console.WriteLine("--- counts");

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in TrackedTables)
        {
            counts[table] = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT count(*) FROM public." + table).ConfigureAwait(false));

            Console.WriteLine(("  " + table).PadRight(24) + ": " + counts[table]);
        }

        report["Counts"] = counts;
        Console.WriteLine();

        // ---- the run ----------------------------------------------------------------------------
        await DumpAsync(connection, report, "IngestionRuns", "--- ingestion_runs",
            "SELECT id, started_at_utc, completed_at_utc, outcome, reason, refusal_rule_id, " +
            "artifacts, request_fingerprint, source_id, category, region, correlation_id, " +
            "requested_at_utc, subject_kind, subject_identifier, window_start_utc, window_end_utc " +
            "FROM public.ingestion_runs ORDER BY started_at_utc").ConfigureAwait(false);

        // ---- provenance --------------------------------------------------------------------------
        await DumpAsync(connection, report, "ProviderExchanges", "--- provider_exchanges",
            "SELECT id, ingestion_run_id, exchange_ordinal, source_id, request_fingerprint, " +
            "endpoint_template, redacted_request_parameters::text, requested_from_utc, " +
            "requested_to_utc, http_status_code, selected_response_headers::text, retrieved_at_utc, " +
            "response_content_hash, response_byte_length, provider_correlation_id, recorded_at_utc " +
            "FROM public.provider_exchanges ORDER BY recorded_at_utc").ConfigureAwait(false);

        // ---- observations -------------------------------------------------------------------------
        await DumpAsync(connection, report, "VldrObservations", "--- observations for " + Symbol,
            "SELECT id, attribute, value, as_of_utc, retrieved_at_utc, source_id, source_record_id " +
            "FROM public.observations WHERE subject_identifier = '" + Symbol + "' " +
            "ORDER BY as_of_utc, attribute").ConfigureAwait(false);

        // ---- quarantine ---------------------------------------------------------------------------
        await DumpAsync(connection, report, "QuarantinedPayloads", "--- quarantined_payloads",
            "SELECT * FROM public.quarantined_payloads").ConfigureAwait(false);

        // ---- the seam -----------------------------------------------------------------------------
        await DumpAsync(connection, report, "ActionExecutions", "--- action_executions",
            "SELECT * FROM public.action_executions ORDER BY 1").ConfigureAwait(false);

        await DumpAsync(connection, report, "ProcessedActions", "--- processed_actions",
            "SELECT * FROM public.processed_actions ORDER BY 1").ConfigureAwait(false);

        // ---- the archive on disk --------------------------------------------------------------------
        Console.WriteLine("--- pilot archive on disk");

        var archiveRoot = Path.Combine(root, "artifacts", "verify", "pilot-archive");
        var files = Directory.Exists(archiveRoot)
            ? Directory.GetFiles(archiveRoot, "*", SearchOption.AllDirectories)
            : [];

        var archive = new List<Dictionary<string, object?>>();

        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);

            Console.WriteLine("  " + info.Name + "   " + info.Length + " bytes");

            var entry = new Dictionary<string, object?>
            {
                ["Name"] = info.Name,
                ["Bytes"] = info.Length,
                ["Path"] = file,
            };

            if (info.Length <= 8192)
            {
                entry["Content"] = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            }

            archive.Add(entry);
        }

        if (archive.Count == 0)
        {
            Console.WriteLine("  (nothing)");
        }

        report["PilotArchive"] = archive;
        Console.WriteLine();

        var path = Path.Combine(root, "artifacts", "verify", "controlled-vldr-live-price-pilot-forensics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        Console.WriteLine("  Evidence: " + path);
        Console.WriteLine();
        Console.WriteLine("  Nothing was written to the database. No provider was contacted.");
        Console.WriteLine();

        return 0;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);

        return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static async Task DumpAsync(
        NpgsqlConnection connection,
        Dictionary<string, object?> report,
        string key,
        string heading,
        string sql)
    {
        Console.WriteLine(heading);

        var rows = new List<Dictionary<string, object?>>();

        await using (var cmd = new NpgsqlCommand(sql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }

                rows.Add(row);
            }
        }

        foreach (var row in rows)
        {
            foreach (var pair in row)
            {
                Console.WriteLine(("    " + pair.Key).PadRight(34) + ": " + pair.Value);
            }

            Console.WriteLine();
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("  (no rows)");
            Console.WriteLine();
        }

        report[key] = rows;
    }
}
