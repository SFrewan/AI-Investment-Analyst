using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace AI.Investment.Integration.Tests;

/// <summary>
/// Provides a real PostgreSQL database for the integration tests.
/// </summary>
/// <remarks>
/// <para>
/// Real PostgreSQL, not an in-memory provider. The things worth testing here - a unique-index
/// violation, jsonb round-tripping, an ILIKE query, a real migration - are exactly the things an
/// in-memory provider does not have. A test that passes against a fake database proves the test
/// runs, not that the system works.
/// </para>
/// <para>
/// Two ways to get one, in order: the connection string in <c>AIINV_TEST_POSTGRES</c>, or a
/// Testcontainers container. If neither is available the fixture reports
/// <see cref="Available"/> = false and every dependent test <strong>skips</strong> via
/// <c>Skip.IfNot</c> - a missing Docker daemon is an environment gap, not a defect in the code.
/// </para>
/// <para>
/// <strong>Skipping, not returning early.</strong> These tests used to check
/// <see cref="Available"/>, print "SKIPPED" to the console and <c>return</c>. A test that returns
/// normally is reported <em>Passed</em>, so eight tests covering the persistence half of the
/// safety seam were counted green on every machine without Docker while asserting nothing - and
/// the only evidence otherwise was a console line nobody reads. The word in the summary now
/// matches what happened. xUnit 2.x cannot skip dynamically on its own, which is why
/// <c>Xunit.SkippableFact</c> is referenced.
/// </para>
/// <para>
/// CI must supply one of the two, or these tests prove nothing - and now say so.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable = "AIINV_TEST_POSTGRES";

    /// <summary>
    /// The suffix a database must carry before this fixture will run the migrations into it and
    /// empty it between tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a safety interlock, not a naming preference.</strong>
    /// <see cref="ResetAsync"/> truncates every mapped table, and the development database sits on
    /// the same server, under the same credentials, one word away in the connection string. A
    /// mistyped environment variable would otherwise silently destroy a developer's working data
    /// on the next test run, and the test suite would report success while doing it.
    /// </para>
    /// <para>
    /// A name check is a weak proof of intent, which is exactly why it fails loudly rather than
    /// skipping: a suite pointed at the wrong database is a configuration error to be seen, not an
    /// environment gap to be tolerated.
    /// </para>
    /// </remarks>
    public const string RequiredDatabaseSuffix = "_tests";

    /// <summary>
    /// Empties every table the model maps, in one statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A compile-time constant on purpose. Built by interpolating table names out of
    /// <c>context.Model</c> it would be a non-constant command string, which is the shape CA2100
    /// exists to flag and which no reviewer can check by reading. Written out, the statement says
    /// precisely what it empties.
    /// </para>
    /// <para>
    /// Staleness is the cost of writing it out, so it is not left to vigilance:
    /// <c>DatabaseResetCoverageTests</c> compares this statement against the tables the EF model
    /// actually maps and fails when a new table is not listed here. A table missing from this
    /// statement would leak rows between tests, which is the defect this whole mechanism exists to
    /// remove.
    /// </para>
    /// <para>
    /// <c>CASCADE</c> is required by PostgreSQL whenever a truncated table is referenced by a
    /// foreign key from another table; every table in this model is listed, so it truncates nothing
    /// that is not already named here.
    /// </para>
    /// </remarks>
    internal const string TruncateStatement =
        """
        TRUNCATE TABLE
            "public"."action_executions",
            "public"."audit_records",
            "public"."companies",
            "public"."data_sources",
            "public"."ingestion_runs",
            "public"."observations",
            "public"."processed_actions",
            "public"."quarantined_payloads",
            "public"."unreplayable_evidence"
        RESTART IDENTITY CASCADE;
        """;

    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public bool Available => !string.IsNullOrWhiteSpace(ConnectionString);

    public string UnavailableReason { get; private set; } =
        "No database was available.";

    public async Task InitializeAsync()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            // Deliberately outside the catch below. A connection string that was configured on
            // purpose and does not work is a broken configuration, and must fail the run rather
            // than quietly turn every dependent test into a skip.
            EnsureDedicatedTestDatabase(fromEnvironment);

            ConnectionString = fromEnvironment;
            await MigrateAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("ai_investment_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync().ConfigureAwait(false);
            ConnectionString = _container.GetConnectionString();

            await MigrateAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure to obtain a database is an environment gap, and
                              // must be reported as such rather than as a test failure.
        catch (Exception ex)
        {
            ConnectionString = null;
            UnavailableReason =
                $"Could not start a PostgreSQL container ({ex.GetType().Name}). " +
                $"Set {ConnectionStringEnvironmentVariable} to a reachable database whose name ends " +
                $"in '{RequiredDatabaseSuffix}', or start Docker.";
        }
#pragma warning restore CA1031
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a context bound to the test database.</summary>
    public AppDbContext CreateContext(IWriteAuthorization writeAuthorization)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new AppDbContext(options, writeAuthorization);
    }

    /// <summary>
    /// Returns the database to the state the migrations left it in: schema present, no rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by each test class's <see cref="IAsyncLifetime.InitializeAsync"/>, so every test
    /// starts from the same empty database no matter what ran before it or how many times the
    /// suite has been run against this server. The alternative that was in place - one long-lived
    /// database and hard-coded identifiers - meant the first run passed and every run after it
    /// failed on <c>23505 duplicate key</c>, which says nothing about the code.
    /// </para>
    /// <para>
    /// Truncation, not <c>EnsureDeleted</c>/<c>EnsureCreated</c>: the schema under test must stay
    /// the one the migrations produced, and it must survive so that a test can write through one
    /// context and read back through a second. It empties tables; it never drops them.
    /// </para>
    /// <para>
    /// Test classes sharing <see cref="SharedPostgresDatabase"/> run one at a time - that is what
    /// an xUnit collection means - so no test can be emptied out from under another.
    /// </para>
    /// </remarks>
    public async Task ResetAsync()
    {
        if (!Available)
        {
            // The dependent test is about to skip on the same condition. Nothing to empty, and
            // nothing to report from here.
            return;
        }

        await using var context = CreateContext(new NeverAuthorized());

        await context.Database.ExecuteSqlRawAsync(TruncateStatement).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a connection string that does not name a database dedicated to these tests.
    /// </summary>
    private static void EnsureDedicatedTestDatabase(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;

        if (database is not null &&
            database.EndsWith(RequiredDatabaseSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{ConnectionStringEnvironmentVariable} names the database '{database ?? "(none)"}'. " +
            $"The integration tests empty every table between tests, so they will only run against " +
            $"a database dedicated to them - one whose name ends in '{RequiredDatabaseSuffix}'. " +
            $"Point this variable at a database such as 'ai_investment_tests', not at the " +
            $"development database.");
    }

    /// <summary>
    /// Brings the test database to the schema the migrations describe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real migration path, which is the point of testing against a real database: these tests
    /// exercise the same DDL that will be applied to a deployed environment, so a migration that is
    /// wrong fails here rather than on release. It replaces <c>EnsureCreated</c>, which built the
    /// schema from the model and therefore proved nothing about the migrations at all.
    /// </para>
    /// <para>
    /// <strong>The discard below is what makes that switch possible on a machine that ran the old
    /// fixture.</strong> <c>EnsureCreated</c> writes no <c>__EFMigrationsHistory</c>, so the
    /// database it leaves behind has every table and no record of any migration. <c>Migrate</c>
    /// then starts from the beginning and the first statement fails with
    /// <c>42P07: relation "action_executions" already exists</c> - which says nothing about the
    /// migrations and cannot be recovered from by running them again.
    /// </para>
    /// <para>
    /// A database that exists with no applied migrations is therefore treated as what it is: not a
    /// migrated database, and not one this fixture can migrate. It is dropped and rebuilt from the
    /// migrations. This is narrow on purpose - the moment one migration is recorded, the condition
    /// is false forever after, and a real migrated database is never dropped. It is also confined
    /// to a database that already had to pass <see cref="EnsureDedicatedTestDatabase"/>.
    /// </para>
    /// </remarks>
    private async Task MigrateAsync()
    {
        await using var context = CreateContext(new NeverAuthorized());

        var creator = context.Database.GetService<IRelationalDatabaseCreator>();

        if (await creator.ExistsAsync().ConfigureAwait(false))
        {
            var applied = await context.Database.GetAppliedMigrationsAsync().ConfigureAwait(false);

            if (!applied.Any())
            {
                await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
            }
        }

        // Creates the database when it does not exist, then applies every migration in order.
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private sealed class NeverAuthorized : IWriteAuthorization
    {
        public bool IsAuthorized => false;

        public Guid? AuthorizingDecisionId => null;

        public IDisposable Authorize(PolicyDecision decision) =>
            throw new NotSupportedException("Schema management does not authorise domain writes.");
    }
}

/// <summary>
/// Groups the test classes that share one <see cref="PostgresFixture"/>, so a single database is
/// started for all of them rather than one per class.
/// </summary>
/// <remarks>
/// Named to avoid the 'Collection' suffix (CA1711), which is reserved for types that actually
/// are collections - this is a marker. xUnit identifies the group by the string passed to
/// <c>[CollectionDefinition]</c> and <c>[Collection]</c>, so the class name is free; using
/// <c>nameof</c> keeps the two ends of that string in sync at compile time.
/// </remarks>
[CollectionDefinition(nameof(SharedPostgresDatabase))]
public sealed class SharedPostgresDatabase : ICollectionFixture<PostgresFixture>;
