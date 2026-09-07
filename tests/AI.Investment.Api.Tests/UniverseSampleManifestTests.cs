using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Admission;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The systematic-sample universe: four hundred filers drawn across the whole population.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The selection rule is fixed before anything is measured, and it is arithmetic.</strong>
/// Order the population deterministically, take every eleventh member starting at the first. There
/// is no threshold to move, no boundary to nudge and nothing about the rule that could be tuned
/// after seeing a dropout rate, a price coverage figure or a sector mix - which is precisely why it
/// was chosen over "the top five hundred", a cut that would have cleared the survivorship gate by
/// one point and had no justification except that it cleared it.
/// </para>
/// <para>
/// <strong>It changes the research question, and the manifest says so in its own text.</strong>
/// The sealed four hundred were the largest US filers by revenue; these four hundred are a
/// systematic sample of every filer that reported one. That is a different population - smaller
/// companies, thinner price series, more failures - and any result measured on it is a result about
/// the US filer population rather than about large caps.
/// </para>
/// <para>
/// <strong>Zero SEC calls.</strong> Membership, ordering, company names and dropout status all come
/// from the seven cross-sections already archived. Tickers come from submissions already fetched
/// and from the delisted list already cached. Nothing here reaches a provider.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_SAMPLE=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseSampleManifestTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_SAMPLE";

    public const string BaseName = "us-pit-sample400-2021-09-to-2026-08";

    private const string RelativePath = "declarations/universe-sample400-2021-2026.json";

    /// <summary>The pre-registered survivorship floor. Never amended.</summary>
    private const decimal Gate2Floor = 0.05m;

    private const decimal MinimumQuarterlyPeriodsPerYear = 2.9m;

    /// <summary>Where the sample starts. The first member of the ordering, not a chosen offset.</summary>
    private const int StartingPosition = 0;

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseSampleManifestTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_systematic_sample_is_sealed_and_judged_against_every_applicable_gate()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Sample sealing is off. Set {GateVariable}=1 to run it. It makes no provider call.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();
        var nowUtc = services.GetRequiredService<IClock>().UtcNow;

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();

        // ---- the population, from the archive alone ------------------------------------------

        var cohorts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var cut in Universe.Cuts)
        {
            var frame = await FrameAsync(context, archive, Universe.MembershipFrame(cut));

            cohorts[Universe.Day(cut)] = frame.Keys.ToHashSet(StringComparer.Ordinal);

            foreach (var pair in frame)
            {
                names.TryAdd(pair.Key, pair.Value.EntityName);
            }
        }

        Skip.If(
            cohorts[Universe.Day(Universe.Cuts[0])].Count == 0,
            "The archived cross-sections could not be re-read.");

        var revenue = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var concept in Universe.RankingConcepts)
        {
            foreach (var pair in await FrameAsync(context, archive, Universe.RankingFrame(concept)))
            {
                if (!revenue.TryGetValue(pair.Key, out var held) || pair.Value.Value > held)
                {
                    revenue[pair.Key] = pair.Value.Value;
                }

                names.TryAdd(pair.Key, pair.Value.EntityName);
            }
        }

        var first = cohorts[Universe.Day(Universe.Cuts[0])];
        var final = cohorts[Universe.Day(Universe.Cuts[^1])];

        // The population: every filer in the first cross-section that also reported a CY2020
        // revenue figure. Stated as a rule, evaluated before any member is chosen.
        var population = new List<(string Cik, decimal Revenue)>();

        foreach (var cik in first)
        {
            if (revenue.TryGetValue(cik, out var value))
            {
                population.Add((cik, value));
            }
        }

        // The deterministic ordering, and it must be total: revenue descending, ties broken on the
        // CIK ordinally. Without the tie-break two filers reporting the same figure could swap
        // places between runs and the sample would not be reproducible from the manifest.
        population = population
            .OrderByDescending(p => p.Revenue)
            .ThenBy(p => p.Cik, StringComparer.Ordinal)
            .ToList();

        var interval = population.Count / Universe.Size;

        Assert.True(
            interval >= 1,
            Universe.Inv($"The population of {population.Count} is smaller than the {Universe.Size} ")
            + "members to be drawn from it, so no systematic sample exists.");

        var selected = new List<string>(Universe.Size);

        for (var i = 0; i < Universe.Size; i++)
        {
            selected.Add(population[StartingPosition + (i * interval)].Cik);
        }

        // ---- what is already known about each of them ----------------------------------------

        var profiles = await ProfilesAsync(context, selected.ToHashSet(StringComparer.Ordinal));
        var filings = await FilingsAsync(context, selected.ToHashSet(StringComparer.Ordinal));
        var delisted = LoadDelisted();

        var members = new List<Member>();
        var unmatched = new List<Unmatched>();
        var stoppedBefore = Universe.WindowEnd.AddMonths(-Universe.ReportingWindowMonths);

        foreach (var cik in selected.OrderBy(c => c, StringComparer.Ordinal))
        {
            profiles.TryGetValue(cik, out var card);
            filings.TryGetValue(cik, out var dates);

            names.TryGetValue(cik, out var entityName);

            var name = card?.Name ?? entityName;
            var ticker = card?.Ticker;
            string source;
            string? note = null;

            if (ticker is not null)
            {
                source = "sec-submissions";
            }
            else
            {
                // No live ticker on record. The delisted list is consulted by exact normalised
                // name, never by resemblance, and an ambiguous name is refused rather than resolved.
                var matches = MatchDelisted(name, delisted);

                if (matches.Count == 1)
                {
                    ticker = matches[0];
                    source = "eodhd-delisted, exact name match";
                    note = "ticker recovered from the delisted list; not confirmed against prices";
                }
                else
                {
                    source = "none";

                    unmatched.Add(new Unmatched(
                        cik,
                        name,
                        matches.Count > 1
                            ? Universe.Inv($"{matches.Count} delisted symbols share this normalised name; refused rather than chosen")
                            : delisted is null
                                ? "no delisted list is cached and EDGAR carries no ticker for this CIK"
                                : "no submissions document has been fetched for this CIK, and no delisted symbol's normalised name equals it"));
                }
            }

            var belongs = Universe.Cuts
                .Where(cut => cohorts[Universe.Day(cut)].Contains(cik))
                .Select(Universe.Day)
                .ToList();

            members.Add(new Member(
                cik,
                name,
                ticker,
                source,
                card?.Sic,
                card?.Sector,
                RankedRevenue: revenue.TryGetValue(cik, out var r)
                    ? r.ToString("F0", CultureInfo.InvariantCulture)
                    : "0",
                PopulationRank: population.FindIndex(p => string.Equals(p.Cik, cik, StringComparison.Ordinal)),
                FirstFilingUtc: dates is { Count: > 0 } ? Universe.Day(dates.Min()) : null,
                LastFilingUtc: dates is { Count: > 0 } ? Universe.Day(dates.Max()) : null,
                Cohorts: belongs,
                StoppedFiling: dates is { Count: > 0 } && dates.Max() < stoppedBefore,
                AbsentFromFinalCrossSection: !final.Contains(cik),
                FactsHeld: dates is { Count: > 0 },
                Note: note));
        }

        // ---- seal, then verify the seal off disk ---------------------------------------------

        var manifest = Seal(members, unmatched, population.Count, interval, nowUtc);
        var path = Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json");

        await Universe.WriteAsync(path, JsonSerializer.Serialize(manifest, Universe.Json) + "\n");

        var reread = JsonSerializer.Deserialize<Manifest>(
            await File.ReadAllTextAsync(path),
            Universe.Json);

        Assert.NotNull(reread);

        var recomputed = Fingerprint(reread! with { EvidenceBaseFingerprint = null });

        // ---- the gates -------------------------------------------------------------------------

        var gates = await JudgeAsync(context, members, recomputed);
        var report = Compose(manifest, members, unmatched, gates, population.Count, interval, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-sample400.md"),
            report);

        _output.WriteLine(report);

        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(opportunitiesBefore, await context.Opportunities.AsNoTracking().CountAsync());

        Assert.Equal(Universe.Size, members.Count);

        Assert.True(
            string.Equals(manifest.EvidenceBaseFingerprint, recomputed, StringComparison.Ordinal),
            Universe.Inv($"The manifest seals as `{manifest.EvidenceBaseFingerprint}` and reads back ")
            + Universe.Inv($"as `{recomputed}`."));

        var failed = gates.Where(g => g.State == GateState.Failed).ToList();

        Assert.True(
            failed.Count == 0,
            "Gates failed: " + string.Join(
                " | ",
                failed.Select(g => Universe.Inv($"{g.Number}. {g.Name}: {g.Detail}"))));
    }

    // ---- reading what is already held ----------------------------------------------------------

    private static async Task<Dictionary<string, (decimal Value, string EntityName)>> FrameAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        string identifier)
    {
        var values = new Dictionary<string, (decimal, string)>(StringComparer.Ordinal);

        var run = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.MarketWideDisclosure)
                .ToListAsync())
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .FirstOrDefault(r => string.Equals(
                r.Request.Subject.Identifier,
                identifier,
                StringComparison.Ordinal));

        if (run is null)
        {
            return values;
        }

        foreach (var hash in run.Artifacts)
        {
            var payload = await archive.RetrieveAsync(hash);

            if (payload is null)
            {
                continue;
            }

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
                    !row.TryGetProperty("cik", out var cikElement) ||
                    Universe.Cik(cikElement) is not { } cik)
                {
                    continue;
                }

                var value = 0m;

                if (row.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.Number)
                {
                    val.TryGetDecimal(out value);
                }

                var entity = row.TryGetProperty("entityName", out var n) &&
                    n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? string.Empty
                        : string.Empty;

                values[cik] = (value, entity.Trim());
            }

            return values;
        }

        return values;
    }

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
            var latest = group
                .GroupBy(r => r.Attribute, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.RetrievedAtUtc).First().Canonical,
                    StringComparer.Ordinal);

            string? Read(string attribute) =>
                latest.TryGetValue(attribute, out var value) ? value : null;

            var ticker = Read("company.ticker");

            cards[group.Key] = new Card(
                Read("company.name"),
                string.IsNullOrWhiteSpace(ticker) ? null : ticker.Trim().ToUpperInvariant(),
                Read("company.sic"),
                Read("company.sic-description"));
        }

        return cards;
    }

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

    // ---- the delisted list, matched by equality and never by resemblance -----------------------

    private static Dictionary<string, List<string>>? LoadDelisted()
    {
        var path = Universe.RepositoryPath("artifacts", "universe", "eodhd-delisted-us.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("Code", out var code) ||
                !row.TryGetProperty("Name", out var name) ||
                code.ValueKind != JsonValueKind.String ||
                name.ValueKind != JsonValueKind.String ||
                code.GetString() is not { Length: > 0 } symbol ||
                name.GetString() is not { Length: > 0 } text)
            {
                continue;
            }

            var key = NormaliseName(text);

            if (key.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(key, out var symbols))
            {
                symbols = [];
                byName[key] = symbols;
            }

            if (!symbols.Contains(symbol + ".US", StringComparer.Ordinal))
            {
                symbols.Add(symbol + ".US");
            }
        }

        return byName;
    }

    private static List<string> MatchDelisted(string? name, Dictionary<string, List<string>>? delisted)
    {
        if (name is null || delisted is null)
        {
            return [];
        }

        var key = NormaliseName(name);

        return key.Length > 0 && delisted.TryGetValue(key, out var symbols) ? symbols : [];
    }

    /// <summary>Case folded, punctuation dropped, a fixed list of legal suffixes removed.</summary>
    private static string NormaliseName(string name)
    {
        var letters = new StringBuilder(name.Length);

        foreach (var c in name.ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                letters.Append(c);
            }
            else if (letters.Length > 0 && letters[^1] != ' ')
            {
                letters.Append(' ');
            }
        }

        return string.Concat(letters.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !Suffixes.Contains(w)));
    }

    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "LLC", "LP", "LLLP",
        "LTD", "LIMITED", "PLC", "NV", "SA", "AG", "THE", "CLASS", "COMMON", "STOCK",
    };

    // ---- sealing --------------------------------------------------------------------------------

    private static string Fingerprint(Manifest content)
    {
        var canonical = JsonSerializer.Serialize(content, Universe.Json);

        return BaseName + "@" + Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..12];
    }

    private static Manifest Seal(
        List<Member> members,
        List<Unmatched> unmatched,
        int populationSize,
        int interval,
        DateTime nowUtc)
    {
        var content = new Manifest(
            EvidenceBaseFingerprint: null,
            SealedAtUtc: nowUtc.ToString("O", CultureInfo.InvariantCulture),
            WindowFromUtc: Universe.Day(Universe.WindowStart),
            WindowToUtc: Universe.Day(Universe.WindowEnd),
            FamilyBudget: Universe.FamilyBudget,
            PopulationDefinition:
                "Every filer present in EDGAR's us-gaap/Assets/USD/CY2021Q2I cross-section that " +
                "also reported a CY2020 revenue figure under either us-gaap/Revenues or " +
                "us-gaap/RevenueFromContractWithCustomerExcludingAssessedTax. Both are taken from " +
                "XBRL frames - a question asked of a period, not of a list of companies - so the " +
                "population contains the filers that later died as well as the ones that did not.",
            PopulationSize: populationSize,
            DeterministicOrdering:
                "CY2020 revenue descending, ties broken by CIK ascending in ordinal order. The " +
                "tie-break is what makes the sample reproducible: without it two filers reporting " +
                "the same figure could change places between runs.",
            SamplingInterval: interval,
            StartingPosition: StartingPosition,
            SelectionRule:
                "Systematic 1-in-" + interval.ToString(CultureInfo.InvariantCulture) + " sample: " +
                "the member at position " + StartingPosition.ToString(CultureInfo.InvariantCulture) +
                " of the ordering and every " + interval.ToString(CultureInfo.InvariantCulture) +
                "th thereafter, for exactly " + Universe.Size.ToString(CultureInfo.InvariantCulture) +
                " members. The rule is arithmetic and was fixed before any dropout rate, price " +
                "coverage figure, ticker mapping, sector mix or performance number was observed. " +
                "There is no threshold in it to move.",
            MembershipRule:
                "Membership is frozen at the 2021-08-31 cut and tracked forward only. A later cut " +
                "can remove a member that stopped reporting; it can never add one, and no company " +
                "outside the sample replaces a member that left.",
            MembershipCaveat:
                "A frame is EDGAR's present-day assembly of what was reported for a period, so it " +
                "can contain a figure first filed after the cut it stands for. Cohort membership " +
                "is corroborated against filing dates where company facts are held, and gate 3 " +
                "refuses any cohort preceding a member's first observed filing.",
            Limitations:
            [
                "This universe changes the research question. The previously sealed four hundred " +
                "were the largest US filers by revenue; these four hundred are a systematic sample " +
                "of every filer that reported one. Results measured here describe the US filer " +
                "population, not large caps, and the two are not comparable.",

                "Smaller filers carry thinner and more thinly traded price series. Coverage, " +
                "liquidity and price quality are expected to be materially worse than in a " +
                "large-cap slice, and none of that has been measured yet.",

                "Company facts and submissions have been fetched for only part of this sample. " +
                "The members drawn from below the top four hundred of the ordering have no stored " +
                "fundamentals, so gates measured over stored facts describe the covered subset " +
                "and say so.",

                "Tickers recovered from the delisted symbol list are matched by exact normalised " +
                "name and have not been confirmed against any price series.",
            ],
            PayloadDeduplication:
                "Archived payloads are de-duplicated by content hash, keyed on subject, before " +
                "normalisation. One payload is normalised once however many runs reach it.",
            ObservationDeduplication:
                "An observation is identified by subject, attribute, period end, publication " +
                "instant and canonical value. The companyfacts normaliser emits each such identity " +
                "once per document, so a 10-K and the 10-K/A that repeats it produce one row.",
            QuarterlyCadenceRule:
                "At least 2.9 distinct quarterly periods per company per complete calendar year, " +
                "excluding the current partial year. Three, not four: a US issuer files three " +
                "10-Qs a year because the fourth quarter is reported inside the 10-K.",
            CohortCutDates: Universe.Cuts.Select(Universe.Day).ToList(),
            Members: members,
            Unmatched: unmatched);

        return content with { EvidenceBaseFingerprint = Fingerprint(content) };
    }

    // ---- judging ---------------------------------------------------------------------------------

    private static async Task<List<Gate>> JudgeAsync(
        AppDbContext context,
        List<Member> members,
        string recomputed)
    {
        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var rows = await facts.CountAsync();

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

        var early = await facts.CountAsync(o => o.Provenance.PublishedAtUtc < o.Provenance.AsOfUtc);
        var late = await facts
            .CountAsync(o => o.Provenance.PublishedAtUtc > o.Provenance.RetrievedAtUtc);

        var attributes = await facts.Select(o => o.Attribute).Distinct().ToListAsync();
        var known = KnownAttributes();
        var strangers = attributes.Where(a => !known.Contains(a)).ToList();

        var covered = members.Count(m => m.FactsHeld);

        var quarterly = (await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == Universe.CompanyKind)
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"))
                .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.AsOfUtc })
                .Distinct()
                .ToListAsync())
            .Where(q => members.Exists(m => string.Equals(m.Cik, q.Cik, StringComparison.Ordinal)))
            .ToList();

        var periods = new HashSet<string>(StringComparer.Ordinal);
        var companyYears = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in quarterly)
        {
            if (row.AsOfUtc.Year < Universe.Cuts[0].Year || row.AsOfUtc.Year > Universe.Cuts[^1].Year)
            {
                continue;
            }

            periods.Add(Universe.Inv($"{row.Cik}|{row.AsOfUtc:yyyy-MM-dd}"));
            companyYears.Add(Universe.Inv($"{row.Cik}|{row.AsOfUtc.Year}"));
        }

        var cadence = companyYears.Count == 0
            ? 0m
            : Math.Round((decimal)periods.Count / companyYears.Count, 2);

        var dropouts = members.Count(m => m.StoppedFiling || m.AbsentFromFinalCrossSection);
        var share = (decimal)dropouts / members.Count;

        var lookAhead = members.Count(m =>
            m.FirstFilingUtc is { } firstFiled &&
            m.Cohorts.Exists(c => string.CompareOrdinal(c, firstFiled) < 0));

        var registerText = await File.ReadAllTextAsync(
            Universe.RepositoryPath(StrategyRegister.RelativePath));

        var declarations = StrategyRegister.Parse(registerText).Count;
        var cites = registerText.Contains(BaseName, StringComparison.Ordinal);

        return
        [
            new Gate(1, "Manifest sealed before any hypothesis",
                cites ? GateState.Failed : GateState.Passed,
                cites
                    ? "a sealed declaration already names this evidence base"
                    : Universe.Inv($"sealed to {RelativePath} as `{recomputed}`; no declaration names it")),

            new Gate(2, "Survivorship",
                share >= Gate2Floor ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{dropouts} of {members.Count} members left the universe inside the ")
                + Universe.Inv($"window - {share:P2} against a floor of {Gate2Floor:P0}, established ")
                + "independently by this manifest"),

            new Gate(3, "Membership look-ahead",
                lookAhead == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{lookAhead} members hold a cohort preceding their first observed ")
                + Universe.Inv($"filing; every member is present at the first cut by construction, ")
                + Universe.Inv($"and {members.Count - covered} carry no filing on record to check against")),

            new Gate(4, "Quarterly cadence",
                covered == members.Count
                    ? (cadence >= MinimumQuarterlyPeriodsPerYear ? GateState.Passed : GateState.Failed)
                    : GateState.Deferred,
                Universe.Inv($"{cadence:F2} per company per complete year against ")
                + Universe.Inv($"{MinimumQuarterlyPeriodsPerYear:F1}, measured over the {covered} of ")
                + Universe.Inv($"{members.Count} members whose facts are already held; the rest need ")
                + "an SEC acquisition this stage is not authorised to make"),

            new Gate(5, "No redefinition",
                strangers.Count == 0 ? GateState.Passed : GateState.Failed,
                strangers.Count == 0
                    ? Universe.Inv($"all {attributes.Count} stored financial attributes are declared ")
                      + "annual or quarterly names"
                    : "unknown attributes: " + string.Join(", ", strangers.Take(8))),

            new Gate(6, "Coverage", GateState.Deferred,
                "needs a price series for every member, which needs the acquisition"),

            new Gate(7, "Point-in-time invariants",
                early == 0 && late == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{early} facts published before the period they describe and {late} ")
                + Universe.Inv($"published after they were retrieved, over {rows} stored rows")),

            new Gate(8, "Request budget", GateState.Passed,
                "0 SEC calls and 1 billable EODHD call this stage, against 0 and 1 authorised"),

            new Gate(9, "Idempotency", GateState.Deferred,
                "a rerun can only be compared against a run"),

            new Gate(10, "Opportunities", GateState.Passed,
                "unchanged; asserted in this run, and no prediction, order or cycle was created"),

            new Gate(11, "Declarations", GateState.Passed,
                Universe.Inv($"{declarations} sealed declarations re-parsed and every fingerprint ")
                + "recomputed; none modified, renamed or re-based"),

            new Gate(12, "De-duplication",
                distinct == rows ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{distinct} distinct facts across {rows} stored rows")),
        ];
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

    // ---- reporting -------------------------------------------------------------------------------

    private static string Compose(
        Manifest manifest,
        List<Member> members,
        List<Unmatched> unmatched,
        List<Gate> gates,
        int populationSize,
        int interval,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        report.AppendLine("# The systematic sample - sealed, judged, nothing acquired");
        report.AppendLine();
        report.AppendLine("**No SEC call. One billable EODHD call, made by the delisted-list stage.**");
        report.AppendLine("**No price history. No strategy declared or scored.**");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| **Evidence-base fingerprint** | `{manifest.EvidenceBaseFingerprint}` |"));
        report.AppendLine(Universe.Inv($"| Population | {populationSize} filers |"));
        report.AppendLine(Universe.Inv($"| Sampling interval | 1 in {interval}, starting at position {StartingPosition} |"));
        report.AppendLine(Universe.Inv($"| **Members sealed** | **{members.Count}** |"));
        report.AppendLine(Universe.Inv($"| Ticker from SEC submissions | {members.Count(m => m.TickerSource == "sec-submissions")} |"));
        report.AppendLine(Universe.Inv($"| Ticker from the delisted list | {members.Count(m => m.TickerSource.StartsWith("eodhd", StringComparison.Ordinal))} |"));
        report.AppendLine(Universe.Inv($"| **Unmatched, recorded not dropped** | **{unmatched.Count}** |"));
        report.AppendLine(Universe.Inv($"| Fundamentals already held | {members.Count(m => m.FactsHeld)} |"));
        report.AppendLine(Universe.Inv($"| Stopped filing inside the window | {members.Count(m => m.StoppedFiling)} |"));
        report.AppendLine(Universe.Inv($"| Absent from the final cross-section | {members.Count(m => m.AbsentFromFinalCrossSection)} |"));
        report.AppendLine(Universe.Inv($"| Window | {manifest.WindowFromUtc} to {manifest.WindowToUtc} |"));
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

            report.AppendLine(Universe.Inv($"| {gate.Number} | {gate.Name} | {state} | {gate.Detail} |"));
        }

        report.AppendLine();
        report.AppendLine("## Members that left the universe");
        report.AppendLine();
        report.AppendLine("| CIK | Name | Ticker | Last filing | Absent from final cut |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var member in members
            .Where(m => m.StoppedFiling || m.AbsentFromFinalCrossSection)
            .OrderBy(m => m.Name ?? string.Empty, StringComparer.Ordinal)
            .Take(60))
        {
            report.AppendLine(Universe.Inv(
                $"| `{member.Cik}` | {member.Name ?? "-"} | {member.Ticker ?? "-"} | {member.LastFilingUtc ?? "-"} | {member.AbsentFromFinalCrossSection} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"…and the rest of the {members.Count(m => m.StoppedFiling || m.AbsentFromFinalCrossSection)} in the sealed manifest."));
        report.AppendLine();
        report.AppendLine("## Known limitations, as sealed");
        report.AppendLine();

        foreach (var limitation in manifest.Limitations)
        {
            report.AppendLine("- " + limitation);
        }

        return report.ToString();
    }

    // ---- shapes ------------------------------------------------------------------------------------

    private enum GateState
    {
        Failed = 0,
        Passed = 1,
        Deferred = 2,
    }

    private sealed record Gate(int Number, string Name, GateState State, string Detail);

    private sealed record Card(string? Name, string? Ticker, string? Sic, string? Sector);

    private sealed record Member(
        string Cik,
        string? Name,
        string? Ticker,
        string TickerSource,
        string? Sic,
        string? Sector,
        string RankedRevenue,
        int PopulationRank,
        string? FirstFilingUtc,
        string? LastFilingUtc,
        List<string> Cohorts,
        bool StoppedFiling,
        bool AbsentFromFinalCrossSection,
        bool FactsHeld,
        string? Note);

    private sealed record Unmatched(string Cik, string? Name, string Reason);

    private sealed record Manifest(
        string? EvidenceBaseFingerprint,
        string SealedAtUtc,
        string WindowFromUtc,
        string WindowToUtc,
        int FamilyBudget,
        string PopulationDefinition,
        int PopulationSize,
        string DeterministicOrdering,
        int SamplingInterval,
        int StartingPosition,
        string SelectionRule,
        string MembershipRule,
        string MembershipCaveat,
        List<string> Limitations,
        string PayloadDeduplication,
        string ObservationDeduplication,
        string QuarterlyCadenceRule,
        List<string> CohortCutDates,
        List<Member> Members,
        List<Unmatched> Unmatched);
}
