using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Operators;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// A host with the EDGAR connector switched on and everything else left alone.
/// </summary>
/// <remarks>
/// <para>
/// The contact address is read from the environment rather than written here. The SEC's fair-access
/// policy requires every request to identify a person who can be reached about it, which makes the
/// address a real identity rather than a configuration value - and one that has no business being
/// committed to a repository. <c>scripts/verify.local.ps1</c> is git-ignored and already holds this
/// machine's other local settings.
/// </para>
/// <para>
/// The request rate is set below the published ceiling of ten a second. Twenty companies is twenty
/// requests; there is nothing to gain from approaching a limit imposed for everyone's benefit.
/// </para>
/// </remarks>
public sealed class SecBackfillApiFactory : WebApplicationFactory<Program>
{
    public const string OperatorId = "sec-backfill@operator.local";

    public const string ApplicationName = "AI-Investment-Analyst";

    public const string ContactVariable = "AIINV_SEC_CONTACT";

    public const int RequestsPerSecond = 5;

    public static string? Contact =>
        Environment.GetEnvironmentVariable(ContactVariable);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperationsHost:RunCycles"] = "false",
                ["OperationsHost:RunOutboxDispatcher"] = "false",
                ["DataPlane:RunRetentionSweep"] = "false",
                ["DataPlane:SeedSourcesOnStartup"] = "false",

                ["Providers:SecEdgar:Enabled"] = "true",
                ["Providers:SecEdgar:ApplicationName"] = ApplicationName,
                ["Providers:SecEdgar:ContactEmail"] = Contact ?? string.Empty,
                ["Providers:SecEdgar:MaxRequestsPerSecond"] =
                    RequestsPerSecond.ToString(CultureInfo.InvariantCulture),
            });
        });

        builder.ConfigureTestServices(services =>
            services.AddScoped<IOperatorContext, SecBackfillOperator>());
    }

    private sealed class SecBackfillOperator : IOperatorContext
    {
        public OperatorIdentity? Current { get; } = OperatorIdentity.Create(
            OperatorId,
            "SEC EDGAR historical backfill",
            [OperatorPrivilege.AdministerWatches]);
    }
}

/// <summary>
/// The twenty-company fundamentals backfill from EDGAR's XBRL companyfacts endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This makes real requests to a U.S. government service, and only when explicitly asked.</strong>
/// EDGAR is public domain and free; there is nothing billable here. What there is, is a fair-access
/// policy that this run honours: an identifying User-Agent, a request rate below the published
/// ceiling, and one request per company rather than a crawl.
/// </para>
/// <para>
/// <strong>Nothing about opportunities or execution is touched.</strong> The run writes observations
/// and ingestion runs. It creates no opportunity, no prediction and no order, starts no cycle, and
/// the assertions at the end prove the opportunity count is unchanged.
/// </para>
/// <para>
/// <strong>One call returns everything.</strong> Unlike the price connector, companyfacts takes no
/// window - it returns the company's whole XBRL history in a single document, which is why this
/// backfill has no date range and why it is cheap to repeat. Idempotency is the ingestion ledger's,
/// exactly as it is for prices.
/// </para>
/// <para>
/// Gated on <c>AIINV_SEC_BACKFILL=1</c>.
/// </para>
/// </remarks>
public sealed class SecBackfillTests : IClassFixture<SecBackfillApiFactory>
{
    private const string GateVariable = "AIINV_SEC_BACKFILL";

    /// <summary>The subject kind EDGAR's connector understands.</summary>
    private const string CompanySubjectKind = "Company";

    /// <summary>
    /// The same twenty instruments, resolved to the identifiers EDGAR uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EDGAR identifies companies by Central Index Key, not by ticker, so the universe has to be
    /// translated before it can be asked for. These numbers were read from the SEC's own published
    /// mapping at <c>https://www.sec.gov/files/company_tickers.json</c> on 2026-09-01 rather than
    /// recalled or inferred - a wrong CIK does not fail, it silently returns another company's
    /// accounts.
    /// </para>
    /// <para>
    /// The ticker is carried alongside because nothing else in the platform links the two. Prices
    /// are stored under <c>Security:AAPL.US</c> and these facts under <c>Company:0000320193</c>;
    /// joining them is a small piece of work that does not exist yet, and this table is the record
    /// of what it would join.
    /// </para>
    /// </remarks>
    private static readonly (string Ticker, string Cik)[] Universe =
    [
        ("AAPL.US", "0000320193"), ("MSFT.US", "0000789019"), ("GOOGL.US", "0001652044"),
        ("AMZN.US", "0001018724"), ("NVDA.US", "0001045810"), ("META.US", "0001326801"),
        ("TSLA.US", "0001318605"), ("JPM.US", "0000019617"), ("V.US", "0001403161"),
        ("JNJ.US", "0000200406"), ("WMT.US", "0000104169"), ("PG.US", "0000080424"),
        ("XOM.US", "0002115436"), ("UNH.US", "0000731766"), ("HD.US", "0000060695"),
        ("MA.US", "0001141391"), ("KO.US", "0000021344"), ("PEP.US", "0000077476"),
        ("CVX.US", "0000093410"), ("MRK.US", "0000310158"),
    ];

