using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Operators;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Application.Sources.ReconcileSourceCoverage;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
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
/// The host the four-hundred-company universe stage runs under.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No limit, policy, cooldown or authorisation control is altered.</strong> The only
/// settings written here are the ones every gated run writes - the scheduler and dispatcher off so
/// that nothing but this test makes a request, the EDGAR connector on, and its request rate set
/// below the published ceiling. Everything the safety seam does is left exactly as configured.
/// </para>
/// <para>
/// The SEC contact address comes from the environment for the same reason it does in
/// <see cref="SecBackfillApiFactory"/>: EDGAR's fair-access policy requires a reachable human, and
/// that is an identity rather than a setting to commit.
/// </para>
/// <para>
/// <strong>The EDGAR settings below do not switch the connector on, and cannot.</strong> The
/// connector reads its own section while the container is being <em>built</em>, which is earlier
/// than a test factory's in-memory settings are applied - so
/// <c>appsettings.Development.json</c>'s deliberate <c>Enabled: false</c> is what
/// <c>AddInfrastructure</c> sees, and the connector is never registered. The environment variables
/// set by <c>scripts/run-universe.ps1</c> are what actually enable it, because they are in place
/// before the entry point runs. These keys stay here as the record of what the run intends, and
/// because they are what a future host that reads configuration later would use.
/// </para>
/// </remarks>
public sealed class UniverseApiFactory : WebApplicationFactory<Program>
{
    public const string OperatorId = "universe@operator.local";

    public const string ApplicationName = "AI-Investment-Analyst";

    public const string ContactVariable = "AIINV_SEC_CONTACT";

    /// <summary>Below EDGAR's published ceiling of ten a second, deliberately.</summary>
    public const int RequestsPerSecond = 5;

    public static string? Contact => Environment.GetEnvironmentVariable(ContactVariable);

    /// <summary>How this host reads the acquisition doors. Substituted only by tests.</summary>
    /// <remarks>
    /// A seam rather than a call to <see cref="Environment"/> so that the archive boundary below
    /// can be asserted both ways. Arming a door by mutating the process environment mid-run would
    /// be visible to every other test in the assembly, and xunit runs collections in parallel.
    /// </remarks>
    internal Func<string, string?> EnvironmentReader { get; init; } =
        Environment.GetEnvironmentVariable;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

            // The stored per-user environment first, then the process block - see
            // WindowsUserEnvironment. The EODHD credential arrives through this path.
            configuration.AddWindowsUserEnvironment();
            configuration.AddEnvironmentVariables();

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

            // ---- where an authorised document is kept ------------------------------------
            //
            // Ordinarily this host archives where the rest of the assembly does: the isolated
            // root TestArchiveRoot pins, inside build output, because a test's payloads are
            // fixtures and fixtures do not belong in the evidence store.
            //
            // An armed filing-document door says the opposite. That gate is the operator's
            // statement that this run is an authorised acquisition, and a document it obtains
            // costs an acquisition unit and cannot be re-fetched for free. Evidence like that
            // must not land somewhere a rebuild can remove, so the run opts into the durable
            // archive - through configuration, so the composition, the seam and the store are
            // all still exactly the ones the application uses.
            //
            // Added last on purpose. The isolation arrives as an environment variable, and an
            // in-memory source registered after AddEnvironmentVariables is what outranks it.
            if (SecFilingDocumentBatchDoor.IsOpen(EnvironmentReader))
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{RawArchiveOptions.SectionName}:{nameof(RawArchiveOptions.RootPath)}"] =
                        TestArchiveRoot.DurableRootPath,
                });
            }
        });

        builder.ConfigureTestServices(services =>
            services.AddScoped<IOperatorContext, UniverseOperator>());
    }

    private sealed class UniverseOperator : IOperatorContext
    {
        public OperatorIdentity? Current { get; } = OperatorIdentity.Create(
            OperatorId,
            "Point-in-time universe construction",
            [OperatorPrivilege.AdministerWatches]);
    }
}

/// <summary>
/// Everything the stages of the universe build share: shapes, paths and the authorised budget.
/// </summary>
internal static class Universe
{
    /// <summary>The five cohort cut dates membership is defined at.</summary>
    public static readonly DateOnly[] Cuts =
    [
        new(2021, 8, 31), new(2022, 8, 31), new(2023, 8, 31),
        new(2024, 8, 31), new(2025, 8, 31),
    ];

    public static readonly DateOnly WindowStart = new(2021, 9, 1);

    public static readonly DateOnly WindowEnd = new(2026, 8, 31);

    /// <summary>How many companies the universe holds.</summary>
    public const int Size = 400;

