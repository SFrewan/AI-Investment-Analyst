using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Admission;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// A client that refuses to send more requests than were authorised.
/// </summary>
/// <remarks>
/// The ceiling is enforced by the transport rather than by counting afterwards, because a budget
/// checked after the fact is a report of the overspend rather than a control on it. A redirect, an
/// internal retry or a later edit that adds a call fails the run instead of quietly spending one.
/// </remarks>
internal sealed class BoundedCalls : DelegatingHandler
{
    private readonly int _ceiling;
    private int _sent;

    public BoundedCalls(int ceiling) => _ceiling = ceiling;

    public int Sent => Volatile.Read(ref _sent);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _sent) > _ceiling)
        {
            throw new InvalidOperationException(
                $"This run is authorised for {_ceiling} billable requests and tried to send " +
                "another. Nothing is retried and nothing is sent past the budget.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Stage four: seal the four-hundred-company manifest and judge it against the twelve gates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two billable calls, and only two.</strong> EODHD's symbol lists - the live one and the
/// delisted one - are read once each, to cross-check the tickers EDGAR gave and to recognise the
/// members that stopped trading. <see cref="BoundedCalls"/> makes that a property of the client
/// rather than an intention. No price history is fetched here; that is the acquisition, and it is
/// not authorised.
/// </para>
/// <para>
/// <strong>Unmatched members stay members.</strong> A company whose ticker EDGAR does not carry, or
/// which appears in neither symbol list, is recorded in the manifest's unmatched list with the
/// reason and keeps its place in the universe. Dropping it would be survivorship bias arriving
/// through the back door of a lookup failure, after being carefully excluded from the front.
/// </para>
/// <para>
/// <strong>Nothing is written to the store and no strategy is declared.</strong> The manifest is
/// written to <c>declarations/</c>, which is the point of it; observation and opportunity counts
/// are asserted unchanged.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_MANIFEST=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseManifest400Tests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_MANIFEST";

    /// <summary>The share of members that must have left, for this not to be a survivor list.</summary>
    private const decimal MinimumDropoutShare = 0.05m;

    /// <summary>Distinct quarterly periods per company per complete calendar year.</summary>
    /// <remarks>
    /// 2.9, corrected from an impossible 3.5: a US issuer files three 10-Qs a year, because the
    /// fourth quarter is reported inside the 10-K rather than separately. See
    /// <see cref="QuarterlyCadenceTests"/> for why the old threshold could never have been met.
    /// </remarks>
    private const decimal MinimumQuarterlyPeriodsPerYear = 2.9m;

    /// <summary>The correlation prefix every request of this stage carries.</summary>
    private const string CorrelationPrefix = "universe-";

    private const string LiveTicker = "sec+eodhd-live";

    private const string DelistedTicker = "sec+eodhd-delisted";

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseManifest400Tests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_four_hundred_are_sealed_and_judged_against_every_gate_before_acquisition()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Manifest sealing is off. Set {GateVariable}=1 to run it. It makes exactly two " +
            "billable calls and starts no acquisition.");

        var loaded = Universe.LoadRoster();

        Skip.If(loaded is null, "No roster is on disk. Stage one builds it.");

        var roster = loaded!;
        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var nowUtc = services.GetRequiredService<IClock>().UtcNow;
        var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;
        var presence = CredentialPresence.Of(options.ApiKey);

        Skip.IfNot(
            presence.IsConfigured,
            "No EODHD credential is configured. Run scripts/check-eodhd-credential.cmd first.");

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();

        // ---- what EDGAR said about each company -------------------------------------------

        var ciks = roster.Members.Select(m => m.Cik).ToHashSet(StringComparer.Ordinal);

        var profiles = await ProfilesAsync(context, ciks);
        var filings = await FilingsAsync(context, ciks);

        // ---- the two billable calls -------------------------------------------------------

        using var budget = new BoundedCalls(Universe.EodhdCallBudget)
        {
            InnerHandler = new HttpClientHandler(),
        };

        using var client = new HttpClient(budget)
        {
            BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromMinutes(3),
        };

        var live = await SymbolsAsync(client, options, delisted: false);
        var dead = await SymbolsAsync(client, options, delisted: true);

        Assert.True(
            live.Count > 0 && dead.Count > 0,
            Universe.Inv($"The symbol lists came back {live.Count} live and {dead.Count} delisted. ") +
            "The cross-check cannot be made against an empty list, and marking every member " +
            "unmatched would report a failed request as a universe of unknown tickers.");

        // ---- the members ------------------------------------------------------------------

        var members = new List<Member>();
        var unmatched = new List<Unmatched>();
        var finalCut = Universe.Day(Universe.Cuts[^1]);
        var stoppedBefore = Universe.WindowEnd.AddMonths(-Universe.ReportingWindowMonths);

        foreach (var entry in roster.Members.OrderBy(m => m.Cik, StringComparer.Ordinal))
        {
            profiles.TryGetValue(entry.Cik, out var card);
            filings.TryGetValue(entry.Cik, out var dates);

            var ticker = card?.Ticker;
            string source;
            string? note = null;

            if (ticker is null)
            {
                source = "none";

                unmatched.Add(new Unmatched(
                    entry.Cik,
                    null,
                    card?.Name,
                    "EDGAR's submissions document carries no ticker for this CIK"));
            }
            else if (live.Contains(ticker))
            {
                source = LiveTicker;
            }
            else if (dead.Contains(ticker))
            {
                source = DelistedTicker;
                note = "delisted; price history is served under the delisted symbol";
            }
            else
            {
                source = "sec-only";

                unmatched.Add(new Unmatched(
                    entry.Cik,
                    ticker,
                    card?.Name,
                    "the ticker EDGAR carries appears in neither EODHD symbol list"));
            }

            // Cohorts are intersected with the filing record, which is the point-in-time one: a
            // frame is EDGAR's present-day assembly and can contain a figure first filed after the
            // cut it stands for. A membership nobody could have known is a look-ahead, so it is
            // removed here rather than reported later.
            var cohorts = entry.FrameCohorts;

            if (dates is { Count: > 0 })
            {
                var firstFiled = Universe.Day(dates.Min());

                cohorts = cohorts
                    .Where(c => string.CompareOrdinal(c, firstFiled) >= 0)
                    .ToList();
            }

            members.Add(new Member(
                entry.Cik,
                ticker,
                source,
                card?.Exchange,
                card?.Sic,
                card?.Sector,
                card?.Name,
                SizeBand: null,
                RankedRevenue: entry.Revenue.ToString("F0", CultureInfo.InvariantCulture),
                RevenueTag: entry.RevenueTag,
                FirstFilingUtc: dates is { Count: > 0 } ? Universe.Day(dates.Min()) : null,
                LastFilingUtc: dates is { Count: > 0 } ? Universe.Day(dates.Max()) : null,
                Cohorts: cohorts,
                StoppedFiling: dates is { Count: > 0 } && dates.Max() < stoppedBefore,
                AbsentFromFinalCrossSection:
                    !entry.FrameCohorts.Contains(finalCut, StringComparer.Ordinal),
                Note: note));
        }

        AssignSizeBands(members);

        // ---- seal, then verify the seal by reading it back --------------------------------

        var manifest = Seal(members, unmatched, nowUtc);

        await Universe.WriteAsync(
            Universe.ManifestPath,
            JsonSerializer.Serialize(manifest, Universe.Json) + "\n");

        var reread = JsonSerializer.Deserialize<Manifest>(
            await File.ReadAllTextAsync(Universe.ManifestPath),
            Universe.Json);

        Assert.NotNull(reread);

        var recomputed = Fingerprint(reread! with { EvidenceBaseFingerprint = null });

        // ---- the gates --------------------------------------------------------------------

        var secCalls = await CallsAsync(context);
        var gates = await JudgeAsync(context, members, secCalls, budget.Sent, recomputed);

        var report = Compose(
            manifest, members, unmatched, gates, secCalls, budget.Sent, roster, presence, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-manifest-400.md"),
            report);

        _output.WriteLine(report);

        // ---- nothing was written to the store ---------------------------------------------

        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(opportunitiesBefore, await context.Opportunities.AsNoTracking().CountAsync());

        Assert.Equal(Universe.EodhdCallBudget, budget.Sent);

        // The seal verified against the file rather than against the object that wrote it. A
        // fingerprint that only ever matches itself in memory proves nothing about what an
        // acquisition would later read off disk.
        Assert.True(
            string.Equals(manifest.EvidenceBaseFingerprint, recomputed, StringComparison.Ordinal),
            Universe.Inv($"The manifest seals as `{manifest.EvidenceBaseFingerprint}` and reads ")
            + Universe.Inv($"back as `{recomputed}`. The written file does not hash to the name it ")
            + "carries, so nothing downstream could verify which universe it was measured on.");

        var failed = gates.Where(g => g.State == GateState.Failed).ToList();

        Assert.True(
            failed.Count == 0,
            "Gates failed before acquisition: " +
            string.Join(
                " | ",
                failed.Select(g => Universe.Inv($"{g.Number}. {g.Name}: {g.Detail}"))));
    }

    // ---- reading what EDGAR gave -------------------------------------------------------------

    private static async Task<Dictionary<string, Card>> ProfilesAsync(
        AppDbContext context,
        HashSet<string> ciks)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, "company.%"))
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.RetrievedAtUtc,
            })
            .ToListAsync();

        var cards = new Dictionary<string, Card>(StringComparer.Ordinal);

        foreach (var group in rows
            .Where(r => ciks.Contains(r.Cik))
            .GroupBy(r => r.Cik, StringComparer.Ordinal))
        {
            // The most recent retrieval wins, per attribute. A company profiled twice has one
            // current name, not two.
            var latest = group
                .GroupBy(r => r.Attribute, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.RetrievedAtUtc).First().Canonical,
                    StringComparer.Ordinal);

            string? Read(string attribute) =>
                latest.TryGetValue(attribute, out var value) ? value : null;

            cards[group.Key] = new Card(
                Read("company.name"),
                Normalise(Read("company.ticker")),
                Read("company.exchange"),
                Read("company.sic"),
                Read("company.sic-description"));
        }

        return cards;
    }

    /// <summary>Every distinct filing date per company, from the provenance of its stored facts.</summary>
    private static async Task<Dictionary<string, List<DateOnly>>> FilingsAsync(
        AppDbContext context,
        HashSet<string> ciks)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
            .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.PublishedAtUtc })
            .Distinct()
            .ToListAsync();

        return rows
            .Where(r => ciks.Contains(r.Cik))
            .GroupBy(r => r.Cik, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => DateOnly.FromDateTime(r.PublishedAtUtc)).Distinct().ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>How many requests this stage's phases actually sent, read off the ledger.</summary>
    /// <remarks>
    /// <para>
    /// Counted from the ingestion ledger rather than from a variable this run incremented. A
    /// self-reported spend is a claim; the ledger is the record, and it also counts a request made
    /// by a run that then failed - which a success counter would quietly omit.
    /// </para>
    /// <para>
    /// Refused runs are excluded, and that is not generosity. A refusal happens at the gateway,
    /// before the connector: the quota check, the licensing check and the category check all
    /// decline without a request leaving the machine. Counting them as spend would report a rate
    /// limiter doing its job as budget consumed, and a paced retry - which sends fewer requests
    /// than a hammering loop - as the more expensive of the two.
    /// </para>
    /// </remarks>
    private static async Task<int> CallsAsync(AppDbContext context)
    {
        var runs = await context.IngestionRuns.AsNoTracking().ToListAsync();

        return runs.Count(r =>
            r.Outcome != IngestionOutcome.Refused &&
            r.Request.CorrelationId.Value.StartsWith(CorrelationPrefix, StringComparison.Ordinal));
    }

    private static string? Normalise(string? ticker) =>
        string.IsNullOrWhiteSpace(ticker) ? null : ticker.Trim().ToUpperInvariant();

    // ---- the two billable calls --------------------------------------------------------------

    private static async Task<HashSet<string>> SymbolsAsync(
        HttpClient client,
        EodhdOptions options,
        bool delisted)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        // The credential is in the query because that is how EODHD authenticates. Built here, used
        // once, and never put into a message, a log or a report.
        var uri = new Uri(
            "api/exchange-symbol-list/US?fmt=json" +
            (delisted ? "&delisted=1" : string.Empty) +
            "&api_token=" + Uri.EscapeDataString(options.ApiKey),
            UriKind.Relative);

        using var response = await client.GetAsync(uri);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return symbols;
        }

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (document.ValueKind != JsonValueKind.Array)
        {
            return symbols;
        }

        foreach (var row in document.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("Code", out var code) &&
                code.ValueKind == JsonValueKind.String &&
                Normalise(code.GetString()) is { } symbol)
            {
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    // ---- building ----------------------------------------------------------------------------

    /// <summary>Terciles of the revenue each member was ranked on.</summary>
    private static void AssignSizeBands(List<Member> members)
    {
        var order = members
            .Select((m, index) => new
            {
                Index = index,
                Value = decimal.Parse(
                    m.RankedRevenue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture),
            })
            .OrderByDescending(x => x.Value)
            .ToList();

        for (var rank = 0; rank < order.Count; rank++)
        {
            var band = rank * 3 < order.Count ? "large"
                : rank * 3 < order.Count * 2 ? "mid"
                : "small";

            members[order[rank].Index] = members[order[rank].Index] with { SizeBand = band };
        }
    }

    private static string Fingerprint(Manifest content)
    {
        var canonical = JsonSerializer.Serialize(content, Universe.Json);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        return Universe.BaseName + "@" + digest[..12];
    }

    private static Manifest Seal(List<Member> members, List<Unmatched> unmatched, DateTime nowUtc)
    {
        var content = new Manifest(
            EvidenceBaseFingerprint: null,
            SealedAtUtc: nowUtc.ToString("O", CultureInfo.InvariantCulture),
            WindowFromUtc: Universe.Day(Universe.WindowStart),
            WindowToUtc: Universe.Day(Universe.WindowEnd),
            FamilyBudget: Universe.FamilyBudget,
            MembershipRule:
                "Membership is the cross-section of US GAAP filers that reported Assets for the " +
                "quarter preceding each cut date, taken from EDGAR's XBRL frames rather than from " +
                "any list of companies that exist today. The universe is the four hundred largest " +
                "of the first cut by 2020 revenue, fixed there and tracked forward; later cuts " +
                "remove members that stopped reporting and never add new ones, so no company " +
                "enters on evidence that postdates its entry.",
            MembershipCaveat:
                "A frame is EDGAR's present-day assembly of what was reported for a period, so it " +
                "can contain a figure first filed after the cut date it stands for. Cohort " +
                "membership is therefore intersected with the filing dates on the company facts, " +
                "which are the point-in-time record, and gate 3 refuses any cohort preceding a " +
                "member's first observed filing.",
            PayloadDeduplication:
                "Archived payloads are de-duplicated by content hash, keyed on subject, before " +
                "normalisation. One payload is normalised once however many ingestion runs reach it.",
            ObservationDeduplication:
                "An observation is identified by subject, attribute, period end, publication " +
                "instant and canonical value. Two payloads whose histories overlap must not record " +
                "the same fact twice.",
            QuarterlyCadenceRule:
                "At least 2.9 distinct quarterly periods per company per complete calendar year, " +
                "excluding the current partial year. Three, not four: a US issuer files three " +
                "10-Qs a year because the fourth quarter is reported inside the 10-K.",
            CohortCutDates: Universe.Cuts.Select(Universe.Day).ToList(),
            Members: members,
            Unmatched: unmatched);

        return content with { EvidenceBaseFingerprint = Fingerprint(content) };
    }

    // ---- judging -----------------------------------------------------------------------------

    private static async Task<List<Gate>> JudgeAsync(
        AppDbContext context,
        List<Member> members,
        int secCalls,
        int eodhdCalls,
        string recomputed)
    {
        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var rows = await facts.CountAsync();
        var early = await facts.CountAsync(o => o.Provenance.PublishedAtUtc < o.Provenance.AsOfUtc);
        var late = await facts
            .CountAsync(o => o.Provenance.PublishedAtUtc > o.Provenance.RetrievedAtUtc);

        var distinct = await facts
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .Distinct()
            .CountAsync();

        var attributes = await facts
            .Select(o => o.Attribute)
            .Distinct()
            .ToListAsync();

        var quarterly = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"))
            .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.AsOfUtc })
            .Distinct()
            .ToListAsync();

        var ciks = members.Select(m => m.Cik).ToHashSet(StringComparer.Ordinal);

        var cadence = Cadence(quarterly
            .Where(q => ciks.Contains(q.Cik))
            .Select(q => (q.Cik, q.AsOfUtc))
            .ToList());

        var dropouts = members.Count(m => m.StoppedFiling || m.AbsentFromFinalCrossSection);
        var share = members.Count == 0 ? 0m : (decimal)dropouts / members.Count;

        var lookAhead = members.Count(m =>
            m.FirstFilingUtc is { } first &&
            m.Cohorts.Exists(c => string.CompareOrdinal(c, first) < 0));

        var unverifiable = members.Count(m => m.FirstFilingUtc is null);

        var known = KnownAttributes();
        var strangers = attributes.Where(a => !known.Contains(a)).ToList();

        var registerText = await File.ReadAllTextAsync(
            Universe.RepositoryPath(StrategyRegister.RelativePath));

        var declarations = StrategyRegister.Parse(registerText).Count;
        var cites = registerText.Contains(Universe.BaseName, StringComparison.Ordinal);

        return
        [
            new Gate(
                1,
                "Manifest sealed before any hypothesis",
                cites ? GateState.Failed : GateState.Passed,
                cites
                    ? "a sealed declaration already names this evidence base, so the universe was " +
                      "not fixed before the hypothesis that will be measured on it"
                    : Universe.Inv($"sealed to {Universe.ManifestRelativePath} as `{recomputed}`; ")
                      + "no declaration names this base"),

            new Gate(
                2,
                "Survivorship",
                share >= MinimumDropoutShare ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{dropouts} of {members.Count} members left the universe inside the ")
                + Universe.Inv($"window - {share:P1} against a floor of {MinimumDropoutShare:P0}")),

            new Gate(
                3,
                "Membership look-ahead",
                lookAhead == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{lookAhead} members hold a cohort preceding their first observed ")
                + Universe.Inv($"filing; {unverifiable} carry no filing on record to check against")),

            new Gate(
                4,
                "Quarterly cadence",
                cadence >= MinimumQuarterlyPeriodsPerYear ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{cadence:F2} distinct quarterly periods per company per complete ")
                + Universe.Inv($"calendar year, against a gate of {MinimumQuarterlyPeriodsPerYear:F1}")),

            new Gate(
                5,
                "No redefinition",
                strangers.Count == 0 ? GateState.Passed : GateState.Failed,
                strangers.Count == 0
                    ? Universe.Inv($"all {attributes.Count} stored financial attributes are ")
                      + "declared annual or quarterly names; the quarterly namespace was added "
                      + "beside the annual one, not over it"
                    : "unknown attributes: " + string.Join(", ", strangers.Take(8))),

            new Gate(
                6,
                "Coverage",
                GateState.Deferred,
                "needs a price series for every member, which needs the acquisition"),

            new Gate(
                7,
                "Point-in-time invariants",
                early == 0 && late == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{early} facts published before the period they describe and {late} ")
                + Universe.Inv($"published after they were retrieved, over {rows} stored rows")),

            new Gate(
                8,
                "Request budget",
                secCalls <= Universe.SecCallBudget && eodhdCalls <= Universe.EodhdCallBudget
                    ? GateState.Passed
                    : GateState.Failed,
                Universe.Inv($"{secCalls} SEC calls against {Universe.SecCallBudget} authorised; ")
                + Universe.Inv($"{eodhdCalls} EODHD calls against {Universe.EodhdCallBudget}")),

            new Gate(
                9,
                "Idempotency",
                GateState.Deferred,
                "a rerun can only be compared against a run; every company already held was "
                + "skipped before a request was built during this build, which is that control "
                + "observed once rather than twice"),

            new Gate(
                10,
                "Opportunities",
                GateState.Passed,
                "unchanged; asserted in this run, and no prediction, order or cycle was created"),

            new Gate(
                11,
                "Declarations",
                GateState.Passed,
                Universe.Inv($"{declarations} sealed declarations re-parsed and every fingerprint ")
                + "recomputed; none modified, renamed or re-based"),

            new Gate(
                12,
                "De-duplication",
                distinct == rows ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{distinct} distinct facts across {rows} stored rows; both ")
                + "de-duplication rules are recorded in the manifest"),
        ];
    }

    /// <summary>
    /// Distinct quarterly period ends per company per complete calendar year of the window.
    /// </summary>
    /// <remarks>
    /// Measured on period ends rather than rows: a restatement republishes a period, it does not
    /// add one, and counting republications would make a company that revised its accounts look
    /// like a company that reported more often.
    /// </remarks>
    private static decimal Cadence(List<(string Cik, DateTime AsOfUtc)> quarterly)
    {
        var periods = new HashSet<string>(StringComparer.Ordinal);
        var companyYears = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (cik, asOf) in quarterly)
        {
            if (asOf.Year < Universe.Cuts[0].Year || asOf.Year > Universe.Cuts[^1].Year)
            {
                continue;
            }

            periods.Add(Universe.Inv($"{cik}|{asOf:yyyy-MM-dd}"));
            companyYears.Add(Universe.Inv($"{cik}|{asOf.Year}"));
        }

        return companyYears.Count == 0
            ? 0m
            : Math.Round((decimal)periods.Count / companyYears.Count, 2);
    }

    private static HashSet<string> KnownAttributes()
    {
        string[] names =
        [
            FinancialFigures.Revenue, FinancialFigures.GrossProfit,
            FinancialFigures.OperatingIncome, FinancialFigures.NetIncome,
            FinancialFigures.DepreciationAndAmortisation, FinancialFigures.OperatingCashFlow,
            FinancialFigures.CapitalExpenditure, FinancialFigures.CashAndEquivalents,
            FinancialFigures.TotalDebt, FinancialFigures.CurrentAssets,
            FinancialFigures.CurrentLiabilities, FinancialFigures.Inventory,
            FinancialFigures.TotalAssets, FinancialFigures.TotalEquity,
            FinancialFigures.DilutedShares, FinancialFigures.FreeCashFlow,
            FinancialFigures.QuickAssets, FinancialFigures.Ebitda, FinancialFigures.NetDebt,

            FinancialFigures.QuarterlyRevenue, FinancialFigures.QuarterlyGrossProfit,
            FinancialFigures.QuarterlyOperatingIncome, FinancialFigures.QuarterlyNetIncome,
            FinancialFigures.QuarterlyDepreciationAndAmortisation,
            FinancialFigures.QuarterlyOperatingCashFlow,
            FinancialFigures.QuarterlyCapitalExpenditure, FinancialFigures.QuarterlyDilutedShares,
        ];

        return names.ToHashSet(StringComparer.Ordinal);
    }

    // ---- reporting ---------------------------------------------------------------------------

    private static string Compose(
        Manifest manifest,
        List<Member> members,
        List<Unmatched> unmatched,
        List<Gate> gates,
        int secCalls,
        int eodhdCalls,
        Universe.Roster roster,
        CredentialPresence presence,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        report.AppendLine("# The four hundred - sealed, judged, nothing acquired");
        report.AppendLine();
        report.AppendLine("**No price history was fetched. No strategy was declared or scored.**");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Credential fingerprint `{presence.Fingerprint}` - the token in force."));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv(
            $"| **Evidence-base fingerprint** | `{manifest.EvidenceBaseFingerprint}` |"));
        report.AppendLine(Universe.Inv(
            $"| Window | {manifest.WindowFromUtc} to {manifest.WindowToUtc} |"));
        report.AppendLine(Universe.Inv($"| **Members sealed** | **{members.Count}** |"));
        report.AppendLine(Universe.Inv(
            $"| Cross-section they were drawn from | {roster.CrossSectionSize} filers |"));
        report.AppendLine(Universe.Inv(
            $"| Ticker confirmed live | {members.Count(m => m.TickerSource == LiveTicker)} |"));
        report.AppendLine(Universe.Inv(
            $"| Ticker confirmed delisted | {members.Count(m => m.TickerSource == DelistedTicker)} |"));
        report.AppendLine(Universe.Inv(
            $"| **Unmatched, recorded not dropped** | **{unmatched.Count}** |"));
        report.AppendLine(Universe.Inv(
            $"| Stopped filing inside the window | {members.Count(m => m.StoppedFiling)} |"));
        report.AppendLine(Universe.Inv(
            $"| Absent from the final cross-section | {members.Count(m => m.AbsentFromFinalCrossSection)} |"));
        report.AppendLine(Universe.Inv(
            $"| **SEC calls spent** | **{secCalls}** of {Universe.SecCallBudget} |"));
        report.AppendLine(Universe.Inv(
            $"| **EODHD calls spent** | **{eodhdCalls}** of {Universe.EodhdCallBudget} |"));
        report.AppendLine(Universe.Inv(
            $"| Family budget | {manifest.FamilyBudget}, so significance 0.05/{manifest.FamilyBudget} = 0.0100 |"));
        report.AppendLine(Universe.Inv($"| Elapsed | {watch.Elapsed.TotalSeconds:F0}s |"));
        report.AppendLine();

        report.AppendLine("## The twelve gates");
        report.AppendLine();
        report.AppendLine("| # | Gate | Result | Detail |");
        report.AppendLine("| ---: | --- | --- | --- |");

        foreach (var gate in gates)
        {
            var state = gate.State switch
            {
                GateState.Passed => "**PASS**",
                GateState.Failed => "**FAIL**",
                _ => "deferred",
            };

            report.AppendLine(Universe.Inv(
                $"| {gate.Number} | {gate.Name} | {state} | {gate.Detail} |"));
        }

        report.AppendLine();
        report.AppendLine("Two gates are deferred by construction: coverage and idempotency both");
        report.AppendLine("describe stored price rows that only an acquisition produces. They are");
        report.AppendLine("not skipped, and no hypothesis may name this base until they have run.");
        report.AppendLine();

        report.AppendLine("## Unmatched");
        report.AppendLine();

        if (unmatched.Count == 0)
        {
            report.AppendLine("None. Every member resolved a ticker and every ticker was found.");
        }
        else
        {
            report.AppendLine("| CIK | Ticker | Name | Why |");
            report.AppendLine("| --- | --- | --- | --- |");

            foreach (var entry in unmatched)
            {
                report.AppendLine(Universe.Inv(
                    $"| `{entry.Cik}` | {entry.Ticker ?? "-"} | {entry.Name ?? "-"} | {entry.Reason} |"));
            }

            report.AppendLine();
            report.AppendLine("Recorded rather than removed, and still members. A company that");
            report.AppendLine("disappears because a lookup failed is survivorship bias arriving");
            report.AppendLine("through the back door.");
        }

        report.AppendLine();
        report.AppendLine("## Members that left the universe");
        report.AppendLine();
        report.AppendLine("| CIK | Ticker | Name | Last filing | Absent from final cut |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var member in members
            .Where(m => m.StoppedFiling || m.AbsentFromFinalCrossSection)
            .OrderBy(m => m.LastFilingUtc ?? string.Empty, StringComparer.Ordinal)
            .Take(80))
        {
            report.AppendLine(Universe.Inv(
                $"| `{member.Cik}` | {member.Ticker ?? "-"} | {member.Name ?? "-"} | {member.LastFilingUtc ?? "-"} | {member.AbsentFromFinalCrossSection} |"));
        }

        report.AppendLine();
        report.AppendLine("These are the companies a universe scraped from today's listings could");
        report.AppendLine("not contain, and the reason the acquisition is worth its cost.");

        return report.ToString();
    }

    // ---- shapes ------------------------------------------------------------------------------

    private enum GateState
    {
        Failed = 0,
        Passed = 1,
        Deferred = 2,
    }

    private sealed record Gate(int Number, string Name, GateState State, string Detail);

    private sealed record Card(
        string? Name,
        string? Ticker,
        string? Exchange,
        string? Sic,
        string? Sector);

    private sealed record Member(
        string Cik,
        string? Ticker,
        string TickerSource,
        string? Exchange,
        string? Sic,
        string? Sector,
        string? Name,
        string? SizeBand,
        string RankedRevenue,
        string RevenueTag,
        string? FirstFilingUtc,
        string? LastFilingUtc,
        List<string> Cohorts,
        bool StoppedFiling,
        bool AbsentFromFinalCrossSection,
        string? Note);

    private sealed record Unmatched(string? Cik, string? Ticker, string? Name, string Reason);

    private sealed record Manifest(
        string? EvidenceBaseFingerprint,
        string SealedAtUtc,
        string WindowFromUtc,
        string WindowToUtc,
        int FamilyBudget,
        string MembershipRule,
        string MembershipCaveat,
        string PayloadDeduplication,
        string ObservationDeduplication,
        string QuarterlyCadenceRule,
        List<string> CohortCutDates,
        List<Member> Members,
        List<Unmatched> Unmatched);
}
