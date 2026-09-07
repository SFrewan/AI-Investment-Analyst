using System.Net;
using System.Text;
using System.Text.Json;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// One billable request, cached whole, so it never has to be made again.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Exactly one call, enforced by the transport.</strong> <see cref="BoundedCalls"/> throws
/// on a second send, so a redirect, an internal retry or a later edit fails this test rather than
/// spending a request nobody approved. No price history is fetched, no symbol is priced and no
/// acquisition is started: the response is a list of names and codes.
/// </para>
/// <para>
/// <strong>Cached whole, and that is the point of the exercise.</strong> The manifest stage already
/// read this list once, used it for a membership test, and threw it away - which is why sixteen
/// dead companies could not be named afterwards without paying for it again. The complete response
/// is written to <c>artifacts/universe/eodhd-delisted-us.json</c> exactly as it arrived, so every
/// future match runs against a fixed artefact instead of against whatever a later fetch returns.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_DELISTED=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseDelistedListTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_DELISTED";

    /// <summary>The authorised budget for this stage: one request, and no second one.</summary>
    private const int CallBudget = 1;

    /// <summary>The fewest symbols a real US delisted list can hold.</summary>
    /// <remarks>
    /// Tens of thousands of US tickers have stopped trading. A few hundred would mean a truncated
    /// or filtered answer, and caching one as though it were the list would make every future
    /// unmatched company look like a company with no ticker rather than like a bad cache.
    /// </remarks>
    private const int PlausibleSymbols = 1000;

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseDelistedListTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_delisted_symbol_list_is_fetched_once_and_kept()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The delisted-list fetch is off. Set {GateVariable}=1 to run it. It makes exactly one " +
            "billable call and fetches no price history.");

        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<EodhdOptions>>().Value;
        var presence = CredentialPresence.Of(options.ApiKey);

        Skip.IfNot(
            presence.IsConfigured,
            "No EODHD credential is configured. Run scripts/check-eodhd-credential.cmd first.");

        var path = Universe.RepositoryPath("artifacts", "universe", "eodhd-delisted-us.json");

        if (File.Exists(path))
        {
            _output.WriteLine("The delisted list is already cached. No call was made.");

            return;
        }

        using var budget = new BoundedCalls(CallBudget)
        {
            InnerHandler = new HttpClientHandler(),
        };

        using var client = new HttpClient(budget)
        {
            BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(3),
        };

        // The credential is in the query because that is how EODHD authenticates. Built here, used
        // once, and never written to the cache, a log or a report - the body is what is kept, and
        // the body carries no token.
        var uri = new Uri(
            "api/exchange-symbol-list/US?fmt=json&delisted=1&api_token=" +
            Uri.EscapeDataString(options.ApiKey),
            UriKind.Relative);

        using var response = await client.GetAsync(uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        var symbols = Count(body);

        Assert.True(
            symbols >= PlausibleSymbols,
            Universe.Inv($"The list came back with {symbols} symbols, fewer than the ") +
            Universe.Inv($"{PlausibleSymbols} a real US delisted list holds. Caching a truncated ") +
            "answer would make every unmatched company look unmappable rather than uncached.");

        await Universe.WriteAsync(path, body);

        var report = new StringBuilder();

        report.AppendLine("# The delisted symbol list - one call, cached whole");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Credential fingerprint `{presence.Fingerprint}` - the token in force."));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Requests sent | **{budget.Sent}** of {CallBudget} authorised |"));
        report.AppendLine(Universe.Inv($"| Symbols cached | {symbols} |"));
        report.AppendLine(Universe.Inv($"| Bytes | {body.Length} |"));
        report.AppendLine("| Cached to | `artifacts/universe/eodhd-delisted-us.json` |");
        report.AppendLine();
        report.AppendLine("No price history was requested. No symbol was priced. Nothing was");
        report.AppendLine("ingested, archived or written to the observation store.");

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "delisted-list.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        Assert.Equal(CallBudget, budget.Sent);
    }

    private static int Count(string body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.GetArrayLength()
            : 0;
    }
}
