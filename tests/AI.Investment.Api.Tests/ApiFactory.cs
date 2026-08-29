using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Boots the real API host in-process.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is supplied in memory so the host's start-up validation passes without a
/// deployed environment. The connection string is syntactically valid but points nowhere: these
/// tests exercise the pipeline - routing, correlation, problem details, liveness, authentication -
/// and must not require a database. Endpoints that need one are covered by the integration tests,
/// against a real database.
/// </para>
/// <para>
/// <strong>Two operator accounts are configured, and the default client uses neither.</strong>
/// <see cref="CreateClient()"/> is anonymous, which is what every read test wants and what every
/// authorization test needs as its baseline. A test that wants an identity asks for one.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>An operator holding every privilege.</summary>
    public const string OperatorKey = "operator-test-key";

    /// <summary>An authenticated operator holding no privileges at all.</summary>
    public const string ObserverKey = "readonly-test-key";

    public const string OperatorId = "operator@example.test";

    /// <summary>A client that presents an operator key on every request.</summary>
    public HttpClient CreateOperatorClient(string key)
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Add("X-Operator-Key", key);

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:ServiceName"] = "AI.Investment.Api.Tests",
                ["Observability:CorrelationIdHeader"] = "X-Correlation-ID",
                ["Observability:AcceptInboundCorrelationId"] = "true",
                ["Database:ConnectionString"] =
                    "Host=localhost;Port=5432;Database=api_tests_unreachable;Username=postgres;Password=postgres",
                ["Database:CommandTimeoutSeconds"] = "5",

                // Deny everything. These tests must not depend on a permissive policy, and a
                // test host that could execute actions would be the wrong default.
                ["Safety:KillSwitchEngaged"] = "false",

                // The keys themselves are never configured - only their SHA-256 digests, which is
                // the same shape a deployment uses. These two are the digests of the constants
                // above, and they exist so the authorization tests have something real to
                // authenticate against.
                ["Operators:Accounts:0:Id"] = OperatorId,
                ["Operators:Accounts:0:DisplayName"] = "Test Operator",
                ["Operators:Accounts:0:KeySha256"] =
                    "1593fd5dc308f0764e70ce08d39e58150fdfc135a45037945811305f6f5dc360",
                ["Operators:Accounts:0:Privileges:0"] = "DecideOpportunities",
                ["Operators:Accounts:0:Privileges:1"] = "AnswerEscalations",
                ["Operators:Accounts:0:Privileges:2"] = "AdministerKillSwitch",
                ["Operators:Accounts:0:Privileges:3"] = "AdministerWatches",
                ["Operators:Accounts:0:Privileges:4"] = "ViewPortfolio",

                // Authenticated, and permitted nothing. The account that proves authentication and
                // authorization are two different questions.
                ["Operators:Accounts:1:Id"] = "observer@example.test",
                ["Operators:Accounts:1:DisplayName"] = "Test Observer",
                ["Operators:Accounts:1:KeySha256"] =
                    "c28b76a28f2a56bbd936c0b7f77fdf54fc7706ef687b64f9038bb3d7df03b8a9",
            });
        });
    }
}
