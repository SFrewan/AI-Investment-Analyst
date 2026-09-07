using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// One call for one month of a ticker that no longer trades.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The assumption the whole survivorship design rests on.</strong> The expansion plan
/// builds point-in-time membership from SEC filings precisely so that companies which failed or
/// were acquired stay in the universe for the period they were reporting. That is worth nothing if
/// the price provider cannot serve their history: the dead names would be admitted to the manifest,
/// come back empty, and — if anyone quietly dropped them — survivorship bias would walk back in
/// through the acquisition step after being carefully excluded from the membership step.
/// </para>
/// <para>
/// <strong>Activision Blizzard, September 2023.</strong> Acquired by Microsoft and delisted in
/// October 2023, so the month asked for is one it certainly traded and the ticker is one that
/// certainly does not trade now. It sits inside the five-year window, which is the window the
/// universe will be acquired over.
/// </para>
/// <para>
/// <strong>Exactly one request, enforced by the client.</strong> <see cref="OneCallOnly"/> throws
/// on a second send, so a redirect, an internal retry or a later edit fails the test rather than
/// spending a call nobody agreed to. Nothing is ingested, archived, ledgered or stored: the
/// response is counted, its dates read, and the payload discarded.
/// </para>
/// <para>
/// Gated on <c>AIINV_DELISTED=1</c>.
/// </para>
/// </remarks>
public sealed class DelistedHistoryProbeTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_DELISTED";

    /// <summary>Delisted October 2023 on the Microsoft acquisition.</summary>
    private const string Symbol = "ATVI.US";

    private static readonly DateOnly WindowStart = new(2023, 9, 1);

    private static readonly DateOnly WindowEnd = new(2023, 9, 29);

    /// <summary>
    /// The fewest sessions a full month of US trading can plausibly have.
    /// </summary>
    /// <remarks>
    /// September 2023 held twenty sessions. A handful of rows would mean something other than
    /// coverage - a truncated answer, or a series clipped at a boundary - and is worth telling
    /// apart from both a full month and an empty one.
    /// </remarks>
    private const int PlausibleSessionsInAMonth = 15;

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public DelistedHistoryProbeTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task A_ticker_that_no_longer_trades_still_has_history()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Delisted-history probe is off. Set {GateVariable}=1 to run it. It makes exactly one " +
            "real, billable call, stores nothing, and starts no acquisition.");

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var clock = services.GetRequiredService<IClock>();
        var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;
        var presence = CredentialPresence.Of(options.ApiKey);

        var report = new StringBuilder();

        report.AppendLine("# Delisted history — one call, one dead ticker");
        report.AppendLine();
        report.AppendLine(Inv($"Run {clock.UtcNow:yyyy-MM-dd HH:mm:ss}Z. **One call**, for one instrument, one month."));
        report.AppendLine("Nothing was ingested, archived, ledgered or stored. No prices were recorded.");
        report.AppendLine("This is not the first request of the universe acquisition.");
        report.AppendLine();
        report.AppendLine(Inv($"Credential fingerprint `{presence.Fingerprint}` - the token in force."));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Inv($"| Instrument | `{Symbol}` |"));
        report.AppendLine("| Status | delisted October 2023, Microsoft acquisition |");
        report.AppendLine(Inv($"| Requested window | {WindowStart:yyyy-MM-dd} to {WindowEnd:yyyy-MM-dd} |"));
        report.AppendLine("| Why it matters | the manifest will contain companies that stopped trading |");
        report.AppendLine();

        Assert.True(
            presence.IsConfigured,
            "No credential is configured. Run scripts/check-eodhd-credential.cmd first.");

        using var budget = new OneCallOnly { InnerHandler = new HttpClientHandler() };
        using var client = new HttpClient(budget)
        {
            BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var sessions = await AskAsync(client, options, report);

        report.AppendLine();
        report.AppendLine(Inv($"Requests sent: **{budget.Sent}**."));

        await WriteAsync(report);
        _output.WriteLine(report.ToString());

        Assert.Equal(1, budget.Sent);

        Assert.True(
            sessions.Count > 0,
            Inv($"EODHD returned no sessions for the delisted {Symbol} in {WindowStart:yyyy-MM}. ") +
            "The point-in-time universe would then contain companies whose prices cannot be " +
            "fetched, and dropping them is exactly the survivorship bias the design exists to " +
            "avoid. The expansion needs a different price source for dead names, or a stated and " +
            "measured limitation - not a quiet exclusion.");

        Assert.True(
            sessions.Count >= PlausibleSessionsInAMonth,
            Inv($"Only {sessions.Count} sessions came back for a full trading month. That is ") +
            "neither coverage nor its absence, and the acquisition should not proceed on an answer " +
            "nobody has explained.");
    }

    private static async Task<List<DateOnly>> AskAsync(
        HttpClient client,
        EodhdOptions options,
        StringBuilder report)
    {
        var sessions = new List<DateOnly>();

        report.AppendLine("## What came back");
        report.AppendLine();

        try
        {
            // The credential is in the query because that is how EODHD authenticates. Built here,
            // used once, and never put into a message, a log or this report.
            var uri = new Uri(
                "api/eod/" + Uri.EscapeDataString(Symbol) +
                "?fmt=json&period=d" +
                "&from=" + WindowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "&to=" + WindowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "&api_token=" + Uri.EscapeDataString(options.ApiKey),
                UriKind.Relative);

            using var response = await client.GetAsync(uri);

            if (!response.IsSuccessStatusCode)
            {
                report.AppendLine(Inv($"- the endpoint answered **{(int)response.StatusCode}**."));
                report.AppendLine(Explain(response.StatusCode));

                return sessions;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (document.ValueKind != JsonValueKind.Array)
            {
                report.AppendLine(Inv($"- the endpoint answered 200 with a `{document.ValueKind}`,"));
                report.AppendLine("  not the array of daily rows. The body is deliberately not");
                report.AppendLine("  recorded: it can echo the request, and the request carries the token.");

                return sessions;
            }

            foreach (var row in document.EnumerateArray())
            {
                if (row.TryGetProperty("date", out var date) &&
                    DateOnly.TryParse(
                        date.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var session))
                {
                    sessions.Add(session);
                }
            }

            sessions.Sort();

            report.AppendLine(Inv($"- **sessions returned: {sessions.Count}**"));
            report.AppendLine(sessions.Count == 0
                ? "- covered range: none"
                : Inv($"- **covered range: {sessions[0]:yyyy-MM-dd} to {sessions[^1]:yyyy-MM-dd}**"));
            report.AppendLine();
            report.AppendLine(Verdict(sessions));

            return sessions;
        }
#pragma warning disable CA1031 // An unreachable network and an unentitled account are different
                              // findings, and reporting the first as the second would send an
                              // operator to their subscription over a dropped connection.
        catch (Exception ex)
        {
            // The type, never the message: a transport message can name the request URI, and the
            // request URI carries the credential.
            report.AppendLine(Inv($"- could not reach the endpoint: `{ex.GetType().Name}`."));

            return sessions;
        }
#pragma warning restore CA1031
    }

    private static string Verdict(List<DateOnly> sessions)
    {
        if (sessions.Count == 0)
        {
            return "**Delisted history is not served.** The survivorship design cannot be " +
                "implemented on this provider alone: a universe built from filings would name " +
                "companies whose prices cannot be fetched. Dropping them silently is the bias the " +
                "design exists to remove, so the expansion needs either another source for dead " +
                "names or a stated, measured limitation.";
        }

        if (sessions.Count < PlausibleSessionsInAMonth)
        {
            return Inv($"**Only {sessions.Count} sessions for a full trading month.** Neither ") +
                "coverage nor its absence. Worth understanding before four hundred names depend on it.";
        }

        return Inv($"**Delisted history is served**: {sessions.Count} sessions for a ticker that ") +
            "stopped trading in October 2023. The point-in-time universe can therefore include the " +
            "companies that failed or were acquired, which is the single assumption the survivorship " +
            "argument rests on. Gate 2 - at least five per cent of members having stopped filing - " +
            "can be met with real price series rather than empty ones.";
    }

    private static string Explain(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound =>
            "- no series under that symbol. For a delisted name this is the interesting answer: it " +
            "may mean the plan excludes dead tickers, or that they are served under a different " +
            "symbol form. Either way the universe design needs to know before it depends on it.",

        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "- refused as unauthenticated or not permitted. The account endpoint accepted this " +
            "token, so a refusal here is about entitlement rather than the credential.",

        HttpStatusCode.TooManyRequests =>
            "- rate limited. Nothing was retried: this run is authorised for one request.",

        _ => "- the response body is deliberately not recorded: this endpoint can echo the request, " +
             "and the request carries the token.",
    };

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(StringBuilder report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "delisted.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
