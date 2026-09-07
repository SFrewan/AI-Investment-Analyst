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
/// One call to EODHD's account endpoint, to learn which subscription the configured token is on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Exactly one request, enforced rather than intended.</strong> The client is built on a
/// handler that throws on a second send, so a redirect, a retry inside <c>HttpClient</c>, or a
/// later edit that adds "just one more check" fails the test instead of quietly spending a second
/// call. A budget that is a comment is not a budget.
/// </para>
/// <para>
/// <strong>Nothing is written.</strong> This does not go through the ingestion gateway: the
/// account endpoint is not an ingestion category, and routing it through the gateway would archive
/// a payload, open a run and record a ledger entry for something that is not evidence about a
/// market. No database row, no archive file, no source activation, no watch. The earlier
/// subscription probe made a second, gateway-borne price call for the same reason it was useful -
/// it stored what it fetched - and that is precisely what is not wanted here.
/// </para>
/// <para>
/// <strong>Four fields are read and the rest are not.</strong> The account endpoint returns the
/// subscriber's name, email address, payment method and invite token beside the plan. Those are
/// read past and never recorded. The API token is never printed under any circumstances, and no
/// exception message is printed either - only its type - because a transport message can name the
/// request, and the request carries the credential in its query string.
/// </para>
/// <para>
/// Gated on <c>AIINV_SUBSCRIPTION=1</c>. It makes one real, billable call.
/// </para>
/// </remarks>
public sealed class SubscriptionVerificationTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_SUBSCRIPTION";

    /// <summary>The account endpoint. Reports the plan; changes nothing.</summary>
    private const string AccountPath = "api/user";

    /// <summary>
    /// The fields worth recording. Everything else this endpoint returns is about a person.
    /// </summary>
    private static readonly string[] Reported =
        ["subscriptionType", "dailyRateLimit", "apiRequests", "apiRequestsDate", "extraLimit"];

    /// <summary>The free tier's daily ceiling, from EODHD's published limits.</summary>
    private const int FreeTierDailyLimit = 20;

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SubscriptionVerificationTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_account_endpoint_is_asked_which_subscription_this_token_is_on()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Subscription verification is off. Set {GateVariable}=1 to run it. It makes exactly " +
            "one real, billable call and writes nothing.");

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var clock = services.GetRequiredService<IClock>();
        var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;
        var presence = CredentialPresence.Of(options.ApiKey);

        var report = new StringBuilder();

        report.AppendLine("# EODHD subscription - verification");
        report.AppendLine();
        report.AppendLine(Inv($"Run {clock.UtcNow:yyyy-MM-dd HH:mm:ss}Z. **One call**, to `/{AccountPath}`."));
        report.AppendLine("Nothing was ingested, archived, ledgered or activated. No price data was requested.");
        report.AppendLine();
        report.AppendLine(Inv($"Credential fingerprint `{presence.Fingerprint}` - the same one ")
            + "artifacts/verify/credential.md reports, so this answer is about the token in force.");
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

        var outcome = await AskAsync(client, options, report);

        report.AppendLine();
        report.AppendLine(Inv($"Requests sent: **{budget.Sent}**."));
        report.AppendLine();
        report.AppendLine("The subscriber's name, email address, payment method and invite token are");
        report.AppendLine("returned by this endpoint and are deliberately not recorded here. Neither is");
        report.AppendLine("the API token, in this file or in any exception text.");

        await WriteAsync(report);
        _output.WriteLine(report.ToString());

        Assert.Equal(1, budget.Sent);

        Assert.True(
            outcome is HttpStatusCode.OK,
            "EODHD did not accept the configured token: it answered " +
            ((int?)outcome)?.ToString(CultureInfo.InvariantCulture) + ". A 401 or 403 means this " +
            "token is wrong, expired, or not on a plan that covers the account endpoint. The " +
            "token itself is not shown.");
    }

    /// <summary>
    /// Sends the one request and records the plan. Returns the status, or null if it never landed.
    /// </summary>
    private static async Task<HttpStatusCode?> AskAsync(
        HttpClient client,
        EodhdOptions options,
        StringBuilder report)
    {
        report.AppendLine("## What the account reports");
        report.AppendLine();

        try
        {
            // The credential is in the query because that is how EODHD authenticates. The URI is
            // built here, used once, and never put into a message, a log or this report.
            var uri = new Uri(
                AccountPath + "?fmt=json&api_token=" + Uri.EscapeDataString(options.ApiKey),
                UriKind.Relative);

            using var response = await client.GetAsync(uri);

            if (!response.IsSuccessStatusCode)
            {
                // The status only. A body from this endpoint can echo the query string back.
                report.AppendLine(Inv($"- the endpoint answered **{(int)response.StatusCode}**."));
                report.AppendLine("- the response body is deliberately not recorded: this endpoint");
                report.AppendLine("  can echo the request, and the request carries the token.");

                return response.StatusCode;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonElement>();

            report.AppendLine("| Field | Value |");
            report.AppendLine("| --- | --- |");

            foreach (var field in Reported)
            {
                report.AppendLine(document.TryGetProperty(field, out var value)
                    ? Inv($"| `{field}` | {value} |")
                    : Inv($"| `{field}` | not reported |"));
            }

            report.AppendLine();
            report.AppendLine(Interpret(document));

            return response.StatusCode;
        }
#pragma warning disable CA1031 // A verification that cannot reach the vendor should say so rather
                              // than fail as though the token were wrong: an unreachable network
                              // and a rejected credential are different findings.
        catch (Exception ex)
        {
            // The type, never the message. A transport message can name the request URI, and the
            // request URI carries the credential.
            report.AppendLine(Inv($"- could not reach the endpoint: `{ex.GetType().Name}`."));

            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// What the numbers mean, stated only as far as they actually settle it.
    /// </summary>
    /// <remarks>
    /// The daily ceiling is the discriminator worth trusting: the free tier's is twenty and a paid
    /// plan's is orders of magnitude larger, so the two cannot be confused. The plan <em>name</em>
    /// is reported verbatim rather than matched against a list, because a list of EODHD's product
    /// names in this repository would be a guess about somebody else's catalogue that goes stale
    /// silently.
    /// </remarks>
    private static string Interpret(JsonElement document)
    {
        if (!document.TryGetProperty("dailyRateLimit", out var limit) ||
            !TryReadInt(limit, out var daily))
        {
            return "**The daily limit was not reported**, so the plan cannot be settled from this " +
                "call. The `subscriptionType` above is the vendor's own name for it.";
        }

        if (daily <= FreeTierDailyLimit)
        {
            return Inv($"**This token is on the free tier.** A daily ceiling of {daily} is the free ")
                + "allowance, not a paid one. The paid history is not available to it, and the "
                + "acquisition plan's forty requests would not fit in a day. If you have bought a "
                + "subscription, this is not that subscription's token.";
        }

        return Inv($"**This token is on a paid plan**: a daily ceiling of {daily} is far above the ")
            + Inv($"free tier's {FreeTierDailyLimit}. The plan's own name is `subscriptionType` ")
            + "above - check it names the all-world end-of-day product, because the ceiling "
            + "establishes that the subscription is paid, not which data it covers.\n\n"
            + "**Depth is not stated by this endpoint and has not been measured.** It follows from "
            + "the plan rather than from anything returned here. Measuring it takes one more call - "
            + "a single instrument, a single month inside the target window - which is a separate "
            + "decision.";
    }

    private static bool TryReadInt(JsonElement value, out int number)
    {
        // EODHD has returned this as both a number and a string across responses, so both are read
        // rather than one being assumed and the other reported as "not reported".
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number))
        {
            return true;
        }

        return int.TryParse(
            value.ValueKind == JsonValueKind.String ? value.GetString() : null,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(StringBuilder report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "subscription.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
