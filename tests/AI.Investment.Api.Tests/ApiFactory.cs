using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Boots the real API host in-process.
/// </summary>
/// <remarks>
/// Configuration is supplied in memory so the host's start-up validation passes without a
/// deployed environment. The connection string is syntactically valid but points nowhere: these
/// tests exercise the pipeline - routing, correlation, problem details, liveness - and must not
/// require a database. Endpoints that need one are covered by the integration tests, against a
/// real database.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
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
            });
        });
    }
}
