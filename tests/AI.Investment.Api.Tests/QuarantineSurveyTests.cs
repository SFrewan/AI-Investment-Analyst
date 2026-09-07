using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Every quarantined payload this platform holds, opened and classified. Nothing is changed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and emphatically so.</strong> No provider is called, no connector is
/// enabled, no payload is reprocessed, reclassified, persisted or removed, and the normaliser is
/// not touched. Run, observation and quarantine counts are asserted unchanged either side, and the
/// sealed manifest and identity table are checked against their approved values.
/// </para>
/// <para>
/// <strong>Why this exists.</strong> A partial-acceptance policy was proposed on the strength of
/// three payloads that all failed on their last row. Three is not an evidence base; it is a
/// coincidence with a hypothesis attached. This opens every quarantined payload in the store and
/// asks the same questions of each, so the policy question is settled by what the vendor actually
/// sends rather than by the three examples that happened to arrive first.
/// </para>
/// <para>
/// The row checks below are a deliberate second implementation of the normaliser's own, written to
/// read and never to write. Re-running the real normaliser would be reprocessing a quarantined
/// payload, which this stage must not do.
/// </para>
/// </remarks>
public sealed class QuarantineSurveyTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_QUARANTINE_SURVEY";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const int SealedBytes = 245126;

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public QuarantineSurveyTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_quarantined_payload_is_opened_and_classified_without_changing_any()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The quarantine survey is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        var quarantined = await context.QuarantinedPayloads
            .AsNoTracking()
            .OrderBy(q => q.QuarantinedAtUtc)
            .ToListAsync();

        // Which symbols each payload was fetched for. A body is stored once however many runs
        // retrieved it, so this is one-to-many by construction.
        var runs = await context.IngestionRuns.AsNoTracking().ToListAsync();

        // Keyed by hash AND source, which is the correction. A content hash names bytes and nothing
        // else, so an empty JSON array retrieved from the splits endpoint has the same hash as one
        // retrieved from the prices endpoint. Grouping by hash alone therefore listed symbols under
        // a quarantine record belonging to a different source - and for the shared empty body, the
        // overwhelming majority of the symbols shown had never been quarantined at all. Nothing in
        // the store changes here: this reads the same rows and attributes them correctly.
        var symbolsFor = runs
            .SelectMany(r => r.Artifacts.Select(h => (
                Hash: h.Value,
                Source: r.Request.SourceId.Value,
                Symbol: r.Request.Subject.Identifier ?? "(none)")))
            .GroupBy(x => Key(x.Hash, x.Source), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        // The runs that archived the same bytes under a DIFFERENT source. Kept rather than dropped:
        // that these bodies are shared is a real fact about the archive, and losing it would trade
        // one misleading report for a merely incomplete one. It is reported as what it is - runs
        // that happen to share a payload, not runs that were quarantined.
        var sharedWith = runs
            .SelectMany(r => r.Artifacts.Select(h => (
                Hash: h.Value,
                Source: r.Request.SourceId.Value,
                Symbol: r.Request.Subject.Identifier ?? "(none)")))
            .GroupBy(x => x.Hash, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.Source, StringComparer.Ordinal)
                    .Select(bySource => (
                        Source: bySource.Key,
                        Runs: bySource.Count(),
                        Symbols: bySource.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Count()))
                    .OrderBy(x => x.Source, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        report.AppendLine("# Quarantine survey: every payload the platform refused to read");
        report.AppendLine();
        report.AppendLine("**Read-only.** No provider was called, nothing was reprocessed,");
        report.AppendLine("reclassified, persisted or removed, and no production code was changed.");
        report.AppendLine();

        var surveyed = new List<Survey>();

        foreach (var row in quarantined)
        {
            var bytes = await archive.RetrieveAsync(row.Id);

            var symbols = symbolsFor.TryGetValue(Key(row.Id.Value, row.SourceId.Value), out var s)
                ? s
                : ["(no run from this source claims this hash)"];

            var shared = sharedWith.TryGetValue(row.Id.Value, out var all)
                ? all.Where(x => !string.Equals(x.Source, row.SourceId.Value, StringComparison.Ordinal)).ToList()
                : [];

            surveyed.Add(new Survey(
                row.Id.Abbreviated,
                string.Join(" ", symbols),
                row.SourceId.Value,
                row.Category.ToString(),
                row.RuleId,
                row.QuarantinedAtUtc,
                bytes?.Length ?? -1,
                Analyse(bytes),
                row.Reason,
                shared.Count == 0
                    ? "-"
                    : string.Join(", ", shared.Select(x => Universe.Inv($"{x.Source}: {x.Runs} run(s), {x.Symbols} symbol(s)")))));
        }

        // ---- the table ------------------------------------------------------------------------------

        report.AppendLine(Universe.Inv($"## All {surveyed.Count} payloads"));
        report.AppendLine();
        report.AppendLine("| # | Symbols | Source | Rule | Bytes | Class | Rows | Read | Bad | First bad | All bad terminal | Valid before | Valid after | Readable span |");
        report.AppendLine("| ---: | --- | --- | --- | ---: | --- | ---: | ---: | ---: | --- | --- | --- | --- | --- |");

        var index = 0;

        foreach (var row in surveyed)
        {
            index++;

            var a = row.Analysis;

            report.AppendLine(Universe.Inv(
                $"| {index} | `{row.Symbols}` | `{row.Source}` | `{row.Rule}` | {(row.Bytes < 0 ? "absent" : row.Bytes.ToString(CultureInfo.InvariantCulture))} | {a.Classification} | {Count(a.Rows)} | {Count(a.Readable)} | {Count(a.Unreadable)} | {a.FirstBad ?? "-"} | {Flag(a.AllBadTerminal)} | {Flag(a.ValidBefore)} | {Flag(a.ValidAfter)} | {a.Span} |"));
        }

        report.AppendLine();

        // ---- payloads whose bytes other sources also hold ---------------------------------------------

        var sharedRows = surveyed
            .Where(r => !string.Equals(r.SharedWith, "-", StringComparison.Ordinal))
            .ToList();

        report.AppendLine("## Payloads whose bytes another source also archived");
        report.AppendLine();
        report.AppendLine("The archive is content-addressed, so identical bodies from different");
        report.AppendLine("endpoints are one payload. Runs listed here are NOT quarantined - they");
        report.AppendLine("retrieved the same bytes and were read successfully by their own");
        report.AppendLine("normaliser. They are named so that a shared body is visible rather than");
        report.AppendLine("silently attributed to whichever source happened to fail on it first.");
        report.AppendLine();

        if (sharedRows.Count == 0)
        {
            report.AppendLine("None.");
        }
        else
        {
            report.AppendLine("| Payload | Quarantined under | Also archived by |");
            report.AppendLine("| --- | --- | --- |");

            foreach (var row in sharedRows)
            {
                report.AppendLine(Universe.Inv($"| `{row.Hash}` | `{row.Source}` | {row.SharedWith} |"));
            }
        }

        report.AppendLine();

        // ---- aggregates ------------------------------------------------------------------------------

        report.AppendLine("## Aggregate");
        report.AppendLine();
        report.AppendLine("| Classification | Payloads | Symbols affected |");
        report.AppendLine("| --- | ---: | ---: |");

        foreach (var group in surveyed
            .GroupBy(r => r.Analysis.Classification, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv(
                $"| {group.Key} | {group.Count()} | {group.Sum(r => r.Symbols.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)} |"));
        }

        report.AppendLine();
        report.AppendLine("| By source | Payloads |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in surveyed
            .GroupBy(r => r.Source, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| `{group.Key}` | {group.Count()} |"));
        }

        report.AppendLine();

        var rowLevel = surveyed.Where(r => r.Analysis.Rows > 0).ToList();
        var withBadRows = rowLevel.Where(r => r.Analysis.Unreadable > 0).ToList();
        var terminalOnly = withBadRows.Where(r => r.Analysis.AllBadTerminal == true).ToList();
        var interior = withBadRows.Where(r => r.Analysis.AllBadTerminal == false).ToList();

        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Payloads that parsed as a row array | {rowLevel.Count} |"));
        report.AppendLine(Universe.Inv($"| …with at least one unreadable row | {withBadRows.Count} |"));
        report.AppendLine(Universe.Inv($"| …**all bad rows terminal** | **{terminalOnly.Count}** |"));
        report.AppendLine(Universe.Inv($"| …**at least one interior bad row** | **{interior.Count}** |"));
        report.AppendLine(Universe.Inv($"| Readable rows that all-or-nothing currently discards | **{withBadRows.Sum(r => r.Analysis.Readable)}** |"));
        report.AppendLine(Universe.Inv($"| Unreadable rows in those same payloads | {withBadRows.Sum(r => r.Analysis.Unreadable)} |"));
        report.AppendLine();

        // ---- what the bad rows actually look like -------------------------------------------------------

        report.AppendLine("## What a bad row actually contains");
        report.AppendLine();
        report.AppendLine("A price row carries a date, an open, a high, a low, a close and a volume.");
        report.AppendLine("None of that is a credential, so the offending rows are shown verbatim -");
        report.AppendLine("this is the evidence for whether the problem belongs at the parser boundary");
        report.AppendLine("or is a genuine hole in the vendor's data.");
        report.AppendLine();
        report.AppendLine("| Symbols | Row | Why it failed | The row |");
        report.AppendLine("| --- | ---: | --- | --- |");

        foreach (var row in withBadRows)
        {
            foreach (var fault in row.Analysis.Faults.Take(3))
            {
                report.AppendLine(Universe.Inv(
                    $"| `{row.Symbols}` | {fault.Index} | {fault.Kind} | `{fault.Raw}` |"));
            }
        }

        report.AppendLine();
        report.AppendLine("| Distinct failure kinds | Rows |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in withBadRows
            .SelectMany(r => r.Analysis.Faults)
            .GroupBy(f => f.Kind, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| {group.Key} | {group.Count()} |"));
        }

        report.AppendLine();

        // ---- the recorded reasons, verbatim ---------------------------------------------------------------

        report.AppendLine("## Distinct recorded reasons");
        report.AppendLine();
        report.AppendLine("| Rule | Payloads | One reason, verbatim |");
        report.AppendLine("| --- | ---: | --- |");

        foreach (var group in surveyed
            .GroupBy(r => r.Rule, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv(
                $"| `{group.Key}` | {group.Count()} | {Shorten(group.First().Reason)} |"));
        }

        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "quarantine-survey.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- nothing moved -----------------------------------------------------------------------------

        var manifestBytes = await File.ReadAllBytesAsync(Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json"));

        var digest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

        Assert.Equal(SealedDigest, digest);
        Assert.True(manifestBytes.Length == SealedBytes, "the sealed manifest's length changed");

        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        var members = identity.RootElement.GetProperty("Members").EnumerateArray().ToList();

        Assert.True(members.Count == 400, "the identity table is not four hundred members");
        Assert.True(
            members.Count(m => m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True) == 355,
            "the identity table no longer holds 355 acquisition-ready members");

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());

        Assert.True(surveyed.Count > 0, "the store holds no quarantined payload to survey");
    }

    // ---- classification ---------------------------------------------------------------------------------

    /// <summary>
    /// Opens a payload and says what is wrong with it, without writing anything anywhere.
    /// </summary>
    private static Analysis Analyse(byte[]? bytes)
    {
        if (bytes is null)
        {
            return Analysis.Shape("archive miss - no bytes under this hash");
        }

        if (bytes.Length == 0)
        {
            return Analysis.Shape("**empty payload** - zero bytes");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return Analysis.Shape("**schema/format** - not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var names = document.RootElement
                    .EnumerateObject()
                    .Take(4)
                    .Select(p => p.Name)
                    .ToList();

                return Analysis.Shape(Universe.Inv(
                    $"**schema/format** - a JSON object, not an array; keys {string.Join(", ", names)}"));
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Analysis.Shape(Universe.Inv(
                    $"**schema/format** - JSON {document.RootElement.ValueKind}, not an array"));
            }

            if (document.RootElement.GetArrayLength() == 0)
            {
                return Analysis.Shape("**empty payload** - an empty JSON array");
            }

            return Rows(document.RootElement);
        }
    }

    private static Analysis Rows(JsonElement array)
    {
        var faults = new List<Fault>();
        var readableIndices = new List<int>();

        string? firstGood = null;
        string? lastGood = null;
        var index = 0;

        foreach (var element in array.EnumerateArray())
        {
            index++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                faults.Add(new Fault(index, null, "not an object", Raw(element)));
                continue;
            }

            var date = element.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            if (date is null)
            {
                faults.Add(new Fault(index, null, "no usable date", Raw(element)));
                continue;
            }

            if (!element.TryGetProperty("close", out var c))
            {
                faults.Add(new Fault(index, date, "close absent", Raw(element)));
                continue;
            }

            if (c.ValueKind == JsonValueKind.Null)
            {
                faults.Add(new Fault(index, date, "close is null", Raw(element)));
                continue;
            }

            if (c.ValueKind != JsonValueKind.Number || !c.TryGetDecimal(out var close))
            {
                faults.Add(new Fault(index, date, "close is not a number", Raw(element)));
                continue;
            }

            if (close <= 0m)
            {
                faults.Add(new Fault(index, date, "close is zero or negative", Raw(element)));
                continue;
            }

            readableIndices.Add(index);
            firstGood ??= date;
            lastGood = date;
        }

        var rows = index;
        var readable = readableIndices.Count;

        if (faults.Count == 0)
        {
            // Refused for a reason no row check reproduces - the ordering rule, or a domain refusal.
            return new Analysis(
                "**parser/normalisation** - every row reads cleanly here, so the refusal was not row shape",
                rows, readable, 0, null, null, null, null, [],
                firstGood is null ? "none" : Universe.Inv($"{firstGood}..{lastGood}"));
        }

        var firstBadIndex = faults[0].Index;
        var lastBadIndex = faults[^1].Index;

        var allBadTerminal = readable > 0 && readableIndices[^1] < firstBadIndex;
        var validBefore = readableIndices.Exists(i => i < firstBadIndex);
        var validAfter = readableIndices.Exists(i => i > lastBadIndex);

        var kinds = faults.Select(f => f.Kind).Distinct(StringComparer.Ordinal).ToList();

        var firstBad = faults[0].Date is null
            ? Universe.Inv($"row {firstBadIndex}")
            : Universe.Inv($"row {firstBadIndex} ({faults[0].Date})");

        var classification = readable == 0
            ? "**malformed throughout** - no row reads"
            : allBadTerminal
                ? "**terminal malformed row(s)**"
                : "**interior malformed row(s)**";

        if (kinds.Count == 1 && kinds[0].StartsWith("close is", StringComparison.Ordinal))
        {
            classification += " - invalid price value";
        }

        return new Analysis(
            classification,
            rows,
            readable,
            faults.Count,
            firstBad,
            allBadTerminal,
            validBefore,
            validAfter,
            faults,
            firstGood is null ? "none" : Universe.Inv($"{firstGood}..{lastGood}"));
    }

    /// <summary>One row, short enough to read and containing no credential by construction.</summary>
    private static string Raw(JsonElement element)
    {
        var text = element.GetRawText().Replace("\n", " ", StringComparison.Ordinal);

        return text.Length <= 150 ? text : text[..150] + "…";
    }

    private static string Count(int value) => value < 0 ? "-" : value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        _ => "-",
    };

    private static string Shorten(string reason) =>
        reason.Length <= 130 ? reason : reason[..130] + "…";

    private sealed record Fault(int Index, string? Date, string Kind, string Raw);

    private sealed record Analysis(
        string Classification,
        int Rows,
        int Readable,
        int Unreadable,
        string? FirstBad,
        bool? AllBadTerminal,
        bool? ValidBefore,
        bool? ValidAfter,
        List<Fault> Faults,
        string Span)
    {
        public static Analysis Shape(string classification) =>
            new(classification, -1, -1, -1, null, null, null, null, [], "-");
    }

    private sealed record Survey(
        string Hash,
        string Symbols,
        string Source,
        string Category,
        string Rule,
        DateTime QuarantinedAtUtc,
        int Bytes,
        Analysis Analysis,
        string Reason,
        string SharedWith);

    /// <summary>
    /// A payload is identified by its bytes; a quarantine is about bytes retrieved from a source.
    /// </summary>
    /// <remarks>
    /// The two are not the same key, and treating them as one is what attributed hundreds of
    /// successful <c>eodhd-splits</c> runs to a <c>eodhd-eod</c> quarantine record. The pipe is
    /// safe as a separator: a content hash is 64 hexadecimal characters and a source identifier
    /// contains no pipe.
    /// </remarks>
    private static string Key(string hash, string source) => hash + "|" + source;
}
