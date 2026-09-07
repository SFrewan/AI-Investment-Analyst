using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Application.Sources.ReconcileSourceCoverage;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The per-company half of the universe build: profiles, then facts.
/// </summary>
/// <remarks>
/// <para>
/// Both stages walk the same roster and differ only in which EDGAR document they ask for, so the
/// loop, the resume rule and the ledger are written once here. What they have in common is the part
/// worth getting right: <strong>a company already held is not re-requested</strong>, checked
/// against the store rather than against the run ledger alone. Nineteen runs once recorded
/// <c>Succeeded</c> while storing nothing, and a ledger-only check would have skipped all nineteen
/// forever and reported the hole as coverage.
/// </para>
/// </remarks>
internal static class UniverseCompanies
{
    /// <summary>Names an attempt, so a failed one cannot bar the next one forever.</summary>
    /// <remarks>
    /// The action gateway's processed-action key is permanent by design and cannot be released.
    /// Data idempotency is unaffected: a company whose document already landed is skipped by the
    /// check below before a correlation is ever built.
    /// </remarks>
    public static readonly string Attempt =
        DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The gap left between requests, so the connector's declared quota is respected rather than
    /// tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate limiter refuses; it does not wait, and says so: "the run is recorded as refused so
    /// the scheduler can retry it rather than a thread waiting." That is the right design for a
    /// scheduler and it means a caller in a tight loop refuses most of its own work - the first
    /// four-hundred-company pass sent 339 requests and had 61 refused for quota, none of which had
    /// anything to do with EDGAR.
    /// </para>
    /// <para>
    /// So the loop paces itself below the declared five a second rather than discovering the
    /// ceiling by hitting it. This is the caller obeying a limit, not the limit being relaxed:
    /// nothing about the quota, the policy or the refusal changes, and a request that still gets
    /// refused is still refused.
    /// </para>
    /// </remarks>
    public const int MillisecondsBetweenRequests = 240;

    /// <summary>How many times to come back for the companies a pass could not reach.</summary>
    /// <remarks>
    /// Bounded, and it stops early when a pass refuses nothing. A refusal that survives four
    /// paced passes is not a timing problem and must be reported rather than retried forever.
    /// </remarks>
    public const int MaxPasses = 4;

    public sealed class Ledger
    {
        /// <summary>Requests that actually reached the network, across every pass.</summary>
        public int Fetched { get; set; }

        public int Skipped { get; set; }

        /// <summary>Refusals still outstanding after the last pass.</summary>
        public int Refused { get; set; }

        public int Observations { get; set; }

        public int Passes { get; set; }

        public List<string> Failures { get; } = [];
    }

    /// <summary>Registers, reconciles the declared coverage, then activates - through the seam.</summary>
    public static async Task RegisterAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        await scope.ServiceProvider.GetRequiredService<RegisterKnownSourcesHandler>().HandleAsync();

        await scope.ServiceProvider.GetRequiredService<ReconcileSourceCoverageHandler>()
            .HandleAsync(SecEdgarProvider.Id);

