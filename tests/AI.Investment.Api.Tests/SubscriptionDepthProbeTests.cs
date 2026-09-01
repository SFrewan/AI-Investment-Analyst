using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Normalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Asks the vendor, rather than the marketing page, how much history this account can have.
/// </summary>
/// <remarks>
/// <para>
/// The Block 2B backfill asked for two years and got one - every instrument came back with 250
/// sessions starting the same week, and nothing older than 370 days reached the database. The
/// request was built correctly and the gateway narrows nothing, so the truncation happened at the
/// vendor. This settles whether that is a limit of the subscription or something else, because
/// "we decided on two years" and "we have one" must not quietly become the same sentence.
/// </para>
/// <para>
/// <strong>Two calls, and it says so.</strong> One to the account endpoint, which reports the plan
/// and the daily limit. One ordinary price request for a month that sits entirely inside the second
/// year - the decisive test, because a plan that carries that month will return it and a plan that
/// does not will return nothing for it.
/// </para>
/// <para>
/// <strong>Nothing identifying is printed.</strong> The account endpoint returns the subscriber's
/// name and email beside the plan. Those are read past and never written to the report, and the API
/// token is never printed under any circumstances.
/// </para>
/// <para>
/// Gated on <c>AIINV_PROBE=1</c>. It makes real, billable calls.
/// </para>
/// </remarks>
public sealed class SubscriptionDepthProbeTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_PROBE";

    private const string Symbol = "AAPL.US";

    /// <summary>A month that lies wholly inside the second year of the requested window.</summary>
    private static readonly DateTime ProbeStart = new(2024, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime ProbeEnd = new(2024, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SubscriptionDepthProbeTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_account_is_asked_what_history_it_is_entitled_to()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Subscription probe is off. Set {GateVariable}=1 to run it. It makes two real, billable calls.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var clock = services.GetRequiredService<IClock>();
        var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;

        Line(report, "# Block 2B - subscription depth probe");
        Line(report, string.Empty);
        Line(report, Inv($"Run at {clock.UtcNow:yyyy-MM-dd HH:mm:ss}Z. Two billable calls."));
        Line(report, string.Empty);

        await DescribeAccountAsync(options, report);
        await ProbeSecondYearAsync(services, clock, report);

        await WriteAsync(report);
        _output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Reads the plan and the daily limit, and nothing that identifies a person.
    /// </summary>
    private static async Task DescribeAccountAsync(EodhdOptions options, StringBuilder report)
    {
        Line(report, "## The account");
        Line(report, string.Empty);

        using var client = new HttpClient { BaseAddress = new Uri(options.BaseAddress) };

        try
        {
            using var response = await client.GetAsync(
                new Uri(
                    "api/user?api_token=" + Uri.EscapeDataString(options.ApiKey) + "&fmt=json",
                    UriKind.Relative));

            if (!response.IsSuccessStatusCode)
            {
                // The status only. A body from this endpoint can echo the query string.
                Line(report, Inv($"- the account endpoint answered {(int)response.StatusCode}."));

                return;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonElement>();

            foreach (var field in new[] { "subscriptionType", "dailyRateLimit", "apiRequests", "extraLimit" })
            {
                Line(report, document.TryGetProperty(field, out var value)
                    ? Inv($"- {field}: {value}")
                    : Inv($"- {field}: not reported"));
            }

            // Deliberately not printed: name, email, paymentMethod, inviteToken.
            Line(report, string.Empty);
            Line(report, "The subscriber's name, email and payment method are returned by this");
            Line(report, "endpoint and are deliberately not recorded here.");
        }
#pragma warning disable CA1031 // A probe that cannot reach the vendor should report that, not fail
                              // the run: the question it answers is about entitlement, and an
                              // unreachable network is a different fact worth stating plainly.
        catch (Exception ex)
        {
            Line(report, Inv($"- could not reach the account endpoint: {ex.GetType().Name}"));
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Asks for one month inside the second year, through the ordinary ingestion path.
    /// </summary>
    /// <remarks>
    /// Through the gateway rather than a bare HTTP call, so the answer is archived, ledgered and
    /// normalised like any other evidence. If those sessions do exist for this account they are
    /// then genuinely held, rather than seen once by a diagnostic and thrown away.
    /// </remarks>
    private static async Task ProbeSecondYearAsync(
        IServiceProvider services,
        IClock clock,
        StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## One month inside the second year");
        Line(report, string.Empty);
        Line(report, Inv($"Requested {Symbol} from {ProbeStart:yyyy-MM-dd} to {ProbeEnd:yyyy-MM-dd}."));
        Line(report, string.Empty);

        var settings = services.GetRequiredService<DiscoverySettings>();
        var subject = IngestionSubject.Create("Security", Symbol);

        var request = IngestionRequest.Create(
            SourceId.Create(settings.PriceSourceId),
            DataCategory.MarketPrices,
            Region.Global,
            subject,
            CorrelationId.Create("probe-depth-AAPL-US-20240902-20240930"),
            clock.UtcNow,
            DateRange.Create(ProbeStart, ProbeEnd));

        var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

        Line(report, Inv($"- run outcome: {result.Run.Outcome}"));
        Line(report, Inv($"- observations recorded: {result.ObservationsRecorded}"));

        if (!result.WasFetched)
        {
            Line(report, Inv($"- not fetched: {result.Run.RefusalRuleId ?? result.Run.Reason ?? "no reason recorded"}"));

            return;
        }

        var stored = await services
            .GetRequiredService<IObservationStore>()
            .ForSubjectAsync(subject, clock.UtcNow);

        var inWindow = stored
            .Where(o => string.Equals(o.Attribute, EodhdDailyPriceNormalizer.CloseAttribute, StringComparison.Ordinal))
            .Where(o => o.Provenance.AsOfUtc >= ProbeStart && o.Provenance.AsOfUtc <= ProbeEnd.AddDays(1))
            .ToList();

        Line(report, Inv($"- sessions now held inside that month: {inWindow.Count}"));

        Line(report, string.Empty);

        Line(report, inWindow.Count > 0
            ? "**The second year is available.** The one-year coverage is therefore not an "
              + "entitlement limit, and the backfill window should be re-run to collect it."
            : "**The second year is not available to this account.** The two-year decision cannot "
              + "be met on this subscription. Actual coverage stands at 250 sessions, about one "
              + "year, and should be reported as that rather than restated as the decision.");
    }

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(StringBuilder report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "subscription-probe.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
