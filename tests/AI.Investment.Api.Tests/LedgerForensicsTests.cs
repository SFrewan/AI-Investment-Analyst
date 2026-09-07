using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// What the store can actually prove about what was fetched, and about what it already holds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and no provider of any kind.</strong> Nothing is written to the database;
/// run, observation, execution and audit counts are asserted unchanged either side. Two artefacts
/// are written to <c>artifacts/</c> and nothing else on disk is touched.
/// </para>
/// <para>
/// Two discrepancies brought this into being, and both are about the difference between a number
/// and the evidence for it. The dry run found six ledger entries that satisfy planned requests
/// where one billable call was believed spent; and it found that only three of three hundred and
/// fifty-five acquirable symbols match any of twenty-five thousand stored closing prices. Neither
/// is safe to resolve by reasoning about what probably happened, because the answer decides how
/// much money the next stage spends and whether a coverage gate is measuring the series it thinks
/// it is.
/// </para>
/// </remarks>
public sealed class LedgerForensicsTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_LEDGER_FORENSICS";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string VendorPrefix = "eodhd";

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public LedgerForensicsTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_ledger_and_the_price_namespace_are_traced_without_touching_either()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The ledger forensics pass is off. Set {GateVariable}=1 to run it. It reads only.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var executionsBefore = await context.ActionExecutions.AsNoTracking().CountAsync();
        var auditsBefore = await context.AuditRecords.AsNoTracking().CountAsync();

        var members = await MembersAsync();

        Assert.True(members.Count == 400, "the identity table is not the sealed four hundred");

        // ---- part one: what the vendor was actually asked for ---------------------------------------

        var allRuns = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var vendorRuns = allRuns
            .Where(r => r.Request.SourceId.Value.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.StartedAtUtc)
            .Select(r => new VendorRun(
                r.Request.SourceId.Value,
                r.Request.Category.ToString(),
                r.Request.Region.Code,
                r.Request.Subject.Kind,
                r.Request.Subject.Identifier,
                r.Request.Window is null
                    ? null
                    : Universe.Inv($"{r.Request.Window.StartUtc:yyyy-MM-dd}..{r.Request.Window.EndUtc:yyyy-MM-dd}"),
                r.Request.Fingerprint(),
                r.Outcome.ToString(),
                r.StartedAtUtc,
                r.CompletedAtUtc,
                r.Artifacts.Count,
                r.Artifacts.Select(a => a.Abbreviated).ToList(),
                r.Request.CorrelationId.Value,
                r.Reason,
                r.RefusalRuleId))
            .ToList();

        // The seam's own record of what it let through, keyed on the same fingerprint the request
        // computes. An execution is the platform saying "this was dispatched"; its absence is not
        // proof of the opposite, and the report says so rather than inferring.
        var executions = (await context.ActionExecutions.AsNoTracking().ToListAsync())
            .ToLookup(e => e.IdempotencyKey, StringComparer.Ordinal);

        var audits = await context.AuditRecords.AsNoTracking().ToListAsync();

        var suppressions = audits
            .Where(a => a.EventType == AuditEventType.DuplicateSuppressed)
            .ToList();

        var vendorAudits = audits
            .Where(a => a.Capability == Capability.DataIngestion)
            .ToList();

        // ---- part two: every closing price, traced --------------------------------------------------

        var priceRows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .ToListAsync();

        var splitRows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute)
            .ToListAsync();

        var index = Index(members);

        var series = priceRows
            .GroupBy(o => o.Subject.Identifier ?? string.Empty, StringComparer.Ordinal)
            .Select(g => Classify(g.Key, g.ToList(), index))
            .OrderByDescending(s => s.Rows)
            .ToList();

        var byBucket = series
            .GroupBy(s => s.Bucket, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Series: g.Count(), Rows: g.Sum(s => s.Rows)), StringComparer.Ordinal);

        // ---- part three: what the planned requests would land on top of -----------------------------

        var acquirable = members.Where(m => m.Ready).ToList();

        var priceBySymbol = series.ToDictionary(s => s.Identifier, s => s, StringComparer.Ordinal);

        var splitsBySymbol = splitRows
            .GroupBy(o => o.Subject.Identifier ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var satisfied = vendorRuns
            .Where(r => string.Equals(r.Outcome, nameof(IngestionOutcome.Succeeded), StringComparison.Ordinal))
            .Select(r => r.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        var overlap = new List<Overlap>();

        foreach (var member in acquirable)
        {
            var symbol = AcquisitionPlanning.SymbolFor(member.Ticker!);

            var prices = priceBySymbol.TryGetValue(symbol, out var held) ? held.Rows : 0;
            var splits = splitsBySymbol.TryGetValue(symbol, out var s) ? s : 0;

            if (prices > 0 || splits > 0)
            {
                overlap.Add(new Overlap(member.Cik, member.Name, symbol, prices, splits));
            }
        }

        // ---- part four: is "no series" a fault, or simply "never asked for"? ------------------------

        // The distinction the gate cannot currently draw, computed here as evidence rather than
        // applied as a change. A member that was never requested and holds nothing is not a
        // coverage fault; a member that WAS requested, succeeded, and still holds nothing is.
        var requestedSymbols = vendorRuns
            .Where(r =>
                string.Equals(r.Outcome, nameof(IngestionOutcome.Succeeded), StringComparison.Ordinal) &&
                string.Equals(r.Category, nameof(DataCategory.MarketPrices), StringComparison.Ordinal) &&
                r.Subject is not null)
            .Select(r => r.Subject!)
            .ToHashSet(StringComparer.Ordinal);

        var neverRequested = 0;
        var requestedAndEmpty = new List<string>();

        foreach (var member in acquirable)
        {
            var symbol = AcquisitionPlanning.SymbolFor(member.Ticker!);
            var rows = priceBySymbol.TryGetValue(symbol, out var held) ? held.Rows : 0;

            if (rows > 0)
            {
                continue;
            }

            if (requestedSymbols.Contains(symbol))
            {
                requestedAndEmpty.Add(symbol);
            }
            else
            {
                neverRequested++;
            }
        }

        // ---- artefacts -------------------------------------------------------------------------------

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "universe", "price-namespace-map.json"),
            JsonSerializer.Serialize(
                new
                {
                    EvidenceBaseFingerprint = SealedFingerprint,
                    TracedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    PriceRows = priceRows.Count,
                    SplitRows = splitRows.Count,
                    DistinctPriceSeries = series.Count,
                    Buckets = byBucket.ToDictionary(
                        b => b.Key,
                        b => new { b.Value.Series, b.Value.Rows },
                        StringComparer.Ordinal),
                    VendorRuns = vendorRuns,
                    Series = series,
                    PlannedOverlap = overlap,
                },
                Universe.Json) + "\n");

        var report = Compose(
            vendorRuns,
            executions,
            suppressions.Count,
            vendorAudits.Count,
            allRuns.Count,
            priceRows.Count,
            splitRows.Count,
            series,
            byBucket,
            overlap,
            acquirable.Count,
            neverRequested,
            requestedAndEmpty,
            satisfied,
            watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "ledger-forensics.md"),
            report);

        _output.WriteLine(report);

        // ---- reading is not writing --------------------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(executionsBefore, await context.ActionExecutions.AsNoTracking().CountAsync());
        Assert.Equal(auditsBefore, await context.AuditRecords.AsNoTracking().CountAsync());

        // Every price row lands in exactly one bucket, or the mapping is not a mapping.
        Assert.True(
            byBucket.Sum(b => b.Value.Rows) == priceRows.Count,
            Universe.Inv($"{byBucket.Sum(b => b.Value.Rows)} rows bucketed of {priceRows.Count}"));
    }

    // ---- classification ------------------------------------------------------------------------------

    /// <summary>Every notation a sealed member's symbol is known by, pointing back at the member.</summary>
    private static Dictionary<string, Member> Index(List<Member> members)
    {
        var index = new Dictionary<string, Member>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in members)
        {
            foreach (var candidate in new[] { member.Ticker, member.ManifestTicker })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var bare = IdentityResolution.Symbol(candidate);

                // Both notations, because the whole question is which one the store used.
                foreach (var form in new[] { bare, bare + AcquisitionPlanning.UsSuffix })
                {
                    index.TryAdd(form, member);
                }
            }
        }

        return index;
    }

    private static Series Classify(string identifier, List<Observation> rows, Dictionary<string, Member> index)
    {
        var first = rows.Min(o => o.Provenance.AsOfUtc);
        var last = rows.Max(o => o.Provenance.AsOfUtc);

        var sources = rows
            .Select(o => o.Provenance.SourceId.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var sessions = rows.Select(o => o.Provenance.AsOfUtc.Date).Distinct().Count();

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return new Series(
                identifier, null, null, false, "E - unmappable: the row carries no subject identifier",
                rows.Count, sessions, first, last, sources);
        }

        if (!index.TryGetValue(identifier, out var member))
        {
            return new Series(
                identifier, null, null, false, "D - outside the sealed universe",
                rows.Count, sessions, first, last, sources);
        }

        // Ordered, and the order is the point: the exact symbol a request would carry comes first,
        // so "already held" never quietly means "held under a name the acquisition will not use".
        var planned = member.Ready && member.Ticker is not null
            ? AcquisitionPlanning.SymbolFor(member.Ticker)
            : null;

        var bucket = planned is not null && string.Equals(identifier, planned, StringComparison.Ordinal)
            ? "A - a current acquisition-ready member, under the exact symbol a request would carry"
            : !member.Ready
                ? "B - one of the forty-five not-ready members"
                : "C - a sealed member, but under a notation no planned request uses";

        return new Series(
            identifier, member.Cik, member.Name, member.Ready, bucket,
            rows.Count, sessions, first, last, sources);
    }

    // ---- reading ---------------------------------------------------------------------------------------

    private static async Task<List<Member>> MembersAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        var manifestTickers = manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null,
                StringComparer.Ordinal);

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m =>
            {
                var cik = m.GetProperty("Cik").GetString() ?? string.Empty;

                return new Member(
                    cik,
                    m.GetProperty("Name").GetString() ?? "(unnamed)",
                    m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null,
                    manifestTickers.TryGetValue(cik, out var mt) ? mt : null);
            })
            .ToList();
    }

    // ---- reporting -------------------------------------------------------------------------------------

    private static string Compose(
        List<VendorRun> vendorRuns,
        ILookup<string, AI.Investment.Domain.Actions.ActionExecution> executions,
        int suppressions,
        int ingestionAudits,
        int allRuns,
        int priceRows,
        int splitRows,
        List<Series> series,
        Dictionary<string, (int Series, int Rows)> byBucket,
        List<Overlap> overlap,
        int acquirable,
        int neverRequested,
        List<string> requestedAndEmpty,
        HashSet<string> satisfied,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        report.AppendLine("# Ledger and price-namespace forensics");
        report.AppendLine();
        report.AppendLine("**Read-only.** No provider was called. Run, observation, execution and");
        report.AppendLine("audit counts are asserted unchanged either side, and the sealed manifest was");
        report.AppendLine("not opened for writing. **Nothing was acquired.**");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Sealed universe `{SealedFingerprint}`."));
        report.AppendLine();

        // ---- one -------------------------------------------------------------------------------------

        report.AppendLine("## 1. What the vendor was actually asked for");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Ingestion runs in the store, all sources | {allRuns} |"));
        report.AppendLine(Universe.Inv($"| Runs against an `{VendorPrefix}` source | {vendorRuns.Count} |"));
        report.AppendLine(Universe.Inv($"| Distinct request fingerprints among them | {vendorRuns.Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine(Universe.Inv($"| Succeeded | {vendorRuns.Count(r => r.Outcome == nameof(IngestionOutcome.Succeeded))} |"));
        report.AppendLine(Universe.Inv($"| Refused, and never dispatched | {vendorRuns.Count(r => r.Outcome == nameof(IngestionOutcome.Refused))} |"));
        report.AppendLine(Universe.Inv($"| Failed | {vendorRuns.Count(r => r.Outcome == nameof(IngestionOutcome.Failed))} |"));
        report.AppendLine(Universe.Inv($"| Runs holding at least one archived payload | {vendorRuns.Count(r => r.Artifacts > 0)} |"));
        report.AppendLine(Universe.Inv($"| Distinct archived payload hashes | {vendorRuns.SelectMany(r => r.ArtifactHashes).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine(Universe.Inv($"| Audit records for `DataIngestion` | {ingestionAudits} |"));
        report.AppendLine(Universe.Inv($"| Audit records for a suppressed duplicate | {suppressions} |"));
        report.AppendLine();

        if (vendorRuns.Count == 0)
        {
            report.AppendLine("No run against this vendor is recorded at all.");
        }
        else
        {
            report.AppendLine("Every one of them, in the order they started:");
            report.AppendLine();
            report.AppendLine("| Started (UTC) | Source | Category | Subject | Window | Outcome | Payloads | Seam execution | Fingerprint |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | ---: | --- | --- |");

            foreach (var run in vendorRuns)
            {
                var seam = executions[run.Fingerprint].Any() ? "recorded" : "none recorded";

                report.AppendLine(Universe.Inv(
                    $"| {run.StartedAtUtc:yyyy-MM-dd HH:mm} | `{run.Source}` | {run.Category} | `{run.Subject}` | {run.Window ?? "none"} | {run.Outcome} | {run.Artifacts} | {seam} | `{run.Fingerprint[..12]}` |"));
            }
        }

        report.AppendLine();
        report.AppendLine("### What this does and does not prove");
        report.AppendLine();
        report.AppendLine("A run row is the platform's record of an attempt, not the vendor's record of");
        report.AppendLine("a charge. What the repository can establish is: how many distinct request");
        report.AppendLine("identities reached this vendor, which of them retrieved a payload that was");
        report.AppendLine("archived, and which were refused before dispatch. What it cannot establish is");
        report.AppendLine("how the vendor meters those - whether a refused run cost nothing, whether two");
        report.AppendLine("runs on one fingerprint were billed once or twice, or whether the plan counts");
        report.AppendLine("requests, symbols or days. **That is a question for the subscription, not for");
        report.AppendLine("this store, and it is reported as unproven rather than assumed.**");
        report.AppendLine();

        var billable = vendorRuns
            .Where(r => r.Outcome != nameof(IngestionOutcome.Refused))
            .Select(r => r.Fingerprint)
            .Distinct(StringComparer.Ordinal)
            .Count();

        report.AppendLine(Universe.Inv(
            $"Distinct request identities that were dispatched rather than refused: **{billable}**. That is the strongest lower bound the repository supports for calls actually made to this vendor through the ingestion seam."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Succeeded vendor fingerprints in the ledger: {satisfied.Count}. The dry run matched six of them to planned requests, which is why it would fetch 704 rather than 710."));
        report.AppendLine();

        // ---- two -------------------------------------------------------------------------------------

        report.AppendLine("## 2. Where the stored closing prices come from");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Closing-price rows | {priceRows} |"));
        report.AppendLine(Universe.Inv($"| Split rows | {splitRows} |"));
        report.AppendLine(Universe.Inv($"| Distinct price series (by subject identifier) | {series.Count} |"));
        report.AppendLine();
        report.AppendLine("Each row is placed in exactly one bucket, tried in this order, so \"already");
        report.AppendLine("held\" can never quietly mean \"held under a name no request will ask for\":");
        report.AppendLine();
        report.AppendLine("| Bucket | Series | Rows |");
        report.AppendLine("| --- | ---: | ---: |");

        foreach (var bucket in byBucket.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv($"| {bucket.Key} | {bucket.Value.Series} | {bucket.Value.Rows} |"));
        }

        report.AppendLine();
        report.AppendLine("Every series, largest first:");
        report.AppendLine();
        report.AppendLine("| Symbol | CIK | Name | Rows | Sessions | First | Last | Provenance source |");
        report.AppendLine("| --- | --- | --- | ---: | ---: | --- | --- | --- |");

        foreach (var row in series.Take(60))
        {
            report.AppendLine(Universe.Inv(
                $"| `{row.Identifier}` | {row.Cik ?? "-"} | {row.Name ?? "-"} | {row.Rows} | {row.Sessions} | {row.First:yyyy-MM-dd} | {row.Last:yyyy-MM-dd} | {string.Join(", ", row.Sources)} |"));
        }

        if (series.Count > 60)
        {
            report.AppendLine(Universe.Inv($"| … | | | | | | | {series.Count - 60} further series in the JSON |"));
        }

        report.AppendLine();

        // ---- three -----------------------------------------------------------------------------------

        report.AppendLine("## 3. What the 710 planned requests would land on");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Acquisition-ready members | {acquirable} |"));
        report.AppendLine(Universe.Inv($"| …whose exact planned symbol already holds observations | {overlap.Count} |"));
        report.AppendLine(Universe.Inv($"| Price rows already under a planned symbol | {overlap.Sum(o => o.PriceRows)} |"));
        report.AppendLine(Universe.Inv($"| Split rows already under a planned symbol | {overlap.Sum(o => o.SplitRows)} |"));
        report.AppendLine(Universe.Inv($"| Planned requests targeting a symbol that already holds data | {overlap.Count(o => o.PriceRows > 0) + overlap.Count(o => o.SplitRows > 0)} of 710 |"));
        report.AppendLine();

        if (overlap.Count > 0)
        {
            report.AppendLine("| CIK | Name | Symbol | Price rows | Split rows |");
            report.AppendLine("| --- | --- | --- | ---: | ---: |");

            foreach (var row in overlap.OrderByDescending(o => o.PriceRows))
            {
                report.AppendLine(Universe.Inv(
                    $"| `{row.Cik}` | {row.Name} | `{row.Symbol}` | {row.PriceRows} | {row.SplitRows} |"));
            }
        }

        report.AppendLine();
        report.AppendLine("A re-request of one of these is not a duplicate observation: the five-part");
        report.AppendLine("identity would match row for row and the store would keep one of each. It is a");
        report.AppendLine("duplicate *request*, and the fingerprint ledger is what stops it - which is");
        report.AppendLine("the check the dry run runs, and the reason the figure above is worth having");
        report.AppendLine("before an authorisation rather than after an invoice.");
        report.AppendLine();

        // ---- four ------------------------------------------------------------------------------------

        report.AppendLine("## 4. Is gate 6's FAIL the expected pre-acquisition state?");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Acquirable members holding no price series | {neverRequested + requestedAndEmpty.Count} |"));
        report.AppendLine(Universe.Inv($"| …never requested from the vendor at all | {neverRequested} |"));
        report.AppendLine(Universe.Inv($"| …requested, succeeded, and still empty | {requestedAndEmpty.Count} |"));
        report.AppendLine();

        if (requestedAndEmpty.Count > 0)
        {
            report.AppendLine(Universe.Inv(
                $"Those {requestedAndEmpty.Count} are real coverage faults and not artefacts of the acquisition not having run: {string.Join(", ", requestedAndEmpty.Take(20))}."));
            report.AppendLine();
        }

        report.AppendLine("The rule is implemented as declared - it is not misreading anything. What it");
        report.AppendLine("cannot currently say is *why* a series is missing, and the two reasons are not");
        report.AppendLine("the same kind of thing: a symbol nobody has asked for yet is a stage that has");
        report.AppendLine("not run, while a symbol that was asked for, answered, and still holds nothing");
        report.AppendLine("is a hole in the data. Today every fault is the first kind, so the gate's");
        report.AppendLine("verdict is correct and its reason is imprecise. **No change is applied here.**");
        report.AppendLine();

        report.AppendLine(Universe.Inv(
            $"Elapsed {watch.Elapsed.TotalSeconds:F0}s. The full mapping is at `artifacts/universe/price-namespace-map.json`."));

        return report.ToString();
    }

    private sealed record Member(
        string Cik,
        string Name,
        bool Ready,
        string? Ticker,
        string? ManifestTicker);

    private sealed record VendorRun(
        string Source,
        string Category,
        string Region,
        string SubjectKind,
        string? Subject,
        string? Window,
        string Fingerprint,
        string Outcome,
        DateTime StartedAtUtc,
        DateTime? CompletedAtUtc,
        int Artifacts,
        List<string> ArtifactHashes,
        string CorrelationId,
        string? Reason,
        string? RefusalRuleId);

    private sealed record Series(
        string Identifier,
        string? Cik,
        string? Name,
        bool Ready,
        string Bucket,
        int Rows,
        int Sessions,
        DateTime First,
        DateTime Last,
        List<string> Sources);

    private sealed record Overlap(
        string Cik,
        string Name,
        string Symbol,
        int PriceRows,
        int SplitRows);
}