        await scope.ServiceProvider.GetRequiredService<ActivateSourceHandler>()
            .HandleAsync(SecEdgarProvider.Id);
    }

    /// <summary>
    /// Acquires one document for one company, skipping it when the store already holds one.
    /// </summary>
    /// <returns>True when a request was actually sent, so the caller knows to pace itself.</returns>
    private static async Task<bool> AcquireAsync(
        IServiceProvider services,
        string cik,
        DataCategory category,
        string attributePrefix,
        string label,
        Ledger ledger)
    {
        var context = services.GetRequiredService<AppDbContext>();

        var alreadyHeld = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind && o.Subject.Identifier == cik)
            .Where(o => EF.Functions.Like(o.Attribute, attributePrefix + "%"))
            .AnyAsync();

        if (alreadyHeld)
        {
            ledger.Skipped++;

            return false;
        }

        var request = IngestionRequest.Create(
            SecEdgarProvider.Id,
            category,
            Region.UnitedStates,

            // A fresh subject per request. Owned entities associate by reference identity, and a
            // shared instance is how four separate defects reached production on this path.
            IngestionSubject.Create(Universe.CompanyKind, cik),
            CorrelationId.Create(Universe.Inv($"universe-{label}-{cik}-{Attempt}")),
            services.GetRequiredService<IClock>().UtcNow);

        var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

        if (result.WasFetched)
        {
            ledger.Fetched++;
            ledger.Observations += result.ObservationsRecorded;

            return true;
        }

        ledger.Refused++;

        var rule = result.Run.RefusalRuleId ?? "no rule recorded";
        var reason = result.Run.Reason ?? "no reason recorded";

        ledger.Failures.Add(Universe.Inv($"{cik}: {result.Run.Outcome} `{rule}` - {reason}"));

        // A refusal reached no network, so the caller has nothing to pace against.
        return false;
    }

    /// <summary>
    /// Walks the roster, paced, and comes back for whatever a pass could not reach.
    /// </summary>
    /// <remarks>
    /// Companies already held are skipped before a request is built, so a second pass costs
    /// nothing for the ones the first pass got. That is the same idempotency that makes a rerun of
    /// the whole stage free, used here at a smaller scale.
    /// </remarks>
    public static async Task WalkAsync(
        IServiceProvider root,
        IReadOnlyList<string> ciks,
        DataCategory category,
        string attributePrefix,
        string label,
        Ledger ledger,
        StringBuilder report)
    {
        ArgumentNullException.ThrowIfNull(ciks);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(report);

        report.AppendLine();
        report.AppendLine("## Passes");
        report.AppendLine();
        report.AppendLine("| Pass | Requests sent | Still outstanding |");
        report.AppendLine("| ---: | ---: | ---: |");

        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            var sentBefore = ledger.Fetched;

            ledger.Refused = 0;
            ledger.Failures.Clear();
            ledger.Passes = pass;

            foreach (var cik in ciks)
            {
                using var scope = root.CreateScope();

                var sent = await AcquireAsync(
                    scope.ServiceProvider, cik, category, attributePrefix, label, ledger);

                if (sent)
                {
                    await Task.Delay(MillisecondsBetweenRequests);
                }
            }

            report.AppendLine(Universe.Inv(
                $"| {pass} | {ledger.Fetched - sentBefore} | {ledger.Refused} |"));

            if (ledger.Refused == 0)
            {
                break;
            }
        }
    }

    public static void Summarise(StringBuilder report, Ledger ledger, int companies)
    {
        report.AppendLine();
        report.AppendLine("## Requests");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Companies on the roster | {companies} |"));
        report.AppendLine(Universe.Inv($"| **Requests sent this run** | **{ledger.Fetched}** |"));
        report.AppendLine(Universe.Inv($"| Already held, not requested | {ledger.Skipped} |"));
        report.AppendLine(Universe.Inv($"| Passes taken | {ledger.Passes} of {MaxPasses} |"));
        report.AppendLine(Universe.Inv($"| Still refused after the last pass | {ledger.Refused} |"));
        report.AppendLine(Universe.Inv($"| Observations recorded | {ledger.Observations} |"));

        if (ledger.Failures.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine("### Still outstanding after the last pass, recorded not dropped");
        report.AppendLine();

        foreach (var failure in ledger.Failures.Take(40))
        {
            report.AppendLine("- " + failure);
        }

        if (ledger.Failures.Count > 40)
        {
            report.AppendLine(Universe.Inv($"- …and {ledger.Failures.Count - 40} more."));
        }
    }
}

