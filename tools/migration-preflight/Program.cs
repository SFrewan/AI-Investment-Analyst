using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace AI.Investment.Tools.MigrationPreflight;

/// <summary>
/// READ-ONLY preflight for applying the provenance migration to the production evidence database.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately built on raw <see cref="NpgsqlConnection"/> rather than the EF context. EF can create,
/// migrate and save; a preflight that reports on a database it must not change should not be able to
/// change it. Every statement in this file is a SELECT, and there is no DbContext, no migrator and no
/// SaveChanges anywhere in the object graph.
/// </para>
/// <para>
/// It opens two connections: the evidence database, to describe what is there now, and the test
/// database, to read back the schema the same migration actually produced. Comparing the two is how
/// "schema identity" is established from measurement rather than from reading the migration twice.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Evidence = "ai_investment";
    private const string Tests = "ai_investment_tests";

    private static readonly string[] ExpectedIndexes =
    [
        "ux_provider_exchanges_run_ordinal",
        "ix_provider_exchanges_ingestion_run_id",
        "ix_provider_exchanges_request_fingerprint",
        "ix_provider_exchanges_response_content_hash",
        "ix_provider_exchanges_retrieved_at_utc",
    ];

    private static readonly string[] CountedTables =
    [
        "observations", "ingestion_runs", "processed_actions", "action_executions", "quarantined_payloads",
    ];

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        Console.WriteLine();
        Console.WriteLine("  ================================================================");
        Console.WriteLine("   PRODUCTION PROVENANCE MIGRATION - PREFLIGHT   (READ ONLY)");
        Console.WriteLine("   SELECT only. No migration applied. No provider. No replay.");
        Console.WriteLine("  ================================================================");
        Console.WriteLine();

        var designTime = Environment.GetEnvironmentVariable("AIINV_DESIGNTIME_DB");
        var testDb = Environment.GetEnvironmentVariable("AIINV_TEST_POSTGRES");

        var report = new Dictionary<string, object?>
        {
            ["RanAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["Mode"] = "READ ONLY - raw Npgsql, SELECT only, no DbContext and no migrator in this tool",
            ["Framework"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        };

        // ---- 1. resolve the configured targets, without printing a credential ----
        var designTimeName = DatabaseOf(designTime);
        var testName = DatabaseOf(testDb);

        report["AIINV_DESIGNTIME_DB_Present"] = !string.IsNullOrWhiteSpace(designTime);
        report["AIINV_DESIGNTIME_DB_Database"] = designTimeName;
        report["AIINV_TEST_POSTGRES_Present"] = !string.IsNullOrWhiteSpace(testDb);
        report["AIINV_TEST_POSTGRES_Database"] = testName;

        Console.WriteLine("--- configured targets (connection strings never printed)");
        Console.WriteLine("  AIINV_DESIGNTIME_DB  -> " + (designTimeName ?? "(not set)"));
        Console.WriteLine("  AIINV_TEST_POSTGRES  -> " + (testName ?? "(not set)"));
        Console.WriteLine();

        var evidenceConn = PickFor(Evidence, designTime, testDb);

        if (evidenceConn is null)
        {
            Console.WriteLine("  STOPPING. Neither configured connection names " + Evidence + ".");

            return 3;
        }

        // ---- 2 and 5 and 6. describe the evidence database ----
        var evidence = await DescribeAsync(evidenceConn, "evidence").ConfigureAwait(false);
        report["EvidenceDatabase"] = evidence;

        // ---- 7. read back the schema the same migration produced in the test database ----
        var testsConn = PickFor(Tests, testDb, designTime);
        Dictionary<string, object?>? tests = null;

        if (testsConn is not null)
        {
            tests = await DescribeAsync(testsConn, "tests").ConfigureAwait(false);
            report["TestDatabase"] = tests;
        }
        else
        {
            report["TestDatabase"] = null;
            Console.WriteLine("  NOTE  no configured connection names " + Tests + "; schema identity cannot be measured.");
            Console.WriteLine();
        }

        // ---- conclusions the tool can state from what it measured ----
        var evidenceHasTable = evidence["ProviderExchangesExists"] is true;
        var evidenceHistory = (string[])(evidence["MigrationHistory"] ?? Array.Empty<string>());
        var evidenceHasMigration = evidenceHistory.Any(m => m.Contains("ProviderExchangeProvenance", StringComparison.Ordinal));
        var conflicts = (string[])(evidence["NameConflicts"] ?? Array.Empty<string>());

        var testsHasTable = tests is not null && tests["ProviderExchangesExists"] is true;

        report["EvidenceAlreadyHasProviderExchanges"] = evidenceHasTable;
        report["EvidenceAlreadyHasTheMigration"] = evidenceHasMigration;
        report["EvidenceNameConflictCount"] = conflicts.Length;
        report["TestDatabaseHasProviderExchanges"] = testsHasTable;

        var additive = !evidenceHasTable && conflicts.Length == 0;

        report["ApplyingWouldBeAdditiveOnly"] = additive;
        report["MigrationApplied"] = false;

        Console.WriteLine("--- conclusions");
        Console.WriteLine("  provider_exchanges already in " + Evidence + " : " + evidenceHasTable);
        Console.WriteLine("  ProviderExchangeProvenance in its history  : " + evidenceHasMigration);
        Console.WriteLine("  name conflicts in " + Evidence + "          : " + conflicts.Length);
        Console.WriteLine("  provider_exchanges present in " + Tests + " : " + testsHasTable);
        Console.WriteLine("  applying would be additive only            : " + additive);
        Console.WriteLine();

        var outPath = Path.Combine(root, "artifacts", "verify", "production-provenance-migration-preflight.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

        Console.WriteLine("=== written: " + outPath);
        Console.WriteLine();
        Console.WriteLine("  NOTHING WAS APPLIED. NO SCHEMA WAS CHANGED. NO ROW WAS WRITTEN.");
        Console.WriteLine("  NO PROVIDER WAS CONTACTED. NOTHING WAS REPLAYED.");

        return additive ? 0 : 1;
    }

    /// <summary>The database a connection string names, without exposing the rest of it.</summary>
    private static string? DatabaseOf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) { return null; }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Database;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The first candidate that names the wanted database.</summary>
    private static string? PickFor(string wanted, params string?[] candidates) =>
        candidates.FirstOrDefault(c => string.Equals(DatabaseOf(c), wanted, StringComparison.Ordinal));

    private static async Task<Dictionary<string, object?>> DescribeAsync(string connectionString, string label)
    {
        var result = new Dictionary<string, object?>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var name = await ScalarAsync<string>(connection, "SELECT current_database()").ConfigureAwait(false);
        var version = await ScalarAsync<string>(connection, "SHOW server_version").ConfigureAwait(false);

        result["Label"] = label;
        result["Host"] = connection.Host;
        result["Port"] = connection.Port;
        result["Database"] = name;
        result["ServerVersion"] = connection.ServerVersion;
        result["PostgresServerVersion"] = version;

        Console.WriteLine("--- " + label + " database");
        Console.WriteLine("  host / port      : " + connection.Host + ":" + connection.Port);
        Console.WriteLine("  current_database : " + name);
        Console.WriteLine("  server_version   : " + version);

        // migration history
        var history = new List<string>();

        await using (var cmd = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", connection))
        await using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false)) { history.Add(reader.GetString(0)); }
        }

        result["MigrationHistory"] = history.ToArray();
        result["MigrationCount"] = history.Count;
        result["LatestMigration"] = history.Count > 0 ? history[^1] : null;

        Console.WriteLine("  migrations       : " + history.Count + ", latest " + (history.Count > 0 ? history[^1] : "(none)"));

        // does the table exist?
        var exists = await ScalarAsync<string>(connection,
            "SELECT COALESCE(to_regclass('public.provider_exchanges')::text, '')").ConfigureAwait(false);

        var hasTable = !string.IsNullOrEmpty(exists);
        result["ProviderExchangesExists"] = hasTable;

        Console.WriteLine("  provider_exchanges: " + (hasTable ? "PRESENT" : "absent"));

        // would anything collide?
        var conflicts = new List<string>();

        await using (var cmd = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE schemaname='public' AND indexname = ANY(@names)", connection))
        {
            cmd.Parameters.AddWithValue("names", ExpectedIndexes);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false)) { conflicts.Add("index " + reader.GetString(0)); }
        }

        await using (var cmd = new NpgsqlCommand(
            "SELECT conname FROM pg_constraint WHERE conname = 'PK_provider_exchanges'", connection))
        await using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false)) { conflicts.Add("constraint " + reader.GetString(0)); }
        }

        result["NameConflicts"] = conflicts.ToArray();

        Console.WriteLine("  name conflicts   : " + conflicts.Count);

        // the columns and indexes, when the table is there
        if (hasTable)
        {
            var columns = new List<Dictionary<string, object?>>();

            await using (var cmd = new NpgsqlCommand(
                "SELECT column_name, data_type, is_nullable, character_maximum_length " +
                "FROM information_schema.columns " +
                "WHERE table_schema='public' AND table_name='provider_exchanges' " +
                "ORDER BY ordinal_position", connection))
            await using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    columns.Add(new Dictionary<string, object?>
                    {
                        ["Name"] = reader.GetString(0),
                        ["Type"] = reader.GetString(1),
                        ["Nullable"] = reader.GetString(2),
                        ["MaxLength"] = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    });
                }
            }

            result["Columns"] = columns;

            var indexes = new List<Dictionary<string, object?>>();

            await using (var cmd = new NpgsqlCommand(
                "SELECT indexname, indexdef FROM pg_indexes " +
                "WHERE schemaname='public' AND tablename='provider_exchanges' ORDER BY indexname", connection))
            await using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    indexes.Add(new Dictionary<string, object?>
                    {
                        ["Name"] = reader.GetString(0),
                        ["Definition"] = reader.GetString(1),
                    });
                }
            }

            result["Indexes"] = indexes;
            result["RowCount"] = await ScalarAsync<long>(connection, "SELECT count(*) FROM provider_exchanges").ConfigureAwait(false);

            Console.WriteLine("  columns / indexes: " + columns.Count + " / " + indexes.Count
                + "   rows " + result["RowCount"]);
        }

        // the counts that must not move
        var counts = new Dictionary<string, long>();

        foreach (var table in CountedTables)
        {
            counts[table] = await ScalarAsync<long>(connection, "SELECT count(*) FROM \"" + table + "\"").ConfigureAwait(false);
        }

        result["Counts"] = counts;

        Console.WriteLine("  counts:");

        foreach (var kv in counts)
        {
            Console.WriteLine("    " + kv.Key.PadRight(22) + kv.Value.ToString(CultureInfo.InvariantCulture).PadLeft(10));
        }

        Console.WriteLine();

        await connection.CloseAsync().ConfigureAwait(false);

        return result;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);

        var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);

        return value is null or DBNull ? default! : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