    /// <summary>How many companies must yield facts before the run counts as a success.</summary>
    /// <remarks>
    /// Not twenty. A company that re-registered under a new CIK carries its history under the old
    /// one, and one or two silent gaps are a normal property of EDGAR rather than a defect in this
    /// run - but a wholesale failure must not pass quietly, so the floor is high enough to catch it.
    /// </remarks>
    private const int MinimumCompaniesWithFacts = 15;

    /// <summary>Names this run, so a previous failed attempt cannot bar the next one forever.</summary>
    /// <remarks>
    /// <para>
    /// There are two idempotency controls on this path and they answer different questions. The
    /// ingestion ledger's <c>HasCompletedAsync</c> asks "has this exact request already SUCCEEDED?"
    /// and is what stops a rerun fetching the same document twice or writing the same observation
    /// twice - it is checked before anything is requested, and it is untouched here. The action
    /// gateway's key asks "has this exact action already been dispatched?" and is what stops a retry
    /// after a timeout replaying a call that may already be in flight.
    /// </para>
    /// <para>
    /// A stable correlation makes the second key permanent, which is correct for a scheduled fetch
    /// and wrong for a backfill: this run's first attempt executed, threw on a malformed header, and
    /// stored nothing - and every attempt afterwards came back <c>DuplicateSuppressed</c>, barred by
    /// an action that had produced no data. The key cannot be released; <c>GuardWrites</c> refuses
    /// to delete a processed action, deliberately.
    /// </para>
    /// <para>
    /// So a run is one attempt and says which attempt it is. Nothing about data idempotency is
    /// weakened by that: a company whose facts already landed is skipped by the ledger before the
    /// correlation is ever used, so no request is repeated and no observation is duplicated.
    /// </para>
    /// </remarks>
    private static readonly string Attempt = Environment.GetEnvironmentVariable("AIINV_SEC_ATTEMPT")
        is { Length: > 0 } supplied
            ? new string(supplied.Where(char.IsAsciiLetterOrDigit).Take(24).ToArray())
            : DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private readonly SecBackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SecBackfillTests(SecBackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_universe_is_backfilled_with_edgar_fundamentals()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"The SEC backfill is off. Set {GateVariable}=1 to run it. It makes real requests to a " +
            "U.S. government service - free, but rate-limited and subject to a fair-access policy.");

        Skip.If(
            string.IsNullOrWhiteSpace(SecBackfillApiFactory.Contact),
            $"{SecBackfillApiFactory.ContactVariable} is not set. EDGAR's fair-access policy requires " +
            "every request to carry a contact address, and this run will not invent one.");

        var root = _factory.Services;
        var report = new StringBuilder();
        var ledger = new CallLedger();

        var now = root.GetRequiredService<IClock>().UtcNow;

        int opportunitiesBefore;

        using (var before = root.CreateScope())
        {
            opportunitiesBefore = await before.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Opportunities.AsNoTracking().CountAsync();
        }

        Line(report, "# SEC EDGAR - fundamentals backfill");
        Line(report, string.Empty);
        Line(report, Inv($"Run at {now:yyyy-MM-dd HH:mm:ss}Z. Twenty companies, one request each."));
        Line(report, "Public-domain source; nothing billable. No opportunity, prediction or order.");
        Line(report, string.Empty);

        await RegisterSourcesAsync(root, report);
        await ActivateSourceAsync(root, report);
        await IngestAsync(root, report, ledger);

        Line(report, string.Empty);
        Line(report, "## Requests");
        Line(report, string.Empty);
        Line(report, Inv($"- fetched: {ledger.Fetched}"));
        Line(report, Inv($"- already complete and skipped: {ledger.Skipped}"));
        Line(report, Inv($"- refused or failed: {ledger.NotFetched}"));

        var landed = await VerifyAsync(root, report);

