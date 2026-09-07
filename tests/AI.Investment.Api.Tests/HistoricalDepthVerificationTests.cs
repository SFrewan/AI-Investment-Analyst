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
/// One call for one month at the far edge of the intended window, to learn whether the
/// subscription actually carries that history.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The question the account endpoint could not answer.</strong> That call established the
/// subscription is paid - a daily ceiling of 100,000 against the free tier's 20 - and said nothing
/// about depth, because depth is not a field it returns. A plan is evidence for depth; it is not a
/// measurement of it. This is the measurement, and it is decisive in one request: a plan that
/// carries September 2021 returns September 2021, and a plan that does not returns an empty array
/// for it.
/// </para>
/// <para>
/// <strong>The oldest month in the window, deliberately.</strong> The intended range opens on
/// 2021-09-01, so this asks for the first month of it. Any later month would leave the far edge -
/// the part most likely to be missing - untested, and would have to be followed by a second call
/// to settle what one call can settle here.
/// </para>
/// <para>
/// <strong>Nothing is written.</strong> This does not go through the ingestion gateway: no run, no
/// ledger entry, no archived payload, no observation, no source activation. The rows that come
/// back are counted and their dates read; the payload is then discarded. That is the difference
/// between verifying depth and beginning the backfill, and it is the whole reason this is not
/// simply the first request of the forty.
/// </para>
/// <para>
/// <strong>Prices are not recorded.</strong> The report states how many sessions came back and
/// which dates they span. It does not state what anything traded at, because that is data this run
/// was not authorised to keep.
/// </para>
/// <para>
/// Gated on <c>AIINV_DEPTH=1</c>. It makes exactly one real, billable call.
/// </para>
/// </remarks>
public sealed class HistoricalDepthVerificationTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_DEPTH";

    /// <summary>
    /// The control instrument, as everywhere else in this project.
    /// </summary>
    /// <remarks>
    /// AAPL is the one the platform already holds recent history for, which makes it the name
    /// whose absence at depth would be about the subscription rather than about the instrument.
    /// </remarks>
    private const string Symbol = "AAPL.US";

    /// <summary>The first month of the intended five-year window.</summary>
    private static readonly DateOnly WindowStart = new(2021, 9, 1);

    private static readonly DateOnly WindowEnd = new(2021, 9, 30);

    /// <summary>
    /// The fewest sessions a full month of US trading can plausibly have.
    /// </summary>
    /// <remarks>
    /// September 2021 held twenty-one sessions. A response with a handful of rows would mean
    /// something other than "the month is covered" - a partial answer, or a window clipped at an
    /// entitlement boundary - and is worth distinguishing from both a full month and an empty one.
    /// </remarks>
    private const int PlausibleSessionsInAMonth = 15;

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public HistoricalDepthVerificationTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task One_month_at_the_far_edge_of_the_window_is_asked_for()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Depth verification is off. Set {GateVariable}=1 to run it. It makes exactly one " +
            "real, billable call, stores nothing, and does not begin the backfill.");

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var clock = services.GetRequiredService<IClock>();
        var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;
        var presence = CredentialPresence.Of(options.ApiKey);

        var report = new StringBuilder();

        report.AppendLine("# EODHD historical depth - verification");
        report.AppendLine();
        report.AppendLine(Inv($"Run {clock.UtcNow:yyyy-MM-dd HH:mm:ss}Z. **One call**, for one instrument, one month."));
        report.AppendLine("Nothing was ingested, archived, ledgered or activated. No prices were recorded.");
        report.AppendLine("This is not the first request of the backfill; the payload was read and discarded.");
        report.AppendLine();
        report.AppendLine(Inv($"Credential fingerprint `{presence.Fingerprint}` - the token in force."));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        report.AppendLine(Inv($"| Instrument | `{Symbol}` |"));
        report.AppendLine(Inv($"| Requested window | {WindowStart:yyyy-MM-dd} to {WindowEnd:yyyy-MM-dd} |"));
        report.AppendLine("| Position in the intended range | the first month of 2021-09 to 2026-08 |");
        report.AppendLine(Inv($"| Currently held from | 2025-09-02 (about one year) |"));
        report.AppendLine();

        Assert.True(
            presence.IsConfigured,
            "No credential is configured, so there is nothing to verify. Run " +
            "scripts/check-eodhd-credential.cmd first.");

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
            Inv($"EODHD returned no sessions for {Symbol} in {WindowStart:yyyy-MM} at all. ") +
            "The subscription does not carry the far edge of the intended window, so the " +
            "five-year plan cannot be met on it and the window must be shortened to what the " +
            "account actually holds rather than restated as the decision.");

        Assert.True(
            sessions.Count >= PlausibleSessionsInAMonth,
            Inv($"EODHD returned only {sessions.Count} sessions for a full trading month. ") +
            "That is neither coverage nor its absence: it suggests a window clipped at an " +
            "entitlement boundary, and the acquisition should not proceed on an answer nobody " +
            "has explained.");
    }

    /// <summary>
    /// Sends the one request and records how many sessions came back and what they span.
    /// </summary>
    /// <remarks>
    /// The URI mirrors what <c>EodhdProvider</c> builds - the same endpoint, the same
    /// <c>fmt</c> and <c>period</c> stated rather than left to the vendor's defaults - rather than
    /// calling the connector itself. The connector takes its client from the factory, and this run
    /// has to send through a client whose handler can refuse a second request. The budget was the
    /// condition of making the call at all, so it wins the trade.
    /// </remarks>
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
                // The status only. A body from this endpoint can echo the query string back.
                report.AppendLine(Inv($"- the endpoint answered **{(int)response.StatusCode}**."));
                report.AppendLine(Explain(response.StatusCode));

                return sessions;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonElement>();

            if (document.ValueKind != JsonValueKind.Array)
            {
                report.AppendLine(Inv($"- the endpoint answered 200 with a `{document.ValueKind}`,"));
                report.AppendLine("  not the array of daily rows this endpoint returns. The body is");
                report.AppendLine("  deliberately not recorded: it can echo the request, and the");
                report.AppendLine("  request carries the token.");

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
            // The type, never the message. A transport message can name the request URI, and the
            // request URI carries the credential.
            report.AppendLine(Inv($"- could not reach the endpoint: `{ex.GetType().Name}`."));

            return sessions;
        }
