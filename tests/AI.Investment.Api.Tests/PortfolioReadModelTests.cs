using System.Net;
using System.Text.Json;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The API host against a real database, for the endpoints whose answer is the point.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApiFactory"/> points at an unreachable database on purpose, which is right for the
/// authorization tests and useless for proving an endpoint answers. This one takes the same
/// connection string the integration fixture uses, from <c>AIINV_TEST_POSTGRES</c>, and applies
/// migrations before the first request so the schema is present whatever ran first.
/// </para>
/// <para>
/// <strong>It refuses any database whose name does not end in <c>_tests</c></strong>, the same
/// rule <c>PostgresFixture</c> enforces and for the same reason: the development database sits on
/// the same server under the same credentials, one word away.
/// </para>
/// <para>
/// Nothing here reaches a provider. Both endpoints are pure reads over stored rows, no connector
/// is enabled, the scheduler is off and the outbox dispatcher is off, so this makes no billable
/// request and starts no cycle.
/// </para>
/// </remarks>
public sealed class DatabaseApiFactory : WebApplicationFactory<Program>
{
    public const string ConnectionStringEnvironmentVariable = "AIINV_TEST_POSTGRES";

    private const string RequiredDatabaseSuffix = "_tests";

    /// <summary>The connection string, or null when none is configured or it is not a test database.</summary>
    public static string? ConnectionString { get; } = Resolve();

    public static bool Available => ConnectionString is not null;

    public static string UnavailableReason =>
        $"No test database. Set {ConnectionStringEnvironmentVariable} to a PostgreSQL connection "
        + $"string whose database name ends in '{RequiredDatabaseSuffix}'.";

    private readonly Lazy<Task> _schema;

    public DatabaseApiFactory() => _schema = new Lazy<Task>(MigrateAsync);

    /// <summary>A client presenting the operator key that holds every privilege.</summary>
    public HttpClient CreateOperatorClient()
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Add("X-Operator-Key", ApiFactory.OperatorKey);

        return client;
    }

    /// <summary>
    /// Applies migrations, once, however many tests ask.
    /// </summary>
    /// <remarks>
    /// Behind a <see cref="Lazy{T}"/> because xUnit builds one fixture per test class and every
    /// test in the class needs the schema. Migrating is idempotent, so repeating it would be
    /// correct and merely wasteful; doing it once is both.
    /// </remarks>
    public Task EnsureSchemaAsync() => _schema.Value;

    private async Task MigrateAsync()
    {
        using var scope = Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database
            .MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:ServiceName"] = "AI.Investment.Api.Tests.Database",
                ["Observability:CorrelationIdHeader"] = "X-Correlation-ID",
                ["Observability:AcceptInboundCorrelationId"] = "true",

                ["Database:ConnectionString"] = ConnectionString ?? string.Empty,
                ["Database:CommandTimeoutSeconds"] = "30",

                // Nothing runs on its own: no seeding, no sweep, no cycles, no dispatch.
                ["DataPlane:SeedSourcesOnStartup"] = "false",
                ["DataPlane:RunRetentionSweep"] = "false",
                ["OperationsHost:RunCycles"] = "false",
                ["OperationsHost:RunOutboxDispatcher"] = "false",

                ["Safety:KillSwitchEngaged"] = "false",

                // The same operator this project's other tests authenticate as.
                ["Operators:Accounts:0:Id"] = ApiFactory.OperatorId,
                ["Operators:Accounts:0:DisplayName"] = "Test Operator",
                ["Operators:Accounts:0:KeySha256"] =
                    "1593fd5dc308f0764e70ce08d39e58150fdfc135a45037945811305f6f5dc360",
                ["Operators:Accounts:0:Privileges:0"] = "DecideOpportunities",
                ["Operators:Accounts:0:Privileges:1"] = "AnswerEscalations",
                ["Operators:Accounts:0:Privileges:2"] = "AdministerKillSwitch",
                ["Operators:Accounts:0:Privileges:3"] = "AdministerWatches",
                ["Operators:Accounts:0:Privileges:4"] = "ViewPortfolio",
            });
        });
    }

    private static string? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        foreach (var segment in configured.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();

            if (!key.Equals("Database", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return value.EndsWith(RequiredDatabaseSuffix, StringComparison.OrdinalIgnoreCase)
                ? configured
                : null;
        }

        return null;
    }
}

