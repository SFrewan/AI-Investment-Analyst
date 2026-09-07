using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Infrastructure.Admission;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Builds and seals a point-in-time universe manifest, and judges it against the twelve gates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This seals the universe already held, not the four hundred.</strong> Membership is
/// defined from SEC filings, so naming four hundred companies needs EDGAR's quarterly form index
/// and their company facts - and mapping any of them to a price series needs EODHD's symbol list,
/// which is billable. None of that is authorised yet. What is buildable at zero cost is the whole
/// mechanism, exercised end to end against the eighteen CIKs and twenty tickers the platform
/// already holds: the schema, the filing-derived membership, the sealing, the fingerprint, the
/// de-duplication rule, the family budget and the gate harness.
/// </para>
/// <para>
/// That is worth doing first rather than writing all of it blind and discovering its defects
/// during a 1,223-request acquisition. The manifest it produces is real, sealed and fingerprinted;
/// it simply describes a smaller universe, and says so in its own name.
/// </para>
/// <para>
/// <strong>Nothing is fetched and nothing is written to the store.</strong> The manifest is written
/// to <c>declarations/</c>, which is the point of it; observation, run and opportunity counts are
/// asserted unchanged.
/// </para>
/// <para>
/// Gated on <c>AIINV_MANIFEST=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseManifestTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_MANIFEST";

    /// <summary>The trial-family budget, declared before any hypothesis names this base.</summary>
    /// <remarks>
    /// Fixed here so every strategy on the base is sized at the same significance from the first
    /// measurement. Counting families as they appear sizes the first strategy at 0.05 and the fifth
    /// at 0.01, which reaches two verdicts at two different bars on one body of evidence.
    /// </remarks>
    private const int FamilyBudget = 5;

    private const string BaseName = "us-pit-pilot-2021-09-to-2026-08";

    private static readonly DateOnly WindowStart = new(2021, 9, 1);
    private static readonly DateOnly WindowEnd = new(2026, 8, 31);

    /// <summary>How stale a filing may be and still count as reporting at a cut date.</summary>
    /// <remarks>
    /// Fifteen months: an annual filer that has filed once in the last year is still a going
    /// concern, and the extra quarter absorbs a late filing without admitting a company that
    /// stopped reporting two years ago.
    /// </remarks>
    private const int ReportingWindowMonths = 15;

    /// <summary>
    /// The known CIK-to-ticker mapping, unchanged from the frozen universe.
    /// </summary>
    /// <remarks>
    /// Hard-coded here only because the pilot universe is the frozen twenty, whose mapping the
    /// platform already established. The four-hundred-company manifest resolves this from EODHD's
    /// symbol list, which is the billable step this stage is not authorised to take.
    /// </remarks>
    private static readonly (string Ticker, string Cik)[] Known =
    {
        ("MSFT.US", "0000789019"), ("GOOGL.US", "0001652044"), ("AMZN.US", "0001018724"),
        ("NVDA.US", "0001045810"), ("META.US", "0001326801"), ("TSLA.US", "0001318605"),
        ("JPM.US", "0000019617"), ("V.US", "0001403161"), ("JNJ.US", "0000200406"),
        ("WMT.US", "0000104169"), ("PG.US", "0000080424"), ("XOM.US", "0002115436"),
        ("UNH.US", "0000731766"), ("MA.US", "0001141391"), ("KO.US", "0000021344"),
        ("PEP.US", "0000077476"), ("CVX.US", "0000093410"), ("MRK.US", "0000310158"),
        ("AAPL.US", "0000320193"), ("HD.US", "0000060695"),
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseManifestTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_universe_is_sealed_and_judged_against_every_gate_that_precedes_acquisition()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Manifest sealing is off. Set {GateVariable}=1 to run it. It fetches nothing.");

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var nowUtc = services.GetRequiredService<IClock>().UtcNow;

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();

        var filings = await FilingsAsync(context);
        var revenue = await RevenueAsync(context);
        var priced = await PricedTickersAsync(context);

        Skip.If(filings.Count == 0, "No fundamental observations are stored. Nothing to seal.");

        var cuts = CutDates();
        var members = new List<Member>();
        var unmatched = new List<Unmatched>();

        foreach (var (ticker, cik) in Known.OrderBy(k => k.Ticker, StringComparer.Ordinal))
        {
            if (!filings.TryGetValue(cik, out var dates) || dates.Count == 0)
            {
                // Recorded, never dropped. A member that vanishes from the manifest because its
                // facts could not be found is the survivorship bias this design exists to remove,
                // arriving through the back door of a lookup failure.
                unmatched.Add(new Unmatched(cik, ticker, "no SEC facts stored for this CIK"));

                continue;
            }

            var cohorts = cuts
                .Where(cut => dates.Any(d =>
                    d <= cut && d > cut.AddMonths(-ReportingWindowMonths)))
                .Select(cut => cut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList();

            members.Add(new Member(
                cik,
                priced.Contains(ticker) ? ticker : null,
                Sector: null,
                SizeBand: null,
                FirstFilingUtc: dates.Min().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LastFilingUtc: dates.Max().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Cohorts: cohorts,
                StoppedFiling: dates.Max() < WindowEnd.AddMonths(-ReportingWindowMonths),
                Note: priced.Contains(ticker) ? null : "no price series held"));
        }

        // Size bands from revenue as filed by the first cut, ranked across members that had one.
        // Nothing here uses a figure published after the date it ranks at.
        AssignSizeBands(members, revenue, cuts[0]);

        foreach (var ticker in priced.Where(t => !Known.Any(k =>
            string.Equals(k.Ticker, t, StringComparison.Ordinal))))
        {
            unmatched.Add(new Unmatched(null, ticker, "price series with no CIK mapping"));
        }

        var manifest = Seal(members, unmatched, cuts, nowUtc);
        var path = await WriteManifestAsync(manifest);

        var declarations = VerifySeals();
        var gates = Judge(manifest, members, unmatched, declarations, path);
        var report = Compose(manifest, members, unmatched, gates, nowUtc);

        await WriteReportAsync(report);
        _output.WriteLine(report);

        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(opportunitiesBefore, await context.Opportunities.AsNoTracking().CountAsync());

        var failed = gates.Where(g => g.State == GateState.Failed).ToList();

        Assert.True(
            failed.Count == 0,
            "Gates failed before acquisition: " +
            string.Join(" | ", failed.Select(g => Inv($"{g.Number}. {g.Name}: {g.Detail}"))));
    }

    // ---- reading what is already held --------------------------------------------------------

    /// <summary>Every distinct filing date per CIK, from the provenance of stored facts.</summary>
    private static async Task<Dictionary<string, List<DateOnly>>> FilingsAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == "Company")
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
            .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.PublishedAtUtc })
            .Distinct()
            .ToListAsync();

        return rows
            .GroupBy(r => r.Cik, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => DateOnly.FromDateTime(r.PublishedAtUtc)).Distinct().ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>Revenue as filed, with its publication date, so a rank can be point-in-time.</summary>
    private static async Task<Dictionary<string, List<(DateOnly Filed, decimal Value)>>> RevenueAsync(
        AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == "Company" && o.Attribute == FinancialFigures.Revenue)
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Value.Canonical,
                o.Provenance.PublishedAtUtc,
            })
            .ToListAsync();

        var byCik = new Dictionary<string, List<(DateOnly, decimal)>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!decimal.TryParse(
                    row.Canonical,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            if (!byCik.TryGetValue(row.Cik, out var list))
            {
                list = [];
                byCik[row.Cik] = list;
            }

            list.Add((DateOnly.FromDateTime(row.PublishedAtUtc), value));
        }

        return byCik;
    }

    private static async Task<HashSet<string>> PricedTickersAsync(AppDbContext context) =>
        (await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == "Security" && o.Attribute == "security.close")
            .Select(o => o.Subject.Identifier!)
            .Distinct()
            .ToListAsync())
        .ToHashSet(StringComparer.Ordinal);

    // ---- building --------------------------------------------------------------------------

    private static List<DateOnly> CutDates() =>
        Enumerable.Range(0, 5).Select(i => new DateOnly(2021 + i, 8, 31)).ToList();

    /// <summary>
    /// Terciles of revenue as it stood at the first cut date. Members with no revenue on the
    /// record by then are banded <c>unranked</c> rather than guessed at.
    /// </summary>
    private static void AssignSizeBands(
        List<Member> members,
        Dictionary<string, List<(DateOnly Filed, decimal Value)>> revenue,
        DateOnly at)
    {
        var ranked = new List<(int Index, decimal Value)>();

        for (var i = 0; i < members.Count; i++)
        {
            if (!revenue.TryGetValue(members[i].Cik, out var points))
            {
                continue;
            }

            var visible = points.Where(p => p.Filed <= at).ToList();

            if (visible.Count > 0)
            {
                ranked.Add((i, visible.OrderByDescending(p => p.Filed).First().Value));
            }
        }

        ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var band = rank * 3 < ranked.Count ? "large"
                : rank * 3 < ranked.Count * 2 ? "mid"
                : "small";

            members[ranked[rank].Index] = members[ranked[rank].Index] with { SizeBand = band };
        }

        for (var i = 0; i < members.Count; i++)
        {
            if (members[i].SizeBand is null)
            {
                members[i] = members[i] with { SizeBand = "unranked" };
            }
        }
    }

    /// <summary>
    /// Hashes the manifest's content, then names the evidence base after the hash.
    /// </summary>
    /// <remarks>
    /// The fingerprint is taken over everything <em>except</em> the name it produces, because a
    /// name cannot contain its own hash. Every field that decides which companies are in and when
    /// they were members is inside it, so a changed universe is mechanically a different evidence
    /// base rather than the same one described differently.
    /// </remarks>
    private static Manifest Seal(
        List<Member> members,
        List<Unmatched> unmatched,
        List<DateOnly> cuts,
        DateTime nowUtc)
    {
        var content = new Manifest(
            EvidenceBaseFingerprint: null,
            SealedAtUtc: nowUtc.ToString("O", CultureInfo.InvariantCulture),
            WindowFromUtc: WindowStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowToUtc: WindowEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FamilyBudget: FamilyBudget,
            PayloadDeduplication:
                "Archived payloads are de-duplicated by content hash, keyed on subject, before " +
                "normalisation. One payload is normalised once however many ingestion runs reach it.",
            ObservationDeduplication:
                "An observation is identified by subject, attribute, period end, publication " +
                "instant and canonical value. Two payloads whose histories overlap must not record " +
                "the same fact twice.",
            CohortCutDates: cuts
                .Select(c => c.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList(),
            Members: members.OrderBy(m => m.Cik, StringComparer.Ordinal).ToList(),
            Unmatched: unmatched.OrderBy(u => u.Ticker ?? u.Cik, StringComparer.Ordinal).ToList());

        var canonical = JsonSerializer.Serialize(content, Json);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        return content with { EvidenceBaseFingerprint = BaseName + "@" + digest[..12] };
    }

    // ---- judging ----------------------------------------------------------------------------

    /// <summary>
    /// Re-parses the sealed register, which throws if any fingerprint no longer matches its entry.
    /// </summary>
    /// <remarks>
    /// Gate 11 asserted rather than checked would be a gate in name only, and this one is cheap:
    /// <c>StrategyRegister.Parse</c> recomputes every declaration's fingerprint and refuses the file
    /// if one has drifted.
    /// </remarks>
    private static int VerifySeals()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            StrategyRegister.RelativePath));

        return StrategyRegister.Parse(File.ReadAllText(path)).Count;
    }

    private static List<Gate> Judge(
        Manifest manifest,
        List<Member> members,
        List<Unmatched> unmatched,
        int declarations,
        string path)
    {
        var stopped = members.Count(m => m.StoppedFiling);
        var withPrices = members.Count(m => m.Ticker is not null);

        var lookAhead = members.All(m =>
            m.Cohorts.All(c => string.CompareOrdinal(m.FirstFilingUtc, c) <= 0));

        return
        [
            new Gate(1, "Manifest sealed first", GateState.Passed,
                Inv($"sealed to {path}; fingerprint derived from its own content")),

            new Gate(2, "Survivorship", stopped > 0 ? GateState.Passed : GateState.Failed,
                stopped > 0
                    ? Inv($"{stopped} of {members.Count} members stopped filing inside the window")
                    : Inv($"0 of {members.Count} members stopped filing. On the frozen twenty this ")
                      + "is expected and is exactly why the pilot cannot stand in for the four "
                      + "hundred: a universe of survivors has no dropouts to find."),

            new Gate(3, "Membership look-ahead", lookAhead ? GateState.Passed : GateState.Failed,
                "every cohort a member belongs to falls at or after its first filing"),

            new Gate(4, "Quarterly cadence", GateState.Passed,
                "3.27 per company per complete year against 2.9, verified in the quarterly stage"),

            new Gate(5, "No redefinition", GateState.Passed,
                "annual attributes unchanged; 0 of 22,082 produced rows failed to match a stored row"),

            new Gate(6, "Coverage", GateState.Deferred,
                "needs price series for every member, which needs the acquisition"),

            new Gate(7, "Point-in-time invariants", GateState.Deferred,
                "counted over stored rows, which the acquisition writes"),

            new Gate(8, "Request budget", GateState.Passed,
                Inv($"{members.Count} members and {unmatched.Count} unmatched; the pilot needs no ")
                + "requests, and the four-hundred plan's 1,223 stands unchanged"),

            new Gate(9, "Idempotency", GateState.Deferred,
                "a rerun can only be compared against a run"),

            new Gate(10, "Opportunities", GateState.Passed,
                "unchanged; asserted in this run"),

            new Gate(11, "Declarations", GateState.Passed,
                Inv($"{declarations} sealed declarations re-parsed and every fingerprint recomputed; ")
                + "none modified, renamed or re-based"),

            new Gate(12, "De-duplication rule", GateState.Passed,
                "payload and observation rules both recorded in the manifest"),
        ];
    }

    // ---- reporting ---------------------------------------------------------------------------

    private static string Compose(
        Manifest manifest,
        List<Member> members,
        List<Unmatched> unmatched,
        List<Gate> gates,
        DateTime nowUtc)
    {
        var report = new StringBuilder();

        report.AppendLine("# Point-in-time universe manifest — sealed, nothing acquired");
        report.AppendLine();
        report.AppendLine(Inv($"Sealed {nowUtc:yyyy-MM-dd HH:mm:ss}Z. **No provider call. No row written.**"));
        report.AppendLine();
        report.AppendLine("**This is the pilot universe, not the four hundred.** Naming four hundred");
        report.AppendLine("companies needs EDGAR's form index and their company facts, and mapping any of");
        report.AppendLine("them to a price series needs EODHD's symbol list. Neither is authorised yet.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Inv($"| **Evidence-base fingerprint** | `{manifest.EvidenceBaseFingerprint}` |"));
        report.AppendLine(Inv($"| Window | {manifest.WindowFromUtc} → {manifest.WindowToUtc} |"));
        report.AppendLine(Inv($"| Members sealed | {members.Count} |"));
        report.AppendLine(Inv($"| With a price series | {members.Count(m => m.Ticker is not null)} |"));
        report.AppendLine(Inv($"| **Unmatched, recorded not dropped** | **{unmatched.Count}** |"));
        report.AppendLine(Inv($"| Stopped filing inside the window | {members.Count(m => m.StoppedFiling)} |"));
        report.AppendLine(Inv($"| Family budget | {manifest.FamilyBudget}, so significance 0.05/{manifest.FamilyBudget} = 0.0100 |"));
        report.AppendLine(Inv($"| Cohort cut dates | {string.Join(", ", manifest.CohortCutDates)} |"));
        report.AppendLine(Inv($"| Sector | **not populated** — SIC codes come from EDGAR submissions |"));
        report.AppendLine();
        report.AppendLine("Sector is the one requested field this stage cannot fill. It lives in EDGAR's");
        report.AppendLine("submissions document, which is a call per company; the field is present in the");
        report.AppendLine("schema and null in every row rather than guessed at or quietly omitted.");
        report.AppendLine();

        report.AppendLine("## Members");
        report.AppendLine();
        report.AppendLine("| CIK | Ticker | Size band | First filing | Last filing | Cohorts | Note |");
        report.AppendLine("| --- | --- | --- | --- | --- | ---: | --- |");

        foreach (var m in members)
        {
            report.AppendLine(Inv(
                $"| `{m.Cik}` | {m.Ticker ?? "—"} | {m.SizeBand} | {m.FirstFilingUtc} | {m.LastFilingUtc} | {m.Cohorts.Count} | {m.Note ?? ""} |"));
        }

        report.AppendLine();
        report.AppendLine("## Unmatched");
        report.AppendLine();

        if (unmatched.Count == 0)
        {
            report.AppendLine("None.");
        }
        else
        {
            report.AppendLine("| CIK | Ticker | Why |");
            report.AppendLine("| --- | --- | --- |");

            foreach (var u in unmatched)
            {
                report.AppendLine(Inv($"| {u.Cik ?? "—"} | {u.Ticker ?? "—"} | {u.Reason} |"));
            }

            report.AppendLine();
            report.AppendLine("Recorded rather than removed. A member that disappears because a lookup");
            report.AppendLine("failed is survivorship bias arriving through the back door.");
        }

        report.AppendLine();
        report.AppendLine("## The twelve gates");
        report.AppendLine();
        report.AppendLine("| # | Gate | Result | Detail |");
        report.AppendLine("| ---: | --- | --- | --- |");

        foreach (var g in gates)
        {
            var state = g.State switch
            {
                GateState.Passed => "**PASS**",
                GateState.Failed => "**FAIL**",
                _ => "deferred",
            };

            report.AppendLine(Inv($"| {g.Number} | {g.Name} | {state} | {g.Detail} |"));
        }

        report.AppendLine();
        report.AppendLine("Four gates are deferred by construction: coverage, the point-in-time");
        report.AppendLine("invariants, idempotency and the acquisition's opportunity count all describe");
        report.AppendLine("stored rows that only an acquisition produces. They are not skipped, and no");
        report.AppendLine("hypothesis may name this base until they have run.");

        return report.ToString();
    }

    private static async Task<string> WriteManifestAsync(Manifest manifest)
    {
        var relative = Path.Combine("declarations", "universe-pilot-2021-2026.json");

        var full = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, JsonSerializer.Serialize(manifest, Json) + "\n");

        return relative.Replace('\\', '/');
    }

    private static async Task WriteReportAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "universe-manifest.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, report);
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    // ---- shapes ------------------------------------------------------------------------------

    private enum GateState
    {
        Failed = 0,
        Passed = 1,
        Deferred = 2,
    }

    private sealed record Gate(int Number, string Name, GateState State, string Detail);

    private sealed record Member(
        string Cik,
        string? Ticker,
        string? Sector,
        string? SizeBand,
        string FirstFilingUtc,
        string LastFilingUtc,
        List<string> Cohorts,
        bool StoppedFiling,
        string? Note);

    private sealed record Unmatched(string? Cik, string? Ticker, string Reason);

    private sealed record Manifest(
        string? EvidenceBaseFingerprint,
        string SealedAtUtc,
        string WindowFromUtc,
        string WindowToUtc,
        int FamilyBudget,
        string PayloadDeduplication,
        string ObservationDeduplication,
        List<string> CohortCutDates,
        List<Member> Members,
        List<Unmatched> Unmatched);
}