    /// <summary>The trial-family budget, fixed before any hypothesis names this base.</summary>
    public const int FamilyBudget = 5;

    /// <summary>How stale a filing may be and still count as reporting at a cut date.</summary>
    public const int ReportingWindowMonths = 15;

    public const string BaseName = "us-pit-400-2021-09-to-2026-08";

    /// <summary>The only taxonomy read, matching the connector's own rule.</summary>
    public const string Taxonomy = "us-gaap";

    /// <summary>The concept whose cross-section defines who was reporting at a cut.</summary>
    /// <remarks>
    /// Assets, because it is the most universally tagged figure in US GAAP: a filer that reports
    /// anything reports a balance sheet. Choosing a revenue concept for membership would exclude
    /// every company mid-way between accounting standards, and choosing net income would exclude
    /// the ones that made none.
    /// </remarks>
    public const string MembershipConcept = "Assets";

    /// <summary>
    /// The two concepts ranked on, because US filers do not agree on one.
    /// </summary>
    /// <remarks>
    /// <c>Revenues</c> is the legacy tag and still what most financials use;
    /// <c>RevenueFromContractWithCustomerExcludingAssessedTax</c> is what ASC 606 pushed nearly
    /// everyone else onto. Reading only one would rank half the market at zero, so both are read
    /// and the larger of the two is the company's revenue - recorded with the tag it came from.
    /// </remarks>
    public static readonly string[] RankingConcepts =
    [
        "Revenues",
        "RevenueFromContractWithCustomerExcludingAssessedTax",
    ];

    /// <summary>The calendar year ranked on: the last one fully reported by the first cut.</summary>
    public const int RankingYear = 2020;

    public const string PeriodKind = "Period";

    public const string CompanyKind = "Company";

    // ---- the authorised budget ---------------------------------------------------------------

    /// <summary>Free SEC calls this stage may spend, as authorised.</summary>
    public const int SecCallBudget = 821;

    /// <summary>Billable EODHD calls this stage may spend, as authorised.</summary>
    public const int EodhdCallBudget = 2;

    // ---- paths -------------------------------------------------------------------------------

    /// <summary>A path inside the repository, from the test binary's own location.</summary>
    public static string RepositoryPath(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var segments = new string[6 + parts.Length];

        segments[0] = AppContext.BaseDirectory;

        for (var i = 1; i < 6; i++)
        {
            segments[i] = "..";
        }

        Array.Copy(parts, 0, segments, 6, parts.Length);

        return Path.GetFullPath(Path.Combine(segments));
    }

    public static string RosterPath => RepositoryPath("artifacts", "universe", "roster.json");

    public static string ManifestPath =>
        RepositoryPath("declarations", "universe-400-2021-2026.json");

    public const string ManifestRelativePath = "declarations/universe-400-2021-2026.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Inv(FormattableString text) => FormattableString.Invariant(text);

    public static string Day(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The frame identifier the connector parses, for one concept and one period.</summary>
    public static string Frame(string concept, string unit, string period) =>
        Taxonomy + "/" + concept + "/" + unit + "/" + period;

    /// <summary>The instant frame whose cross-section stands for a cut date.</summary>
    /// <remarks>
    /// The second calendar quarter of the cut's own year: filed by mid-August for a calendar-year
    /// filer, which is the most recent quarter a 31 August cut can expect to see.
    /// </remarks>
    public static string MembershipFrame(DateOnly cut) =>
        Frame(MembershipConcept, "USD", Inv($"CY{cut.Year}Q2I"));

    public static string RankingFrame(string concept) =>
        Frame(concept, "USD", Inv($"CY{RankingYear}"));

    /// <summary>Ten-digit CIK from whatever shape the JSON used, or null when it is not one.</summary>
    public static string? Cik(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number <= 0 ? null : number.ToString("D10", CultureInfo.InvariantCulture);
        }

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(
                element.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed <= 0 ? null : parsed.ToString("D10", CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>
    /// Reads a frames document into CIK-to-value, or an empty map when it is not one.
    /// </summary>
    /// <remarks>
    /// Forgiving about fields it does not need and unforgiving about the one it does: a row
    /// without a readable CIK is skipped rather than guessed at, because a wrong CIK does not
    /// fail, it silently admits another company to the universe.
    /// </remarks>
    public static Dictionary<string, decimal> ReadFrame(byte[] payload)
    {
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("cik", out var cikElement))
            {
                continue;
            }

            var cik = Cik(cikElement);

            if (cik is null)
            {
                continue;
            }

            var value = 0m;

            if (row.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.Number)
            {
                val.TryGetDecimal(out value);
            }

            // Last wins. A CIK appearing twice in one frame is EDGAR carrying two segments of the
            // same figure; either is a size, and neither decides membership on its own.
            values[cik] = value;
        }

        return values;
    }

    public static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, content);
    }

    public static Roster? LoadRoster() =>
        File.Exists(RosterPath)
            ? JsonSerializer.Deserialize<Roster>(File.ReadAllText(RosterPath), Json)
            : null;

    // ---- shapes ------------------------------------------------------------------------------

    public sealed record RosterEntry(
        string Cik,
        decimal Revenue,
        string RevenueTag,
        List<string> FrameCohorts);

    public sealed record Roster(
        string BuiltAtUtc,
        int RankingYear,
        List<string> CutDates,
        int FrameCalls,
        int CrossSectionSize,
        List<RosterEntry> Members);
}