#pragma warning restore CA1031
    }

    /// <summary>What the sessions mean for the acquisition, stated as far as one month settles it.</summary>
    private static string Verdict(List<DateOnly> sessions)
    {
        if (sessions.Count == 0)
        {
            return "**The subscription does not carry this month.** The five-year window cannot be " +
                "acquired on this account, and the plan should be shortened to the depth the " +
                "account actually holds rather than left stating a range it cannot fill.";
        }

        if (sessions.Count < PlausibleSessionsInAMonth)
        {
            return Inv($"**Only {sessions.Count} sessions came back for a full trading month.** ") +
                "That is neither coverage nor its absence. A window clipped at an entitlement " +
                "boundary looks like this, and the acquisition should not proceed until somebody " +
                "has explained it.";
        }

        return Inv($"**The far edge of the intended window is available**: {sessions.Count} ") +
            "sessions for a month that held twenty-one, five years before today and four years " +
            "older than anything currently stored. Depth is no longer an inference from the plan " +
            "name. The forty-request acquisition can proceed on the window as planned - it has " +
            "not been started, and nothing here stored a price.";
    }

    /// <summary>Whose problem a refusal is, without naming anything from the request.</summary>
    private static string Explain(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "- the token was refused, or its plan does not cover this data. The account endpoint " +
            "accepted it, so a refusal here is about entitlement rather than the credential.",

        HttpStatusCode.NotFound =>
            "- no series under that symbol on this plan. Either the ticker is wrong or the market " +
            "is not included.",

        HttpStatusCode.TooManyRequests =>
            "- rate limited. Nothing was retried: this run is authorised for one request.",

        _ => "- the response body is deliberately not recorded: this endpoint can echo the " +
             "request, and the request carries the token.",
    };

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(StringBuilder report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "depth.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