/// <summary>
/// Stage two: who each of the four hundred is - ticker, exchange, SIC and name.
/// </summary>
/// <remarks>
/// <para>
/// The frames name companies by CIK and nothing else. A CIK cannot be priced, sorted into a sector
/// or recognised by a human, and the submissions document is where EDGAR keeps all three. It is
/// also the only authoritative CIK-to-ticker mapping there is: a vendor's is a mapping of the
/// tickers that vendor sells.
/// </para>
/// <para>
/// <strong>Four hundred requests, free, and each one skipped if its answer is already stored.</strong>
/// Gated on <c>AIINV_UNIVERSE_PROFILES=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseProfileTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_PROFILES";

    /// <summary>How many of the four hundred must resolve before the stage counts as done.</summary>
    /// <remarks>
    /// Not four hundred. A company that re-registered carries its profile under another CIK, and
    /// one or two silent gaps are a property of EDGAR rather than a defect in this run - but a
    /// wholesale failure must not pass quietly, and the missing ones are recorded either way.
    /// </remarks>
    private const int MinimumResolved = 380;

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseProfileTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_company_on_the_roster_is_given_a_ticker_a_sector_and_a_name()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The profile stage is off. Set {GateVariable}=1 to run it. Free EDGAR requests only.");

        Skip.If(
            string.IsNullOrWhiteSpace(UniverseApiFactory.Contact),
            $"{UniverseApiFactory.ContactVariable} is not set. EDGAR's fair-access policy requires " +
            "every request to carry a contact address, and this run will not invent one.");

        var loaded = Universe.LoadRoster();

        Skip.If(
            loaded is null,
            "No roster is on disk. Stage one builds it from the period cross-sections.");

        var roster = loaded!;
        var watch = Stopwatch.StartNew();
        var root = _factory.Services;
        var ledger = new UniverseCompanies.Ledger();
        var report = new StringBuilder();

        await UniverseCompanies.RegisterAsync(root);

        int opportunitiesBefore;

        using (var before = root.CreateScope())
        {
            opportunitiesBefore = await before.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Opportunities.AsNoTracking().CountAsync();
        }

        report.AppendLine("# Universe stage 2 - company profiles");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"{roster.Members.Count} companies, one free EDGAR submissions request each."));
        report.AppendLine("No prices, nothing billable, no opportunity and no strategy.");

        await UniverseCompanies.WalkAsync(
            root,
            roster.Members.Select(m => m.Cik).ToList(),
            DataCategory.CompanyProfile,
            "company.",
            "profile",
            ledger,
            report);

        UniverseCompanies.Summarise(report, ledger, roster.Members.Count);

        int resolved;
        int withTicker;

        using (var after = root.CreateScope())
        {
            var context = after.ServiceProvider.GetRequiredService<AppDbContext>();

            var ciks = roster.Members.Select(m => m.Cik).ToHashSet(StringComparer.Ordinal);

            var rows = await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == Universe.CompanyKind)
                .Where(o => o.Attribute == "company.name" || o.Attribute == "company.ticker")
                .Select(o => new { Cik = o.Subject.Identifier!, o.Attribute })
                .Distinct()
                .ToListAsync();

            resolved = rows
                .Where(r => string.Equals(r.Attribute, "company.name", StringComparison.Ordinal))
                .Count(r => ciks.Contains(r.Cik));

            withTicker = rows
                .Where(r => string.Equals(r.Attribute, "company.ticker", StringComparison.Ordinal))
                .Count(r => ciks.Contains(r.Cik));

            var opportunitiesAfter = await context.Opportunities.AsNoTracking().CountAsync();

            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"| Profiles resolved | **{resolved}** of {roster.Members.Count} |"));
            report.AppendLine(Universe.Inv($"| Carrying a ticker | **{withTicker}** |"));
            report.AppendLine(Universe.Inv(
                $"| Opportunities before / after | {opportunitiesBefore} / {opportunitiesAfter} |"));

            Assert.Equal(opportunitiesBefore, opportunitiesAfter);
        }

        report.AppendLine();
        report.AppendLine("A company with no ticker in its own submissions document is not dropped.");
        report.AppendLine("It is carried into the manifest and recorded as unmatched, which is what");
        report.AppendLine("keeps a lookup failure from quietly becoming survivorship bias.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-profiles.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        Assert.True(
            resolved >= MinimumResolved,
            Universe.Inv($"Only {resolved} of {roster.Members.Count} companies resolved a profile, ") +
            Universe.Inv($"against a floor of {MinimumResolved}. That is a wholesale failure rather ") +
            "than the handful of gaps EDGAR normally has, and it must not pass quietly.");
    }
}

