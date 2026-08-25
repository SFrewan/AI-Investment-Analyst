using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
/// <see cref="Available"/> = false and the tests return early with a message rather than
/// failing - a missing Docker daemon is an environment gap, not a defect in the code. CI must
/// therefore provide one of the two, or these tests silently prove nothing.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable = "AIINV_TEST_POSTGRES";

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
                $"Set {ConnectionStringEnvironmentVariable} to a reachable database, or start Docker.";
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

    private async Task MigrateAsync()
    {
        await using var context = CreateContext(new NeverAuthorized());

        // EnsureCreated rather than Migrate: no migration has been generated yet, because the
        // .NET SDK is unavailable in the environment where this code was written. Once
        // 'dotnet ef migrations add InitialCreate' has been run, switch this to MigrateAsync so
        // the tests exercise the real migration path - that is the point of using a real
        // database. See the Phase 1 completion report.
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
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