/// <summary>
/// Stage one: the period cross-sections, and the four hundred companies they name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the step that removes survivorship bias, and it is the only one that could.</strong>
/// Every per-company EDGAR endpoint answers a question about a company you already named, so a
/// universe assembled from them can only ever describe the list you started with - and any list of
/// companies obtainable today is a list of the ones that survived until today. The frames endpoint
/// answers a question about a <em>period</em>: which filers reported this figure for it. The
/// companies later acquired, taken private or wound up are in that answer, because they were in
/// that quarter.
/// </para>
/// <para>
/// <strong>Seven requests, and the arithmetic is the point.</strong> Five instant frames, one per
/// cut date, each naming everybody who was reporting then; two annual revenue frames for the
/// ranking, because US filers split between the legacy tag and the ASC 606 one. Seven documents
/// establish membership for a universe that would take tens of thousands of per-company calls to
/// assemble by asking - and would still be a survivor list at the end of it.
/// </para>
/// <para>
/// <strong>A stated limitation, recorded rather than papered over.</strong> A frame is EDGAR's
/// present-day assembly of what was reported for a period, so it can contain a figure first filed
/// after the cut date it stands for - a restatement, or a late filer. That is a look-ahead in
/// membership, and it is bounded rather than ignored: the filing dates on the company facts are the
/// point-in-time record, the manifest intersects cohorts with them, and gate 3 refuses any cohort
/// preceding a member's first observed filing.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_FRAMES=1</c>. Free, public-domain requests; nothing billable.
/// </para>
/// </remarks>
public sealed class UniverseFramesTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_FRAMES";

    /// <summary>The fewest filers a real cross-section of US GAAP assets can hold.</summary>
    /// <remarks>
    /// Thousands report a balance sheet each quarter. A few hundred would mean a truncated or
    /// mis-parsed document rather than a small market, and the universe must not be ranked out of
    /// one.
    /// </remarks>
    private const int PlausibleCrossSection = 1000;

    private static readonly string Attempt =
        DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseFramesTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_period_cross_sections_name_the_universe_including_the_companies_that_died()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The frame stage is off. Set {GateVariable}=1 to run it. It makes seven free requests " +
            "to a U.S. government service and starts no acquisition.");

        Skip.If(
            string.IsNullOrWhiteSpace(UniverseApiFactory.Contact),
            $"{UniverseApiFactory.ContactVariable} is not set. EDGAR's fair-access policy requires " +
            "every request to carry a contact address, and this run will not invent one.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;
        var report = new StringBuilder();
        var calls = 0;

        await SourcesAsync(root);

        report.AppendLine("# Universe stage 1 - the period cross-sections");
        report.AppendLine();
        report.AppendLine("Seven free EDGAR frame requests. Nothing billable, no prices, no strategy.");
        report.AppendLine();
        report.AppendLine("| Frame | Requested | Filers |");
        report.AppendLine("| --- | --- | ---: |");

        var membership = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);

        foreach (var cut in Universe.Cuts)
        {
            var identifier = Universe.MembershipFrame(cut);
            var (values, fetched) = await FrameAsync(root, identifier);

            calls += fetched ? 1 : 0;
            membership[Universe.Day(cut)] = values;

            report.AppendLine(Universe.Inv(
                $"| `{identifier}` | {(fetched ? "fetched" : "already held")} | {values.Count} |"));
        }

        var revenue = new Dictionary<string, (decimal Value, string Tag)>(StringComparer.Ordinal);

        foreach (var concept in Universe.RankingConcepts)
        {
            var identifier = Universe.RankingFrame(concept);
            var (values, fetched) = await FrameAsync(root, identifier);

            calls += fetched ? 1 : 0;

            foreach (var pair in values)
            {
                // The larger of the two tags. A company reporting both is describing the same
                // business twice, and the smaller figure is the partial one.
                if (!revenue.TryGetValue(pair.Key, out var held) || pair.Value > held.Value)
                {
                    revenue[pair.Key] = (pair.Value, concept);
                }
            }

            report.AppendLine(Universe.Inv(
                $"| `{identifier}` | {(fetched ? "fetched" : "already held")} | {values.Count} |"));
        }

        var first = membership[Universe.Day(Universe.Cuts[0])];

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Requests actually sent this run: **{calls}**."));
        report.AppendLine();

        Assert.True(
            first.Count >= PlausibleCrossSection,
            Universe.Inv($"The first cross-section named {first.Count} filers. ") +
            Universe.Inv($"At least {PlausibleCrossSection} were expected: a US GAAP assets frame ") +
            "holds thousands, and a small one means a truncated or mis-parsed document rather than " +
            "a small market. The universe must not be ranked out of it.");

        // Ranked among the filers actually reporting at the first cut. A company with a 2020
        // revenue figure but no 2021 balance sheet is not a member of a universe starting in 2021,
        // however large it was.
        var candidates = new List<(string Cik, decimal Value, string Tag)>();

        foreach (var cik in first.Keys)
        {
            if (revenue.TryGetValue(cik, out var found))
            {
                candidates.Add((cik, found.Value, found.Tag));
            }
        }

        var ranked = candidates
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Cik, StringComparer.Ordinal)
            .Take(Universe.Size)
            .ToList();

        Assert.True(
            ranked.Count == Universe.Size,
            Universe.Inv($"Only {ranked.Count} of the {first.Count} filers in the first ") +
            Universe.Inv($"cross-section carry a {Universe.RankingYear} revenue figure under either ") +
            "tag, so a four-hundred-company universe cannot be ranked out of them. Widening the " +
            "tag list is a design decision, not a fix to apply mid-run.");

        var members = ranked
            .Select(r => new Universe.RosterEntry(
                r.Cik,
                r.Value,
                r.Tag,
                Universe.Cuts
                    .Where(cut => membership[Universe.Day(cut)].ContainsKey(r.Cik))
                    .Select(Universe.Day)
                    .ToList()))
            .OrderBy(m => m.Cik, StringComparer.Ordinal)
            .ToList();

        var roster = new Universe.Roster(
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Universe.RankingYear,
            Universe.Cuts.Select(Universe.Day).ToList(),
            calls,
            first.Count,
            members);

        await Universe.WriteAsync(
            Universe.RosterPath,
            JsonSerializer.Serialize(roster, Universe.Json) + "\n");

        Describe(report, membership, members);

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-frames.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        var last = membership[Universe.Day(Universe.Cuts[^1])];
        var gone = members.Count(m => !last.ContainsKey(m.Cik));

        Assert.True(
            gone > 0,
            "Not one of the four hundred is absent from the final cross-section. A universe fixed " +
            "in 2021 and tracked to 2025 without a single company leaving it is a survivor list, " +
            "which is the exact defect this stage exists to remove.");
    }

    private static void Describe(
        StringBuilder report,
        Dictionary<string, Dictionary<string, decimal>> membership,
        List<Universe.RosterEntry> members)
    {
        report.AppendLine("## Membership by cut");
        report.AppendLine();
        report.AppendLine("| Cut | Filers in the cross-section | Of the four hundred, still reporting |");
        report.AppendLine("| --- | ---: | ---: |");

        foreach (var cut in Universe.Cuts)
        {
            var day = Universe.Day(cut);
            var section = membership[day];
            var present = members.Count(m => section.ContainsKey(m.Cik));

            report.AppendLine(Universe.Inv($"| {day} | {section.Count} | {present} |"));
        }

        report.AppendLine();
        report.AppendLine("The right-hand column falling is the dropouts: companies reporting in");
        report.AppendLine("2021 that had stopped by a later cut, which a universe built from today's");
        report.AppendLine("listings could not contain.");
        report.AppendLine();
        report.AppendLine("| Revenue tag ranked on | Companies |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in members
            .GroupBy(m => m.RevenueTag, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| `{group.Key}` | {group.Count()} |"));
        }
    }

    // ---- fetching ----------------------------------------------------------------------------

    /// <summary>
    /// Registers, reconciles and activates, in that order, through the seam.
    /// </summary>
    /// <remarks>
    /// The middle step is the one that is easy to leave out and impossible to work around.
    /// Registration fills gaps and does not reconcile, so the sec-edgar row written before this
    /// build existed declares four categories and knows nothing about market-wide frames - and
    /// every frame request is refused by <c>source.supplies-category@1</c> naming a category count
    /// that is merely out of date. Widening the row is a deliberate, audited act rather than
    /// something seeding does behind an operator's back, so it is asked for here explicitly.
    /// </remarks>
    private static async Task SourcesAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var services = scope.ServiceProvider;

        var registered = await services.GetRequiredService<RegisterKnownSourcesHandler>()
            .HandleAsync();

        var reconciled = await services.GetRequiredService<ReconcileSourceCoverageHandler>()
            .HandleAsync(SecEdgarProvider.Id);

        var providers = string.Join(
            ", ",
            services.GetServices<IDataProvider>().Select(p => p.SourceId.Value));

        await services.GetRequiredService<ActivateSourceHandler>().HandleAsync(SecEdgarProvider.Id);

        // Read back, in a fresh scope, and refuse to continue if the row still cannot serve a
        // frame.
        //
        // The reconciliation returns a status nobody was reading, and a status nobody reads is a
        // control nobody has. Ignoring it cost a run: the widening did not take, seven frame
        // requests were attempted anyway, and the failure that came back named a stale category
        // count rather than the reconciliation that was supposed to have fixed it. Now the run
        // stops here, before the network, and says which of the two went wrong.
        using var check = root.CreateScope();

        var stored = await check.ServiceProvider
            .GetRequiredService<ISourceRegistry>()
            .GetByIdAsync(SecEdgarProvider.Id);

        var categories = stored is null
            ? "no registry row at all"
            : string.Join(", ", stored.Categories);

        if (stored is null ||
            !stored.Supplies(DataCategory.MarketWideDisclosure, Region.UnitedStates))
        {
            throw new InvalidOperationException(
                $"The sec-edgar registry row still does not supply MarketWideDisclosure. " +
                $"Reconciliation answered {reconciled.Status}: {reconciled.Reason} " +
                $"Seeding answered [{string.Join("; ", registered.Select(r => r.SourceId + " -> " + r.Outcome))}]. " +
                $"Connectors resolved: [{providers}]. The row declares [{categories}], and the " +
                "shipped definition declares " +
                $"[{string.Join(", ", new SecEdgarSource().Definition(DateTime.UtcNow).Categories)}]. " +
                "Every frame request would be refused, so none is made.");
        }
    }

    /// <summary>
    /// Fetches one frame, or reads the one already archived, and returns what it names.
    /// </summary>
    /// <remarks>
    /// The archive is asked first and the network second. A frame already held is one already paid
    /// for in fair-access terms, and re-requesting it to arrive at the same bytes would spend
    /// somebody else's rate limit to learn nothing.
    /// </remarks>
    private static async Task<(Dictionary<string, decimal> Values, bool Fetched)> FrameAsync(
        IServiceProvider root,
        string identifier)
    {
        using var scope = root.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var held = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.MarketWideDisclosure)
                .ToListAsync())
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .FirstOrDefault(r => string.Equals(
                r.Request.Subject.Identifier,
                identifier,
                StringComparison.Ordinal));

        if (held is not null)
        {
            foreach (var hash in held.Artifacts)
            {
                var stored = await archive.RetrieveAsync(hash);

                if (stored is not null)
                {
                    return (Universe.ReadFrame(stored), false);
                }
            }
        }

        var request = IngestionRequest.Create(
            SecEdgarProvider.Id,
            DataCategory.MarketWideDisclosure,

            // A period is not a place, and a request still has to name one. The United States is
            // whose disclosure regime this is, which is the honest answer for a frame of US GAAP.
            Region.UnitedStates,
            IngestionSubject.Create(Universe.PeriodKind, identifier),
            CorrelationId.Create(Universe.Inv($"universe-frame-{Attempt}-{Digest(identifier)}")),
            services.GetRequiredService<IClock>().UtcNow);

        var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

        if (!result.WasFetched)
        {
            throw new InvalidOperationException(
                $"The frame '{identifier}' was not fetched: {result.Run.Outcome} " +
                $"({result.Run.RefusalRuleId ?? "no rule"} - {result.Run.Reason ?? "no reason"}). " +
                "The universe cannot be built from a cross-section that never arrived.");
        }

        foreach (var hash in result.Run.Artifacts)
        {
            var payload = await archive.RetrieveAsync(hash);

            if (payload is not null)
            {
                return (Universe.ReadFrame(payload), true);
            }
        }

        throw new InvalidOperationException(
            $"The frame '{identifier}' was fetched and archived nothing readable back.");
    }

    /// <summary>A short stable digest, so a correlation stays inside its length limit.</summary>
    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant()[..12];
}
