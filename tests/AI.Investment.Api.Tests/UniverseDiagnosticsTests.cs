using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Two questions the sealed manifest raised, answered from what is already held.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and nothing about that is incidental.</strong> No provider is called, no row
/// is written, updated or deleted, no payload is re-normalised into the store, and neither the
/// manifest nor the strategy register is opened for writing. The observation, run, quarantine and
/// opportunity counts are asserted unchanged at the end, which is the only claim of that kind worth
/// making - one that would fail if it were untrue.
/// </para>
/// <para>
/// <strong>Question one: where do 1,196 duplicate facts come from?</strong> Gate 12 counts distinct
/// facts against stored rows and found 649,597 against 650,793. A duplicate is a real defect or an
/// artefact of history, and those need entirely different responses, so this classifies every
/// duplicate group by <em>when its rows were retrieved</em>: two rows retrieved at the same instant
/// came out of one normalisation of one document; two rows retrieved at different instants came
/// from two fetches; and rows retrieved before this stage began belong to the twenty-company pilot
/// rather than to the four hundred.
/// </para>
/// <para>
/// <strong>Question two: can the seventeen dead companies be priced?</strong> Every one of them is
/// also unmatched, because EDGAR removes a company's ticker from its submissions document once it
/// deregisters. This reports, per company, whether a ticker is evidenced <em>locally</em> - in an
/// archived payload, in the sealed pilot manifest, or in a repository artefact - and names the
/// source. Nothing is guessed: a company with no local evidence is reported as having none.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_DIAGNOSE=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseDiagnosticsTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_DIAGNOSE";

    private const string CorrelationPrefix = "universe-";

    /// <summary>
    /// The twenty companies of the original pilot backfill, by CIK.
    /// </summary>
    /// <remarks>
    /// Copied from <see cref="SecBackfillTests"/>'s universe rather than re-derived, because the
    /// question being asked is literally "is this row one of the ones that backfill wrote".
    /// </remarks>
    private static readonly string[] PilotCiks =
    [
        "0000320193", "0000789019", "0001652044", "0001018724", "0001045810",
        "0001326801", "0001318605", "0000019617", "0001403161", "0000200406",
        "0000104169", "0000080424", "0002115436", "0000731766", "0000060695",
        "0001141391", "0000021344", "0000077476", "0000093410", "0000310158",
    ];

    /// <summary>
    /// Ticker evidence this repository already holds, and where it holds it.
    /// </summary>
    /// <remarks>
    /// One entry, and it is worth being explicit about why there is only one. This table is not a
    /// mapping source; it is a record of the places a mapping could have been found. The delisted
    /// probe named its instrument in the test and in the report it wrote, so that one symbol is
    /// evidenced locally. No other dead name in this universe has ever been named by anything in
    /// this repository, and inventing the other sixteen from memory would be exactly the
    /// unverifiable step this whole design exists to avoid.
    /// </remarks>
    private static readonly (string Cik, string Ticker, string Source)[] LocalTickerEvidence =
    [
        ("0000718877", "ATVI.US",
            "tests/AI.Investment.Api.Tests/DelistedHistoryProbeTests.cs (Symbol constant) and " +
            "artifacts/verify/delisted.md, which recorded 20 sessions returned for it"),
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseDiagnosticsTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_duplicates_and_the_dead_names_are_both_accounted_for()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Diagnostics are off. Set {GateVariable}=1 to run them. They call no provider and " +
            "write nothing.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();
        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();

        var report = new StringBuilder();

        report.AppendLine("# Diagnostics - the duplicates, and the dead names");
        report.AppendLine();
        report.AppendLine("**Read-only.** No provider call. No row written, updated or deleted.");
        report.AppendLine("No manifest, declaration or roster modified.");
        report.AppendLine();

        var inspected = new List<string>();

        await DuplicatesAsync(context, archive, report, inspected);
        await DropoutsAsync(context, archive, report, inspected);

        report.AppendLine();
        report.AppendLine("## Everything this run opened");
        report.AppendLine();

        foreach (var item in inspected)
        {
            report.AppendLine("- " + item);
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-diagnostics.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- nothing moved ------------------------------------------------------------------

        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(
            quarantineBefore,
            await context.QuarantinedPayloads.AsNoTracking().CountAsync());
        Assert.Equal(opportunitiesBefore, await context.Opportunities.AsNoTracking().CountAsync());
    }

    // ---- question one: the duplicates ---------------------------------------------------------

    private static async Task DuplicatesAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        StringBuilder report,
        List<string> inspected)
    {
        report.AppendLine("## 1. Where the duplicate facts come from");
        report.AppendLine();

        // When this stage's first request was made. Everything retrieved before it belongs to an
        // earlier era of this store, which is the difference between a defect in the new
        // acquisition and a fact about the old one.
        var runs = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var stageRuns = runs
            .Where(r => r.Request.CorrelationId.Value
                .StartsWith(CorrelationPrefix, StringComparison.Ordinal))
            .ToList();

        var stageStart = stageRuns.Count == 0
            ? DateTime.MaxValue
            : stageRuns.Min(r => r.StartedAtUtc);

        inspected.Add(Universe.Inv(
            $"ingestion_runs: {runs.Count} rows read, of which {stageRuns.Count} carry this ") +
            "stage's correlation prefix");

        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var total = await facts.CountAsync();

        // The duplicate key IS the manifest's stated identity for an observation: subject,
        // attribute, period end, publication instant and canonical value. Retrieval time is
        // deliberately NOT part of it - two rows that differ only in when they were fetched are
        // still the same fact recorded twice, which is the thing gate 12 refuses.
        var groups = await facts
            .GroupBy(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .Select(g => new
            {
                g.Key.Cik,
                g.Key.Attribute,
                g.Key.AsOfUtc,
                g.Key.PublishedAtUtc,
                Rows = g.Count(),
                First = g.Min(o => o.Provenance.RetrievedAtUtc),
                Last = g.Max(o => o.Provenance.RetrievedAtUtc),
            })
            .Where(g => g.Rows > 1)
            .ToListAsync();

        var extra = groups.Sum(g => g.Rows - 1);
        var pilot = PilotCiks.ToHashSet(StringComparer.Ordinal);

        var legacy = groups.Where(g => g.Last < stageStart).ToList();
        var straddling = groups.Where(g => g.First < stageStart && g.Last >= stageStart).ToList();
        var thisStage = groups.Where(g => g.First >= stageStart).ToList();

        var oneRetrieval = thisStage.Where(g => g.First == g.Last).ToList();
        var manyRetrievals = thisStage.Where(g => g.First != g.Last).ToList();

        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Stored financial rows | {total} |"));
        report.AppendLine(Universe.Inv($"| Duplicate groups | {groups.Count} |"));
        report.AppendLine(Universe.Inv($"| **Rows above one per fact** | **{extra}** |"));
        report.AppendLine(Universe.Inv($"| Companies affected | {groups.Select(g => g.Cik).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine(Universe.Inv($"| This stage's first request | {stageStart:yyyy-MM-dd HH:mm:ss}Z |"));
        report.AppendLine();

        report.AppendLine("### By when the duplicated rows were retrieved");
        report.AppendLine();
        report.AppendLine("| Category | Groups | Extra rows | What it means |");
        report.AppendLine("| --- | ---: | ---: | --- |");
        report.AppendLine(Universe.Inv(
            $"| Wholly before this stage | {legacy.Count} | {legacy.Sum(g => g.Rows - 1)} | ") +
            "legacy: written by the twenty-company pilot, before content-hash de-duplication |");
        report.AppendLine(Universe.Inv(
            $"| Straddling this stage | {straddling.Count} | {straddling.Sum(g => g.Rows - 1)} | ") +
            "a pilot row and a new row for the same fact: the skip check failed to prevent a refetch |");
        report.AppendLine(Universe.Inv(
            $"| This stage, one retrieval instant | {oneRetrieval.Count} | {oneRetrieval.Sum(g => g.Rows - 1)} | ") +
            "**one document emitted the same fact twice** |");
        report.AppendLine(Universe.Inv(
            $"| This stage, several retrieval instants | {manyRetrievals.Count} | {manyRetrievals.Sum(g => g.Rows - 1)} | ") +
            "two snapshots of one company both emitted it |");
        report.AppendLine();

        report.AppendLine("### By which universe the company belongs to");
        report.AppendLine();

        var pilotGroups = groups.Where(g => pilot.Contains(g.Cik)).ToList();

        report.AppendLine(Universe.Inv(
            $"- of the twenty pilot companies: **{pilotGroups.Count}** groups, ") +
            Universe.Inv($"{pilotGroups.Sum(g => g.Rows - 1)} extra rows"));
        report.AppendLine(Universe.Inv(
            $"- of the other {groups.Select(g => g.Cik).Distinct(StringComparer.Ordinal).Count(c => !pilot.Contains(c))} ") +
            Universe.Inv($"companies: {groups.Count - pilotGroups.Count} groups, ") +
            Universe.Inv($"{groups.Sum(g => g.Rows - 1) - pilotGroups.Sum(g => g.Rows - 1)} extra rows"));
        report.AppendLine();

        report.AppendLine("### The most affected companies");
        report.AppendLine();
        report.AppendLine("| CIK | Pilot | Groups | Extra rows | Attributes involved |");
        report.AppendLine("| --- | --- | ---: | ---: | --- |");

        var worst = groups
            .GroupBy(g => g.Cik, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(x => x.Rows - 1))
            .Take(15)
            .ToList();

        foreach (var company in worst)
        {
            var attributes = string.Join(
                ", ",
                company.Select(g => g.Attribute).Distinct(StringComparer.Ordinal).Take(4));

            report.AppendLine(Universe.Inv(
                $"| `{company.Key}` | {(pilot.Contains(company.Key) ? "yes" : "no")} | {company.Count()} | {company.Sum(x => x.Rows - 1)} | {attributes} |"));
        }

        report.AppendLine();

        if (worst.Count > 0)
        {
            var sample = worst[0].OrderByDescending(g => g.Rows).First();

            await ExplainFromPayloadAsync(
                context,
                archive,
                report,
                inspected,
                worst[0].Key,
                sample.Attribute,
                sample.AsOfUtc,
                sample.PublishedAtUtc);
        }
    }

    /// <summary>
    /// Re-reads one archived document and shows the raw entries that produced a duplicate.
    /// </summary>
    /// <remarks>
    /// A classification by timestamp says <em>when</em> the rows were written. Only the document
    /// says <em>why</em>, and it is already in the archive - so the answer costs a read rather than
    /// a request. Nothing is normalised, nothing is stored: the payload is parsed here and dropped.
    /// </remarks>
    private static async Task ExplainFromPayloadAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        StringBuilder report,
        List<string> inspected,
        string cik,
        string attribute,
        DateTime asOfUtc,
        DateTime publishedAtUtc)
    {
        report.AppendLine("### What the archived document itself shows");
        report.AppendLine();

        var run = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.FinancialStatements)
                .ToListAsync())
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .LastOrDefault(r => string.Equals(
                r.Request.Subject.Identifier,
                cik,
                StringComparison.Ordinal));

        if (run is null)
        {
            report.AppendLine(Universe.Inv(
                $"No completed company-facts run is stored for `{cik}`, so the document that ") +
                "produced these rows cannot be re-read. Recorded rather than assumed.");

            return;
        }

        byte[]? payload = null;

        foreach (var hash in run.Artifacts)
        {
            payload = await archive.RetrieveAsync(hash);

            if (payload is not null)
            {
                inspected.Add(Universe.Inv(
                    $"archived companyfacts payload for CIK {cik}, content hash {hash.Value}, ") +
                    Universe.Inv($"{payload.Length} bytes - parsed in memory, not normalised"));

                break;
            }
        }

        if (payload is null)
        {
            report.AppendLine(Universe.Inv(
                $"The run for `{cik}` is recorded but its payload is no longer in the archive."));

            return;
        }

        using var document = JsonDocument.Parse(payload);

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("facts", out var facts) ||
            !facts.TryGetProperty("us-gaap", out var taxonomy))
        {
            report.AppendLine("The archived document has no `facts.us-gaap` section to read.");

            return;
        }

        var repeated = new List<string>();
        var scanned = 0;

        foreach (var tag in taxonomy.EnumerateObject())
        {
            if (!tag.Value.TryGetProperty("units", out var units))
            {
                continue;
            }

            foreach (var unit in units.EnumerateObject())
            {
                var seen = new Dictionary<string, List<string>>(StringComparer.Ordinal);

                foreach (var entry in unit.Value.EnumerateArray())
                {
                    scanned++;

                    var key = string.Join(
                        '|',
                        Text(entry, "start"),
                        Text(entry, "end"),
                        Text(entry, "filed"),
                        Text(entry, "val"));

                    var described = Universe.Inv(
                        $"accn {Text(entry, "accn")}, form {Text(entry, "form")}, ") +
                        Universe.Inv($"fy {Text(entry, "fy")}{Text(entry, "fp")}, frame {Text(entry, "frame")}");

                    if (!seen.TryGetValue(key, out var occurrences))
                    {
                        occurrences = [];
                        seen[key] = occurrences;
                    }

                    occurrences.Add(described);
                }

                foreach (var pair in seen.Where(p => p.Value.Count > 1).Take(3))
                {
                    repeated.Add(Universe.Inv(
                        $"`us-gaap/{tag.Name}` in `{unit.Name}`: {pair.Value.Count} entries share ") +
                        Universe.Inv($"(start, end, filed, val) = ({pair.Key}) - {string.Join(" | ", pair.Value)}"));
                }
            }
        }

        report.AppendLine(Universe.Inv(
            $"The document for `{cik}` holds {scanned} raw fact entries. A duplicate observation ") +
            "arises when two of them agree on period, filing date and value while differing in " +
            "something the platform does not record - the accession number, the form, or the " +
            "fiscal period label.");
        report.AppendLine();

        if (repeated.Count == 0)
        {
            report.AppendLine("No entry in this document repeats a (start, end, filed, val)");
            report.AppendLine("combination, so the duplication did not come from the document.");
            report.AppendLine("That points at the rows having been written twice rather than read");
            report.AppendLine("twice, which the retrieval-time classification above distinguishes.");
            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"The duplicated observation checked was `{attribute}` for the period ending ") +
                Universe.Inv($"{asOfUtc:yyyy-MM-dd}, published {publishedAtUtc:yyyy-MM-dd}."));

            return;
        }

        report.AppendLine(Universe.Inv($"{repeated.Count} such repeats were found. The first few:"));
        report.AppendLine();

        foreach (var line in repeated.Take(8))
        {
            report.AppendLine("- " + line);
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"One duplicated observation for this company was `{attribute}` for the period ") +
            Universe.Inv($"ending {asOfUtc:yyyy-MM-dd}, published {publishedAtUtc:yyyy-MM-dd}."));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.GetRawText(),
            }
            : string.Empty;

    // ---- question two: the dead names ---------------------------------------------------------

    private static async Task DropoutsAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        StringBuilder report,
        List<string> inspected)
    {
        report.AppendLine();
        report.AppendLine("## 2. The seventeen companies that left, and whether they can be priced");
        report.AppendLine();

        var path = Universe.ManifestPath;

        if (!File.Exists(path))
        {
            report.AppendLine("The sealed manifest is not on disk, so there is no dropout list to read.");

            return;
        }

        inspected.Add(Universe.ManifestRelativePath + " - read, not modified");

        var manifest = JsonSerializer.Deserialize<ManifestFile>(
            await File.ReadAllTextAsync(path),
            Universe.Json);

        if (manifest?.Members is null)
        {
            report.AppendLine("The sealed manifest could not be read as a manifest.");

            return;
        }

        var dropouts = manifest.Members
            .Where(m => m.StoppedFiling || m.AbsentFromFinalCrossSection)
            .OrderBy(m => m.LastFilingUtc ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var evidence = LocalTickerEvidence.ToDictionary(
            e => e.Cik,
            e => (e.Ticker, e.Source),
            StringComparer.Ordinal);

        var pilotManifest = Universe.RepositoryPath(
            "declarations", "universe-pilot-2021-2026.json");

        var pilotTickers = await PilotTickersAsync(pilotManifest, inspected);

        report.AppendLine("| CIK | Company | Last filing | Ticker in the manifest | Ticker evidenced locally | Evidence source | Status |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        var evidenced = 0;

        foreach (var member in dropouts)
        {
            var archived = await SubmissionsTickerAsync(context, archive, member.Cik, inspected);

            string? found = null;
            string source;

            if (archived is not null)
            {
                found = archived;
                source = "the archived EDGAR submissions payload for this CIK still carries it";
            }
            else if (evidence.TryGetValue(member.Cik, out var known))
            {
                found = known.Ticker;
                source = known.Source;
            }
            else if (pilotTickers.TryGetValue(member.Cik, out var pilotTicker))
            {
                found = pilotTicker;
                source = "declarations/universe-pilot-2021-2026.json, the sealed pilot manifest";
            }
            else
            {
                source = "none - no archived payload, manifest or repository artefact names one";
            }

            if (found is not null)
            {
                evidenced++;
            }

            report.AppendLine(Universe.Inv(
                $"| `{member.Cik}` | {member.Name ?? "-"} | {member.LastFilingUtc ?? "-"} | {member.Ticker ?? "none"} | {found ?? "**none**"} | {source} | {(found is null ? "**unpriceable from local evidence**" : "evidenced")} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"**{evidenced} of {dropouts.Count}** dropouts have a ticker evidenced locally. ") +
            Universe.Inv($"{dropouts.Count - evidenced} do not."));
        report.AppendLine();
        report.AppendLine("EDGAR removes the `tickers` array from a company's submissions document");
        report.AppendLine("once it deregisters, and the platform has never held a historical");
        report.AppendLine("CIK-to-ticker mapping for these names. The EODHD delisted symbol list");
        report.AppendLine("read during the manifest stage was used for a membership test and");
        report.AppendLine("discarded rather than stored, so it cannot be consulted now without");
        report.AppendLine("spending a call nobody has authorised.");
    }

    /// <summary>The ticker an archived submissions document still carries, if it carries one.</summary>
    private static async Task<string?> SubmissionsTickerAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        string cik,
        List<string> inspected)
    {
        var run = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.CompanyProfile)
                .ToListAsync())
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .LastOrDefault(r => string.Equals(
                r.Request.Subject.Identifier,
                cik,
                StringComparison.Ordinal));

        if (run is null)
        {
            return null;
        }

        foreach (var hash in run.Artifacts)
        {
            var payload = await archive.RetrieveAsync(hash);

            if (payload is null)
            {
                continue;
            }

            inspected.Add(Universe.Inv(
                $"archived EDGAR submissions payload for CIK {cik}, content hash {hash.Value}"));

            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("tickers", out var tickers) &&
                tickers.ValueKind == JsonValueKind.Array)
            {
                foreach (var ticker in tickers.EnumerateArray())
                {
                    if (ticker.ValueKind == JsonValueKind.String &&
                        ticker.GetString() is { Length: > 0 } value)
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        return null;
    }

    private static async Task<Dictionary<string, string>> PilotTickersAsync(
        string path,
        List<string> inspected)
    {
        var tickers = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            return tickers;
        }

        inspected.Add("declarations/universe-pilot-2021-2026.json - read, not modified");

        var pilot = JsonSerializer.Deserialize<ManifestFile>(
            await File.ReadAllTextAsync(path),
            Universe.Json);

        foreach (var member in pilot?.Members ?? [])
        {
            if (member.Ticker is { Length: > 0 } ticker)
            {
                tickers[member.Cik] = ticker;
            }
        }

        return tickers;
    }

    /// <summary>Only the fields this diagnostic reads. Extra members in the file are ignored.</summary>
    private sealed record ManifestMember(
        string Cik,
        string? Ticker,
        string? Name,
        string? LastFilingUtc,
        bool StoppedFiling,
        bool AbsentFromFinalCrossSection);

    private sealed record ManifestFile(List<ManifestMember>? Members);
}