/// <summary>
/// Stage three: the XBRL company facts for every member, annual and quarterly.
/// </summary>
/// <remarks>
/// <para>
/// This is the evidence base itself. The filing dates on these facts are also the point-in-time
/// record the manifest's membership is checked against: a frame says a company reported for a
/// period, and a fact says when that report was published, which is the only one of the two a
/// backtest may act on.
/// </para>
/// <para>
/// <strong>Four hundred requests, free, and the payloads are large.</strong> Company facts return
/// a company's entire XBRL history in one document, which is why there is no window here and why a
/// rerun is cheap: the ledger and the store both refuse to fetch a company twice.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_FACTS=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseFactsTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_FACTS";

    /// <summary>How many of the four hundred must yield facts before the stage counts as done.</summary>
    private const int MinimumWithFacts = 360;

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseFactsTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_company_on_the_roster_yields_its_filed_facts()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The facts stage is off. Set {GateVariable}=1 to run it. Free EDGAR requests only.");

        Skip.If(
            string.IsNullOrWhiteSpace(UniverseApiFactory.Contact),
            $"{UniverseApiFactory.ContactVariable} is not set. EDGAR's fair-access policy requires " +
            "every request to carry a contact address, and this run will not invent one.");

        var loaded = Universe.LoadRoster();

        Skip.If(
            loaded is null,
            "No roster is on disk. Stage one builds it from the period cross-sections.");

        var roster = loaded!;
        var watch = Stopwatch.StartNew();
        var root = _factory.Services;
        var ledger = new UniverseCompanies.Ledger();
        var report = new StringBuilder();

        await UniverseCompanies.RegisterAsync(root);

        int opportunitiesBefore;

        using (var before = root.CreateScope())
        {
            opportunitiesBefore = await before.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Opportunities.AsNoTracking().CountAsync();
        }

        report.AppendLine("# Universe stage 3 - filed company facts");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"{roster.Members.Count} companies, one free EDGAR company-facts request each."));
        report.AppendLine("No prices, nothing billable, no opportunity, no prediction, no order.");

        await UniverseCompanies.WalkAsync(
            root,
            roster.Members.Select(m => m.Cik).ToList(),
            DataCategory.FinancialStatements,
            FinancialFigures.Prefix,
            "facts",
            ledger,
            report);

        UniverseCompanies.Summarise(report, ledger, roster.Members.Count);

        int withFacts;

        using (var after = root.CreateScope())
        {
            var context = after.ServiceProvider.GetRequiredService<AppDbContext>();

            var ciks = roster.Members.Select(m => m.Cik).ToHashSet(StringComparer.Ordinal);

            var held = (await context.Observations
                    .AsNoTracking()
                    .Where(o => o.Subject.Kind == Universe.CompanyKind)
                    .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
                    .Select(o => o.Subject.Identifier!)
                    .Distinct()
                    .ToListAsync())
                .ToHashSet(StringComparer.Ordinal);

            withFacts = ciks.Count(held.Contains);

            var quarterly = await context.Observations
                .AsNoTracking()
                .Where(o => EF.Functions.Like(
                    o.Attribute,
                    FinancialFigures.QuarterlyPrefix + "%"))
                .CountAsync();

            var opportunitiesAfter = await context.Opportunities.AsNoTracking().CountAsync();

            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"| Companies holding facts | **{withFacts}** of {roster.Members.Count} |"));
            report.AppendLine(Universe.Inv($"| Quarterly observations in the store | {quarterly} |"));
            report.AppendLine(Universe.Inv(
                $"| Opportunities before / after | {opportunitiesBefore} / {opportunitiesAfter} |"));

            Assert.Equal(opportunitiesBefore, opportunitiesAfter);
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-facts.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        Assert.True(
            withFacts >= MinimumWithFacts,
            Universe.Inv($"Only {withFacts} of {roster.Members.Count} companies hold facts, ") +
            Universe.Inv($"against a floor of {MinimumWithFacts}. The manifest's filing history ") +
            "comes from these documents, and a universe missing a tenth of them cannot be judged.");
    }
}