/// <summary>
/// That both portfolio routes answer, against a database that exists.
/// </summary>
/// <remarks>
/// <para>
/// The proof <c>PortfolioEndpointTests</c> structurally cannot give. Those tests run against an
/// unreachable database, so a 500 is their normal outcome and no assertion there can tell a
/// broken container from an absent one. Here the database is real, so the only acceptable answer
/// is 200 with a body.
/// </para>
/// <para>
/// Deliberately shape assertions, not value assertions. The integration suite truncates this same
/// database between its own tests, so the book may be empty or may not be; what must hold either
/// way is that the endpoint answers, names its currency, reports counts that agree with the
/// positions it returned, and states a price availability for every holding rather than a bare
/// null.
/// </para>
/// </remarks>
public sealed class PortfolioReadModelTests : IClassFixture<DatabaseApiFactory>
{
    /// <summary>Hoisted so it is allocated once rather than on every loop iteration (CA1861).</summary>
    private static readonly string[] PriceAvailabilities = ["Available", "NoObservedPrice", "NotHeld"];

    private readonly DatabaseApiFactory _factory;

    public PortfolioReadModelTests(DatabaseApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task The_portfolio_endpoint_answers_with_a_snapshot()
    {
        Skip.IfNot(DatabaseApiFactory.Available, DatabaseApiFactory.UnavailableReason);

        await _factory.EnsureSchemaAsync();

        using var client = _factory.CreateOperatorClient();

        using var response = await client.GetAsync(new Uri("/api/portfolio", UriKind.Relative));

        // The assertion the old test could not make. A missing registration, a wrong lifetime or a
        // schema that never migrated all land here as something other than 200.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = document.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("currency").GetString()));

        // Asserted on the wire format rather than on a parsed DateTime's Kind: what a consumer of
        // this API actually receives is the string, and a stamp without the Z is the defect.
        var asAt = root.GetProperty("asAtUtc").GetString() ?? string.Empty;

        Assert.EndsWith("Z", asAt, StringComparison.Ordinal);

        var positions = root.GetProperty("positions");

        Assert.Equal(JsonValueKind.Array, positions.ValueKind);

        // The counts are a claim about the same list, so they have to agree with it.
        var open = root.GetProperty("openPositions").GetInt32();
        var valued = root.GetProperty("valuedPositions").GetInt32();
        var unvalued = root.GetProperty("unvaluedPositions").GetInt32();

        Assert.Equal(open, valued + unvalued);

        // The rule the read model exists to keep: a total is withheld unless everything was
        // valued, rather than reported smaller than the truth.
        if (!root.GetProperty("isFullyValued").GetBoolean())
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("totalValue").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("marketValue").ValueKind);
        }
    }

    [SkippableFact]
    public async Task The_positions_endpoint_answers_with_a_list()
    {
        Skip.IfNot(DatabaseApiFactory.Available, DatabaseApiFactory.UnavailableReason);

        await _factory.EnsureSchemaAsync();

        using var client = _factory.CreateOperatorClient();

        using var response = await client.GetAsync(
            new Uri("/api/portfolio/positions", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        foreach (var position in document.RootElement.EnumerateArray())
        {
            // Availability is a name, not a flag. A dash on a screen tells an operator nothing
            // about whether a feed is broken, and this is the assertion that keeps it a name.
            var availability = position.GetProperty("priceAvailability").GetString() ?? string.Empty;

            Assert.Contains(availability, PriceAvailabilities, StringComparer.Ordinal);

            Assert.False(string.IsNullOrWhiteSpace(position.GetProperty("instrument").GetString()));
        }
    }

    /// <summary>
    /// The privilege still governs, against a real database as much as against an absent one.
    /// </summary>
    /// <remarks>
    /// Repeated here deliberately. An endpoint that answered 500 for everyone was also refusing
    /// everyone, and the day it starts answering is the day its authorization matters.
    /// </remarks>
    [SkippableFact]
    public async Task An_anonymous_caller_is_still_refused()
    {
        Skip.IfNot(DatabaseApiFactory.Available, DatabaseApiFactory.UnavailableReason);

        await _factory.EnsureSchemaAsync();

        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/portfolio", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The surface stays read-only when there is a database behind it.</summary>
    [SkippableFact]
    public async Task The_surface_still_accepts_no_writes()
    {
        Skip.IfNot(DatabaseApiFactory.Available, DatabaseApiFactory.UnavailableReason);

        await _factory.EnsureSchemaAsync();

        using var client = _factory.CreateOperatorClient();

        using var post = await client.PostAsync(
            new Uri("/api/portfolio", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }
}
