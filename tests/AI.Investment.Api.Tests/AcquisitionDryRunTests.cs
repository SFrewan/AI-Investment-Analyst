using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Everything the price acquisition would do, computed without doing any of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called.</strong> The requests are built exactly as the acquisition would
/// build them and their fingerprints computed, then thrown away. Ingestion-run and observation
/// counts are asserted unchanged either side, so a dry run that started acquiring would fail rather
/// than succeed quietly.
/// </para>
/// <para>
/// <strong>What it is for.</strong> A fingerprint that already sits in the ledger fetches nothing,
/// so an acquisition whose window happens to match a previous one spends a subscription and returns
/// silence. Checking that before spending 710 billable requests is the whole point, and it is the
/// same check that caught the pilot's overlap a stage ago.
/// </para>
/// <para>
/// It also evaluates gate 6's sealed rule against the prices that already exist, which is the only
/// honest way to test a coverage rule before there is coverage to test it on.
/// </para>
/// </remarks>
public sealed class AcquisitionDryRunTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_ACQUISITION_DRY_RUN";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";

    /// <summary>Used only when the ledger holds no price run to take the kind from.</summary>
    private const string DefaultSecurityKind = "Security";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public AcquisitionDryRunTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_whole_acquisition_is_planned_and_priced_without_sending_a_request()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The acquisition dry run is off. Set {GateVariable}=1 to run it. It makes no provider "
            + "call of any kind.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(IdentityResolution.IsSealedAs(
            Encoding.UTF8.GetString(manifestBefore), SealedFingerprint));

        // ---- who would be asked for -------------------------------------------------------------

        var members = await MembersAsync();

        Assert.True(members.Count == 400, Universe.Inv($"the identity table holds {members.Count} members, not the sealed 400"));

        var acquirable = members.Where(m => m.Ready).ToList();
        var excluded = members.Where(m => !m.Ready).ToList();

        // ---- what would be requested -------------------------------------------------------------

        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();

        // The ledger, as fingerprints. Read from the runs themselves rather than asked of a store,
        // so the dry run and the acquisition agree on what "already satisfied" means by using the
        // one function that decides it. A run that failed is not satisfaction: it must be re-tried,
        // and counting it here would under-state the bill by exactly the requests most likely to
        // still be needed.
        var ledger = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var satisfied = ledger
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .Select(r => r.Request.Fingerprint())
            .ToHashSet(StringComparer.Ordinal);

        // The subject kind is taken from the price runs already in the ledger, never assumed. It is
        // part of the fingerprint, so a kind guessed differently from the one the acquisition
        // actually uses would make every ledger comparison miss - and a suppression check that can
        // only ever answer "nothing is satisfied" is worse than no check, because it reads like one.
        var priorPriceRun = ledger
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefault();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        var planned = new List<Planned>(acquirable.Count * 2);

        foreach (var member in acquirable)
        {
            var symbol = AcquisitionPlanning.SymbolFor(member.Ticker!);

            foreach (var (source, category) in new[]
            {
                (PriceSource, DataCategory.MarketPrices),
                (ActionsSource, DataCategory.CorporateActions),
            })
            {
                // A fresh subject and a fresh window per request. Both are owned entities, and
                // sharing one instance across two requests in a scope is the re-parenting defect
                // this repository has already met three times.
                var request = IngestionRequest.Create(
                    SourceId.Create(source),
                    category,
                    region,
                    IngestionSubject.Create(subjectKind, symbol),
                    // The bare ticker, not the symbol: a correlation identifier admits only ASCII
                    // letters, digits, '-' and '_', and `AE.US` carries a dot. It is outside the
                    // fingerprint either way, so this changes what the run is called and nothing
                    // about what it would fetch.
                    CorrelationId.Create(Universe.Inv($"dry-run-{member.Ticker}-{category}")),
                    clock.UtcNow,
                    DateRange.Create(WindowStart, WindowEnd));

                var fingerprint = request.Fingerprint();

                planned.Add(new Planned(
                    member.Cik,
                    member.Name,
                    symbol,
                    source,
                    category.ToString(),
                    fingerprint,
                    satisfied.Contains(fingerprint)));
            }
        }

        // ---- gate 9: determinism, provable now ----------------------------------------------------

        // The same request built twice yields the same fingerprint, and a different window yields a
        // different one. That is what makes a second identical run a no-op and a genuinely new
        // window real work - the two halves of idempotency that can be proved before any run.
        var repeat = IngestionRequest.Create(
            SourceId.Create(PriceSource),
            DataCategory.MarketPrices,
            region,
            IngestionSubject.Create(subjectKind, "AAPL.US"),
            CorrelationId.Create("a-different-correlation-id"),
            clock.UtcNow.AddHours(3),
            DateRange.Create(WindowStart, WindowEnd));

        var again = IngestionRequest.Create(
            SourceId.Create(PriceSource),
            DataCategory.MarketPrices,
            region,
            IngestionSubject.Create(subjectKind, "AAPL.US"),
            CorrelationId.Create("another-one-entirely"),
            clock.UtcNow.AddDays(9),
            DateRange.Create(WindowStart, WindowEnd));

        var shorter = IngestionRequest.Create(
            SourceId.Create(PriceSource),
            DataCategory.MarketPrices,
            region,
            IngestionSubject.Create(subjectKind, "AAPL.US"),
            CorrelationId.Create("a-different-correlation-id"),
            clock.UtcNow,
            DateRange.Create(WindowStart, WindowEnd.AddDays(-1)));

        Assert.Equal(repeat.Fingerprint(), again.Fingerprint());
        Assert.NotEqual(repeat.Fingerprint(), shorter.Fingerprint());

        // ---- symbol construction, recorded before it is judged -------------------------------------

        // Only exchange US carries a configured session, so a symbol that does not end in `.US`
        // normalises into an unstated-session quarantine rather than into observations. A ticker
        // that already carries a dot - a class marker such as `BRK.B` - keeps its own and never
        // gains the suffix, which is the one way a well-meaning ticker becomes an unfetchable
        // symbol. Collected here and asserted after the report is written, so a failure leaves the
        // evidence behind rather than only a red test.
        var malformed = planned.Where(p => !WellFormed(p.Symbol)).ToList();

        var collisions = planned
            .GroupBy(p => p.Fingerprint, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        // ---- gate 6: the sealed rule, run against the prices that exist ---------------------------

        var held = await SessionsAsync(context);

        // Which symbols a price request has actually succeeded for. Read from the ledger, because
        // "was it asked for" is a fact about the ledger and not something a coverage rule may
        // assume in either direction.
        var requested = ledger
            .Where(r =>
                r.Outcome == IngestionOutcome.Succeeded &&
                r.Request.Category == DataCategory.MarketPrices &&
                r.Request.Subject.Identifier is not null)
            .Select(r => r.Request.Subject.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var coverage = new List<Coverage>();

        foreach (var member in members)
        {
            var symbol = member.Ready && member.Ticker is not null
                ? AcquisitionPlanning.SymbolFor(member.Ticker)
                : null;

            List<DateOnly> sessions = symbol is not null && held.TryGetValue(symbol, out var dates)
                ? dates
                : [];

            var (from, to) = (member.SpanFrom, member.SpanTo);

            coverage.Add(new Coverage(
                member,
                symbol,
                CoverageEvaluation.Evaluate(
                    member.Ready,
                    sessions,
                    from,
                    to,
                    wasRequested: symbol is not null && requested.Contains(symbol))));
        }

        var faults = coverage.Count(c => c.Verdict.IsFault);
        var pending = coverage.Count(c => c.Verdict.NotYetAcquired);
        var withSeries = coverage.Count(c => c.Verdict.Sessions > 0);

        // ---- gate 4: cadence, over the members whose facts are held --------------------------------

        var cadence = await CadenceAsync(context);

        // ---- the manifest ----------------------------------------------------------------------------

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "universe", "acquisition-dry-run.json"),
            JsonSerializer.Serialize(
                new
                {
                    EvidenceBaseFingerprint = SealedFingerprint,
                    PlannedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    WindowFromUtc = WindowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    WindowToUtc = WindowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Members = members.Count,
                    Acquirable = acquirable.Count,
                    Excluded = excluded.Count,
                    PlannedRequests = planned.Count,
                    WouldFetch = planned.Count(p => !p.AlreadySatisfied),
                    AlreadySatisfied = planned.Count(p => p.AlreadySatisfied),
                    Requests = planned,
                },
                Universe.Json) + "\n");

        var shape = new Shape(
            subjectKind,
            region.Code,
            priorPriceRun is not null,
            ledger.Count,
            satisfied.Count);

        var report = Compose(
            members, acquirable, excluded, planned, coverage, faults, pending, withSeries, cadence,
            malformed, collisions.Count, shape, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "price-acquisition-dry-run.md"),
            report);

        _output.WriteLine(report);

        // ---- planning is not acquiring ------------------------------------------------------------------

        var runsAfter = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();

        Assert.Equal(runsBefore, runsAfter);
        Assert.Equal(observationsBefore, observationsAfter);

        var manifestAfter = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(manifestBefore.SequenceEqual(manifestAfter), "the sealed manifest changed");

        // ---- and only now, the judgements ----------------------------------------------------------

        Assert.Empty(malformed);
        Assert.Empty(collisions);

        Assert.True(
            planned.Count == acquirable.Count * AcquisitionPlanning.RequestsPerMember,
            Universe.Inv($"{planned.Count} requests planned for {acquirable.Count} members"));

        Assert.True(
            acquirable.Select(m => m.Ticker).Distinct(StringComparer.Ordinal).Count() == acquirable.Count,
            "two acquirable members share one ticker, which would price one company as another");
    }

    /// <summary>A symbol this platform can actually normalise: a bare ticker plus the US suffix.</summary>
    private static bool WellFormed(string symbol)
    {
        if (!symbol.EndsWith(AcquisitionPlanning.UsSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var bare = symbol[..^AcquisitionPlanning.UsSuffix.Length];

        return bare.Length is > 0 and <= 5 && bare.All(char.IsAsciiLetterUpper);
    }

    // ---- reading -------------------------------------------------------------------------------------

    private static async Task<List<Member>> MembersAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        // The last cut is not the end of the window. A member still present at the final cohort cut
        // remains a member for the rest of the window, so its series is expected to run to the
        // window's end and not to the cut - reading the cut as the end would under-state every
        // survivor's expectation by a year and quietly excuse a year of missing prices.
        var finalCut = manifest.RootElement
            .GetProperty("CohortCutDates")
            .EnumerateArray()
            .Select(c => c.GetString() ?? string.Empty)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Last();

        var cohorts = manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => m.GetProperty("Cohorts").EnumerateArray()
                    .Select(c => c.GetString() ?? string.Empty)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var windowFrom = DateOnly.FromDateTime(WindowStart);
        var windowTo = DateOnly.FromDateTime(WindowEnd);

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m =>
            {
                var cik = m.GetProperty("Cik").GetString() ?? string.Empty;
                List<string> list = cohorts.TryGetValue(cik, out var c) ? c : [];

                var (spanFrom, spanTo) = MembershipSpan.Derive(
                    [.. list.Select(d => DateOnly.Parse(d, CultureInfo.InvariantCulture))],
                    DateOnly.Parse(finalCut, CultureInfo.InvariantCulture),
                    windowFrom,
                    windowTo);

                return new Member(
                    cik,
                    m.GetProperty("Name").GetString() ?? "(unnamed)",
                    m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null,
                    m.GetProperty("Status").GetString() ?? "unknown",
                    spanFrom,
                    spanTo,
                    list.Count > 0 && string.Equals(list[^1], finalCut, StringComparison.Ordinal));
            })
            .ToList();
    }

    private static async Task<Dictionary<string, List<DateOnly>>> SessionsAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .Select(o => new { o.Subject.Identifier, o.Provenance.AsOfUtc })
            .ToListAsync();

        return rows
            .Where(r => r.Identifier is not null)
            .GroupBy(r => r.Identifier!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => DateOnly.FromDateTime(r.AsOfUtc)).Distinct().ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>Distinct quarterly period ends per company per complete calendar year.</summary>
    /// <remarks>
    /// Selected on <see cref="FinancialFigures.QuarterlyPrefix"/> rather than on a suffix guessed
    /// from the shape of the names. A prefix that matched nothing would report a clean mean over an
    /// empty set, which is the failure this repository has already met once - a gate reading PASS
    /// because its filter matched no rows at all.
    /// </remarks>
    private static async Task<Cadence> CadenceAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"))
            .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.AsOfUtc })
            .ToListAsync();

        var byCompanyYear = rows
            .Where(r => r.AsOfUtc.Year < DateTime.UtcNow.Year)
            .GroupBy(r => (r.Cik, r.AsOfUtc.Year))
            .Select(g => g.Select(r => r.AsOfUtc.Date).Distinct().Count())
            .ToList();

        return new Cadence(
            rows.Select(r => r.Cik).Distinct(StringComparer.Ordinal).Count(),
            byCompanyYear.Count,
            byCompanyYear.Count == 0 ? 0m : (decimal)byCompanyYear.Sum() / byCompanyYear.Count);
    }

    // ---- reporting ------------------------------------------------------------------------------------

    private static string Compose(
        List<Member> members,
        List<Member> acquirable,
        List<Member> excluded,
        List<Planned> planned,
        List<Coverage> coverage,
        int faults,
        int pending,
        int withSeries,
        Cadence cadence,
        List<Planned> malformed,
        int collisions,
        Shape shape,
        Stopwatch watch)
    {
        var wouldFetch = planned.Count(p => !p.AlreadySatisfied);
        var report = new StringBuilder();

        report.AppendLine("# Price acquisition - dry run");
        report.AppendLine();
        report.AppendLine("**No provider was called.** Every request below was built exactly as the");
        report.AppendLine("acquisition would build it, its fingerprint computed, and then discarded.");
        report.AppendLine("Ingestion-run and observation counts are asserted unchanged either side,");
        report.AppendLine("and the sealed manifest is byte-identical. **Nothing was acquired.**");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Sealed universe `{SealedFingerprint}`."));
        report.AppendLine();

        report.AppendLine("## The acquisition set");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Members | {members.Count} |"));
        report.AppendLine(Universe.Inv($"| **Acquisition-ready** | **{acquirable.Count}** |"));
        report.AppendLine(Universe.Inv($"| Excluded, and still members | {excluded.Count} |"));
        report.AppendLine(Universe.Inv($"| Distinct symbols | {acquirable.Select(m => m.Ticker).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine();

        var panelDropouts = acquirable.Count(m => !m.SurvivesToWindowEnd);
        var universeDropouts = members.Count(m => !m.SurvivesToWindowEnd);

        report.AppendLine("### Gate 2, on the set that would actually be priced");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Dropouts in the sealed universe | {universeDropouts} of {members.Count} ({AcquisitionPlanning.DropoutShare(universeDropouts, members.Count):P2}) |"));
        report.AppendLine(Universe.Inv($"| Dropouts in the acquisition set | {panelDropouts} of {acquirable.Count} ({AcquisitionPlanning.DropoutShare(panelDropouts, acquirable.Count):P2}) |"));
        report.AppendLine(Universe.Inv($"| Pre-registered floor | {AcquisitionPlanning.SurvivorshipFloor:P2}, unmoved |"));
        report.AppendLine(Universe.Inv($"| Panel verdict | {(AcquisitionPlanning.ClearsSurvivorshipFloor(panelDropouts, acquirable.Count) ? "**PASSES**" : "**FAILS**")} |"));
        report.AppendLine();
        report.AppendLine("This is the measure that matters, and it is not the manifest's. A universe");
        report.AppendLine("can be survivorship-free and still yield a survivorship-biased panel if the");
        report.AppendLine("members that died are exactly the ones whose symbols could not be");
        report.AppendLine("established. That was the case before the identity recovery, and it is the");
        report.AppendLine("reason the recovery was run.");
        report.AppendLine();

        report.AppendLine("## What would be requested");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Window | {AcquisitionPlanning.WindowFrom} to {AcquisitionPlanning.WindowTo} |"));
        report.AppendLine(Universe.Inv($"| Price requests (`eodhd-eod`) | {planned.Count(p => p.Source == PriceSource)} |"));
        report.AppendLine(Universe.Inv($"| Corporate-action requests (`eodhd-splits`) | {planned.Count(p => p.Source == ActionsSource)} |"));
        report.AppendLine(Universe.Inv($"| **Total billable requests** | **{planned.Count}** |"));
        report.AppendLine(Universe.Inv($"| Already satisfied by the ledger | {planned.Count - wouldFetch} |"));
        report.AppendLine(Universe.Inv($"| **Would actually fetch** | **{wouldFetch}** |"));
        report.AppendLine(Universe.Inv($"| Distinct fingerprints | {planned.Select(p => p.Fingerprint).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine();
        report.AppendLine("### The shape of one request");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        report.AppendLine(Universe.Inv($"| Subject kind | `{shape.SubjectKind}` |"));
        report.AppendLine(Universe.Inv($"| Region | `{shape.Region}` |"));
        report.AppendLine(Universe.Inv($"| Taken from | {(shape.FromLedger ? "a price run already in the ledger" : "**a default** - the ledger holds no price run to take it from")} |"));
        report.AppendLine(Universe.Inv($"| Runs in the ledger | {shape.LedgerRuns}, of which {shape.SatisfiedFingerprints} succeeded |"));
        report.AppendLine();
        report.AppendLine("Subject kind and region are both inside the fingerprint. They are read from");
        report.AppendLine("the ledger rather than assumed, because a kind guessed differently from the");
        report.AppendLine("one the acquisition uses would make every suppression check miss - and a");
        report.AppendLine("check that can only answer \"nothing is satisfied\" is worse than no check,");
        report.AppendLine("because it reads like one.");
        report.AppendLine();

        report.AppendLine(Universe.Inv(
            $"Fingerprint collisions: {collisions}. A collision would make two members one request, and the second would be suppressed as a duplicate of the first - one company priced as another, silently."));
        report.AppendLine();

        report.AppendLine("### Symbol construction");
        report.AppendLine();
        report.AppendLine("Only exchange `US` has a configured session. A symbol that does not end");
        report.AppendLine("`.US` normalises into an unstated-session quarantine rather than into");
        report.AppendLine("observations, and a ticker that already carries a dot - a class marker");
        report.AppendLine("such as `BRK.B` - keeps its own and never gains the suffix.");
        report.AppendLine();

        if (malformed.Count == 0)
        {
            report.AppendLine(Universe.Inv(
                $"All {planned.Count} planned symbols are a bare ticker of one to five letters plus `.US`. None would quarantine on its symbol alone."));
        }
        else
        {
            report.AppendLine(Universe.Inv($"**{malformed.Count} planned symbols are not fetchable as built.** Acquisition must not start until each is resolved."));
            report.AppendLine();
            report.AppendLine("| CIK | Name | Symbol |");
            report.AppendLine("| --- | --- | --- |");

            foreach (var row in malformed.DistinctBy(p => p.Symbol, StringComparer.Ordinal))
            {
                report.AppendLine(Universe.Inv($"| `{row.Cik}` | {row.Name} | `{row.Symbol}` |"));
            }
        }

        report.AppendLine();

        report.AppendLine("## Gate 6 - the sealed coverage rule, run against what exists");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Acquirable members with a price series today | {withSeries} |"));
        report.AppendLine(Universe.Inv($"| Acquirable members with none | {acquirable.Count - withSeries} |"));
        report.AppendLine(Universe.Inv($"| **Faults** | **{faults}** |"));
        report.AppendLine(Universe.Inv($"| **Not yet acquired** | **{pending}** |"));
        report.AppendLine(Universe.Inv($"| Gate 6, acquisition not complete | {(CoverageEvaluation.Passes(faults, pending, acquisitionComplete: false) ? "**PASS**" : "**FAIL**")} |"));
        report.AppendLine(Universe.Inv($"| Gate 6, were acquisition declared complete now | {(CoverageEvaluation.Passes(faults, pending, acquisitionComplete: true) ? "**PASS**" : "**FAIL** - the safeguard, refusing to certify coverage nobody looked for")} |"));
        report.AppendLine();
        // Counted, not asserted. Saying "every fault is no-series" without counting is how a
        // second kind of fault hides inside a number nobody broke down.
        var noSeries = coverage.Count(c => c.Verdict.Faults.Contains(CoverageEvaluation.NoSeries));

        var interior = coverage
            .Where(c => c.Verdict.Faults.Contains(CoverageEvaluation.InteriorGap))
            .ToList();

        var discontinuous = coverage.Count(
            c => c.Verdict.Faults.Contains(CoverageEvaluation.UnexplainedDiscontinuity));

        report.AppendLine("| Fault | Members |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.NoSeries}` | {noSeries} |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.InteriorGap}` | {interior.Count} |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.UnexplainedDiscontinuity}` | {discontinuous} |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.NotYetAcquired}` (not a fault) | {pending} |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"{pending} acquirable members hold no closing price and no price request has ever succeeded for them, so they are pending rather than faulty. {noSeries} were requested successfully and still hold nothing - those are real holes, and that is the number that must be zero."));
        report.AppendLine();
        report.AppendLine("The pending count is not an excuse that outlives its reason. The moment an");
        report.AppendLine("acquisition stage declares itself complete, a member still carrying it was");
        report.AppendLine("skipped rather than pending, and the gate fails on it - which is what the");
        report.AppendLine("second verdict above reports.");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Of the {withSeries} members that do hold prices, {interior.Count} carry an interior gap longer than the {CoverageEvaluation.InteriorGapTolerance} sessions a holiday run explains. The rule was exercised against real series rather than only against examples."));
        report.AppendLine();

        report.AppendLine("## Gate 4 - quarterly cadence");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Companies with quarterly facts | {cadence.Companies} |"));
        report.AppendLine(Universe.Inv($"| Company-years measured | {cadence.CompanyYears} |"));
        report.AppendLine(Universe.Inv($"| Mean distinct quarterly periods | {cadence.Mean:F2} against the declared {QuarterlyFloor:F1} |"));
        report.AppendLine(Universe.Inv($"| Verdict on the evidence held | {Cadence4(cadence)} |"));
        report.AppendLine();

        if (cadence.CompanyYears == 0)
        {
            report.AppendLine(
                "**No quarterly observation is held**, so this gate has nothing to measure and must");
            report.AppendLine(
                "not be reported as passing. An empty set satisfies every average, which is the one");
            report.AppendLine("way a coverage gate lies.");
        }
        else
        {
            report.AppendLine(Universe.Inv(
                $"This is prices-independent: it measures filings, not market data, so acquisition cannot change it, and closing it now costs nothing and defers nothing. It is measured over every company whose facts are held and says nothing about the {members.Count - cadence.Companies} whose fundamentals were never fetched - a separate acquisition nobody has authorised."));
        }

        report.AppendLine();

        report.AppendLine("## Gate 9 - idempotency");
        report.AppendLine();
        report.AppendLine("Two halves. The first is provable now and is proved above: a request built");
        report.AppendLine("twice from the same source, category, region, subject and window yields the");
        report.AppendLine("same fingerprint, and a window one day shorter yields a different one. A");
        report.AppendLine("second identical acquisition is therefore suppressed by the ledger rather");
        report.AppendLine("than re-fetched.");
        report.AppendLine();
        report.AppendLine("The second half - that a real second run changes no store - cannot be shown");
        report.AppendLine("until a first run exists. It stays deferred, and it is the check to run");
        report.AppendLine("immediately after acquisition rather than a box to tick now.");
        report.AppendLine();

        report.AppendLine("## The excluded, still members");
        report.AppendLine();
        report.AppendLine("| Reason | Members |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in excluded
            .GroupBy(m => m.Status, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| `{group.Key}` | {group.Count()} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"All {excluded.Count} remain in the sealed four hundred. None is sent to a provider."));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"The full request list is at `artifacts/universe/acquisition-dry-run.json`. Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        return report.ToString();
    }

    private sealed record Member(
        string Cik,
        string Name,
        bool Ready,
        string? Ticker,
        string Status,
        DateOnly SpanFrom,
        DateOnly SpanTo,
        bool SurvivesToWindowEnd);

    private sealed record Planned(
        string Cik,
        string Name,
        string Symbol,
        string Source,
        string Category,
        string Fingerprint,
        bool AlreadySatisfied);

    private sealed record Coverage(
        Member Member,
        string? Symbol,
        CoverageVerdict Verdict);

    private sealed record Cadence(int Companies, int CompanyYears, decimal Mean);

    private sealed record Shape(
        string SubjectKind,
        string Region,
        bool FromLedger,
        int LedgerRuns,
        int SatisfiedFingerprints);

    /// <summary>
    /// The manifest's own quarterly floor: three 10-Qs a year, less a little tolerance.
    /// </summary>
    private const decimal QuarterlyFloor = 2.9m;

    /// <summary>Gate 4's verdict, which an empty set is never allowed to satisfy.</summary>
    private static string Cadence4(Cadence cadence) =>
        cadence.CompanyYears == 0
            ? "**NOT MEASURABLE** - no quarterly facts are held"
            : cadence.Mean >= QuarterlyFloor ? "**PASSES**" : "**FAILS**";
}