        using (var after = root.CreateScope())
        {
            var opportunitiesAfter = await after.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Opportunities.AsNoTracking().CountAsync();

            Line(report, string.Empty);
            Line(report, Inv($"Opportunities before {opportunitiesBefore}, after {opportunitiesAfter}."));

            Assert.Equal(opportunitiesBefore, opportunitiesAfter);
        }

        await WriteAsync(report.ToString());
        _output.WriteLine(report.ToString());

        Assert.True(
            landed.CompaniesWithFacts >= MinimumCompaniesWithFacts,
            Inv($"Only {landed.CompaniesWithFacts} of {Universe.Length} companies produced facts; ") +
            Inv($"at least {MinimumCompaniesWithFacts} were expected. ") +
            "A wholesale failure must not pass quietly.");

        // Point-in-time, asserted against what was actually stored rather than against a fixture.
        Assert.Equal(0, landed.PublishedBeforePeriod);
        Assert.Equal(0, landed.PublishedAfterRetrieval);
    }

    // ---- 1. registration and activation, through the seam -------------------

    private static async Task RegisterSourcesAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, "## Source registration");
        Line(report, string.Empty);

        using var scope = root.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<RegisterKnownSourcesHandler>();

        foreach (var result in await handler.HandleAsync())
        {
            Line(report, Inv($"- `{result.SourceId}` -> {result.Outcome}"));
        }
    }

    private static async Task ActivateSourceAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## Source activation");
        Line(report, string.Empty);

        using var scope = root.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<ActivateSourceHandler>();
        var result = await handler.HandleAsync(SecEdgarProvider.Id);

        Line(report, Inv($"- `{SecEdgarProvider.Id}` -> {result.Status}: {result.Reason}"));
    }

    // ---- 2. ingestion --------------------------------------------------------

    private static async Task IngestAsync(
        IServiceProvider root,
        StringBuilder report,
        CallLedger ledger)
    {
        Line(report, string.Empty);
        Line(report, "## Ingestion");
        Line(report, string.Empty);
        Line(report, "| Ticker | CIK | Outcome |");
        Line(report, "| --- | --- | --- |");

        foreach (var (ticker, cik) in Universe)
        {
            using var scope = root.CreateScope();

            var outcome = await AcquireAsync(scope.ServiceProvider, cik, ledger);

            Line(report, Inv($"| {ticker} | {cik} | {outcome} |"));
        }
    }

    private static async Task<string> AcquireAsync(
        IServiceProvider services,
        string cik,
        CallLedger ledger)
    {
        var request = IngestionRequest.Create(
            SecEdgarProvider.Id,
            DataCategory.FinancialStatements,
            Region.UnitedStates,

            // A fresh subject per request. The factory copies it defensively now, but building one
            // here costs nothing and keeps the call site honest about what it owns.
            IngestionSubject.Create(CompanySubjectKind, cik),

            // Per attempt, not per request - see the Attempt field. The ledger check above is what
            // makes a rerun free; this only has to be unique enough that a failed attempt does not
            // bar the next one.
            CorrelationId.Create(Inv($"sec-facts-{cik}-{Attempt}")),
            services.GetRequiredService<IClock>().UtcNow);

        // companyfacts takes no window: the endpoint returns the company's whole history, so there
        // is no range to ask for and nothing to page through.

        var runs = services.GetRequiredService<IIngestionRunStore>();

        // "Already complete" has to mean the facts are there, not merely that a run finished.
        // Nineteen runs recorded Succeeded while storing nothing - the payload arrived gzipped and
        // was handed to the normaliser unread - and a ledger-only check would have skipped all
        // nineteen on every future run and reported the gap as coverage. So both must hold: the
        // ledger says it succeeded AND the store actually holds facts for this company.
        var context = services.GetRequiredService<AppDbContext>();

        var alreadyHeld = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == CompanySubjectKind && o.Subject.Identifier == cik)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
            .AnyAsync();

        if (alreadyHeld && await runs.HasCompletedAsync(request.Fingerprint()))
        {
            ledger.Skipped++;

            return "already complete, skipped";
        }

        var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

        if (result.WasFetched)
        {
            ledger.Fetched++;

            return Inv($"{result.Run.Outcome}, {result.ObservationsRecorded} observations");
        }

        ledger.NotFetched++;

        // Both, not one or the other. The gateway's rule id names which gate said no; the reason
        // carries the policy decision underneath it, and a report that prints only the first sends
        // the reader back to the database to find out what actually happened.
        var rule = result.Run.RefusalRuleId ?? "no rule recorded";
        var reason = result.Run.Reason ?? "no reason recorded";

        return Inv($"{result.Run.Outcome}: `{rule}` - {reason}");
    }

    // ---- 3. what actually landed --------------------------------------------

    private static async Task<Landed> VerifyAsync(IServiceProvider root, StringBuilder report)
    {
        using var scope = root.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var landed = new Landed();

        Line(report, string.Empty);
        Line(report, "## What landed, per company");
        Line(report, string.Empty);
        Line(report, "| Ticker | Facts | Attributes | Earliest period | Latest period | Restated |");
        Line(report, "| --- | ---: | ---: | --- | --- | ---: |");

        foreach (var (ticker, cik) in Universe)
        {
            var rows = await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == CompanySubjectKind && o.Subject.Identifier == cik)
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
                .Select(o => new
                {
                    o.Attribute,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                    o.Provenance.RetrievedAtUtc,
                })
                .ToListAsync();

            if (rows.Count == 0)
            {
                Line(report, Inv($"| {ticker} | 0 | 0 | — | — | 0 |"));

                continue;
            }

            landed.CompaniesWithFacts++;
            landed.Facts += rows.Count;

            // A restatement is a second version of the same figure for the same period.
            var restated = rows
                .GroupBy(r => new { r.Attribute, r.AsOfUtc })
                .Count(g => g.Count() > 1);

            landed.Restated += restated;
            landed.PublishedBeforePeriod += rows.Count(r => r.PublishedAtUtc < r.AsOfUtc);
            landed.PublishedAfterRetrieval += rows.Count(r => r.PublishedAtUtc > r.RetrievedAtUtc);

            foreach (var attribute in rows.Select(r => r.Attribute).Distinct())
            {
                landed.Attributes.Add(attribute);
            }

            var lag = rows.Average(r => (r.PublishedAtUtc - r.AsOfUtc).TotalDays);

            landed.LagSamples.Add(lag);

            var attributes = rows.Select(r => r.Attribute).Distinct().Count();
            var earliest = rows.Min(r => r.AsOfUtc);
            var latest = rows.Max(r => r.AsOfUtc);

            Line(report, Inv($"| {ticker} | {rows.Count} | {attributes} | {earliest:yyyy-MM-dd} | {latest:yyyy-MM-dd} | {restated} |"));
        }

        Line(report, string.Empty);
        Line(report, "## Headline");
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Companies asked | {Universe.Length} |"));
        Line(report, Inv($"| Companies that returned facts | **{landed.CompaniesWithFacts}** |"));
        Line(report, Inv($"| Fundamental observations stored | **{landed.Facts}** |"));
        Line(report, Inv($"| Distinct attributes | {landed.Attributes.Count} |"));
        Line(report, Inv($"| Figures with at least one restatement | {landed.Restated} |"));

        if (landed.LagSamples.Count > 0)
        {
            Line(report, Inv($"| Mean gap from period end to publication | {landed.LagSamples.Average():F0} days |"));
        }

        Line(report, Inv($"| Published before the period they describe | **{landed.PublishedBeforePeriod}** |"));
        Line(report, Inv($"| Published after they were retrieved | **{landed.PublishedAfterRetrieval}** |"));

        Line(report, string.Empty);
        Line(report, "The last two rows are the point-in-time guarantee, counted over what was");
        Line(report, "actually stored rather than over a fixture. Both must be zero: a figure that");
        Line(report, "was public before the period it describes, or before anyone fetched it, would");
        Line(report, "be visible to a backtest at a date the market could not have seen it.");

        Line(report, string.Empty);
        Line(report, "### Attributes now available");
        Line(report, string.Empty);

        foreach (var attribute in landed.Attributes.OrderBy(a => a, StringComparer.Ordinal))
        {
            Line(report, Inv($"- `{attribute}`"));
        }

        return landed;
    }

    private static async Task WriteAsync(string report)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "sec-backfill.md");

        var full = Path.GetFullPath(path);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await File.WriteAllTextAsync(full, report);
    }

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class CallLedger
    {
        public int Fetched { get; set; }

        public int Skipped { get; set; }

        public int NotFetched { get; set; }
    }

    private sealed class Landed
    {
        public int CompaniesWithFacts { get; set; }

        public int Facts { get; set; }

        public int Restated { get; set; }

        public int PublishedBeforePeriod { get; set; }

        public int PublishedAfterRetrieval { get; set; }

        public HashSet<string> Attributes { get; } = new(StringComparer.Ordinal);

        public List<double> LagSamples { get; } = [];
    }
}
