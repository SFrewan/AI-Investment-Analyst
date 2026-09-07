using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Every archived price payload the platform refused, replayed through the real normaliser. In
/// memory, and nothing is kept.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider, no write, no policy change.</strong> No connector is enabled and no request
/// leaves the process. Nothing is persisted, reclassified, released or removed: run, observation and
/// quarantine counts are asserted unchanged either side. The normaliser, the pipeline, Gate 6 and
/// its sealed declaration are read and not touched.
/// </para>
/// <para>
/// <strong>Why the real normaliser and not a second implementation.</strong>
/// <see cref="QuarantineSurveyTests"/> deliberately re-implemented the row checks, on the ground
/// that "re-running the real normaliser would be reprocessing a quarantined payload". That caution
/// was about the <em>pipeline</em>, which stores observations and writes quarantine records.
/// <see cref="EodhdDailyPriceNormalizer.NormalizeAsync"/> is a pure function of bytes: it holds no
/// store, no gateway and no quarantine writer, and returns a result rather than causing one. Calling
/// it directly cannot reprocess anything, and it is the only way to answer what the production code
/// actually does with each row rather than what a copy of it would.
/// </para>
/// <para>
/// <strong>How every rejected row is found without changing the normaliser.</strong> The normaliser
/// fails fast: the first unreadable row returns a quarantine naming that row, and the rows after it
/// are never examined. So each payload is replayed repeatedly - the named row is removed from the
/// array in memory, the reduced array is replayed again, and the loop continues until the normaliser
/// accepts what is left. Every rejection is therefore the production code's own verdict on that row,
/// and the final accepting replay is the direct answer to what would be recoverable if invalid rows
/// were isolated instead of condemning the payload. The reduced arrays exist only as local
/// variables; the archive is opened read-only and never written.
/// </para>
/// </remarks>
public sealed class PricePayloadRereadTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_PAYLOAD_REREAD";

    private const string PriceSource = "eodhd-eod";

    /// <summary>A guard on the peel loop, so a reason this cannot parse cannot spin.</summary>
    private const int MaxPeels = 200;

    private static readonly Regex RowIndex = new(
        @"^Row (\d+):", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PricePayloadRereadTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Archived_price_payloads_are_replayed_in_memory_and_nothing_is_written()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The price payload re-read is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        // The production normaliser, resolved from the container exactly as the pipeline resolves
        // it - same type, same options, same configured exchange sessions.
        var normalizer = services.GetServices<INormalizer>()
            .OfType<EodhdDailyPriceNormalizer>()
            .Single();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        var everyQuarantine = await context.QuarantinedPayloads
            .AsNoTracking()
            .OrderBy(q => q.QuarantinedAtUtc)
            .ToListAsync();

        // Filtered in memory rather than in the query, the way the survey does it: the source is a
        // value object and translating it would be relying on a mapping this stage should not assume.
        var quarantined = everyQuarantine
            .Where(q => string.Equals(q.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .ToList();

        var runs = await context.IngestionRuns.AsNoTracking().ToListAsync();

        // Keyed by hash AND source, for the reason the survey records: a content hash names bytes,
        // and the same empty array arrives from two endpoints.
        var priceRuns = runs
            .Where(r => string.Equals(r.Request.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .SelectMany(r => r.Artifacts.Select(h => (Hash: h.Value, Run: r)))
            .GroupBy(x => x.Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Run).ToList(), StringComparer.Ordinal);

        var replays = new List<Replay>();

        foreach (var row in quarantined)
        {
            var bytes = await archive.RetrieveAsync(row.Id);

            if (!priceRuns.TryGetValue(row.Id.Value, out var claimants) || claimants.Count == 0)
            {
                replays.Add(Replay.Unclaimed(row.Id.Abbreviated, row.RuleId, bytes?.Length ?? -1));

                continue;
            }

            var symbols = claimants
                .Select(r => r.Request.Subject.Identifier ?? "(none)")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (bytes is null)
            {
                replays.Add(Replay.Absent(row.Id.Abbreviated, string.Join(" ", symbols), row.RuleId));

                continue;
            }

            // One replay per symbol that fetched these bytes. The normaliser's verdict depends on
            // the subject - it resolves the exchange session from the symbol - so a shared payload
            // has to be asked about each claimant rather than once.
            foreach (var run in claimants
                .GroupBy(r => r.Request.Subject.Identifier ?? "(none)", StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(r => r.Request.Subject.Identifier, StringComparer.Ordinal))
            {
                var request = run.Request;
                var retrievedAtUtc = run.StartedAtUtc;
                var hash = row.Id;

                replays.Add(await ReplayAsync(
                    normalizer,
                    request.Subject.Identifier ?? "(none)",
                    row.Id.Abbreviated,
                    payload => new NormalizationInput(
                        request.SourceId,
                        request.Category,
                        request.Subject,
                        hash,
                        payload,
                        retrievedAtUtc),
                    bytes));
            }
        }

        // ================================================================================
        // The report
        // ================================================================================

        report.AppendLine("# Archived price payloads, replayed through the real normaliser");
        report.AppendLine();
        report.AppendLine("**In-memory and read-only.** No provider was called, no connector enabled,");
        report.AppendLine("nothing persisted, reclassified, released or removed. The normaliser, the");
        report.AppendLine("pipeline and Gate 6 are unchanged. Every verdict below is the production");
        report.AppendLine("normaliser's own, obtained by calling it on bytes and reading what it returned.");
        report.AppendLine();

        report.AppendLine("## Cross-payload summary");
        report.AppendLine();
        report.AppendLine("| Symbol | Payload | Bytes | Raw rows | Accepted | Rejected | close=0 | First accepted | Last accepted | Zero-row position | Contiguous after isolation? |");
        report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |");

        foreach (var r in replays)
        {
            report.AppendLine(Universe.Inv($"| `{r.Symbol}` | `{r.Payload}` | {r.Bytes} | {r.RawRows} | **{r.Accepted}** | {r.Rejected} | {r.ZeroClose} | {r.FirstAccepted} | {r.LastAccepted} | {r.ZeroPosition} | {r.Contiguity} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Payloads replayed: **{replays.Count}**. Total rows accepted across all of them: **{replays.Sum(r => r.Accepted)}**."));
        report.AppendLine();

        report.AppendLine("## Per payload");
        report.AppendLine();

        foreach (var r in replays)
        {
            report.AppendLine(Universe.Inv($"### `{r.Symbol}` — payload `{r.Payload}`"));
            report.AppendLine();
            report.AppendLine("| | |");
            report.AppendLine("| --- | --- |");
            report.AppendLine(Universe.Inv($"| Raw payload size | {r.Bytes} bytes |"));
            report.AppendLine(Universe.Inv($"| Raw rows in the array | {r.RawRows} |"));
            report.AppendLine(Universe.Inv($"| Verdict, payload as archived | `{r.FirstVerdictRule}` — {r.FirstVerdictReason} |"));
            report.AppendLine(Universe.Inv($"| Rows the normaliser accepts once invalid rows are isolated | **{r.Accepted}** |"));
            report.AppendLine(Universe.Inv($"| Rows the normaliser rejects | **{r.Rejected}** |"));
            report.AppendLine(Universe.Inv($"| Accepted date range | {r.FirstAccepted} .. {r.LastAccepted} |"));
            report.AppendLine(Universe.Inv($"| Unique accepted trading dates | {r.UniqueAccepted} |"));
            report.AppendLine(Universe.Inv($"| Duplicate dates among accepted rows | {r.Duplicates} |"));
            report.AppendLine(Universe.Inv($"| Rows carrying close = 0 | {r.ZeroClose} ({r.ZeroPosition}) |"));
            report.AppendLine(Universe.Inv($"| Interior gaps over the Gate 6 tolerance, accepted rows only | {r.GapsOverTolerance} (largest {r.LargestGap} session(s)) |"));
            report.AppendLine(Universe.Inv($"| Would every accepted row become a PriceObservation? | {r.WouldObserve} |"));
            report.AppendLine(Universe.Inv($"| Other normalisation warnings | {r.OtherNotes} |"));
            report.AppendLine();

            if (r.Rejections.Count > 0)
            {
                report.AppendLine("Rejected rows, verbatim and unmodified, each with the production");
                report.AppendLine("normaliser's own reason for refusing it:");
                report.AppendLine();
                report.AppendLine("| Raw row # | Date | close | The row | The normaliser's reason |");
                report.AppendLine("| ---: | --- | --- | --- | --- |");

                foreach (var bad in r.Rejections)
                {
                    report.AppendLine(Universe.Inv($"| {bad.RawIndex} | {bad.Date} | {bad.Close} | `{bad.Row}` | {bad.Reason} |"));
                }

                report.AppendLine();
            }
        }

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "gate6-price-payload-reread.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -------------------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());

        // Every payload the store calls quarantined is still quarantined, under the same rule.
        var after = (await context.QuarantinedPayloads
                .AsNoTracking()
                .OrderBy(q => q.QuarantinedAtUtc)
                .ToListAsync())
            .Where(q => string.Equals(q.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(quarantined.Count, after.Count);
        Assert.Equal(
            quarantined.Select(q => q.Id.Value + "|" + q.RuleId).ToList(),
            after.Select(q => q.Id.Value + "|" + q.RuleId).ToList());

        Assert.NotEmpty(replays);
    }

    // ---- the replay ----------------------------------------------------------------------------

    private static async Task<Replay> ReplayAsync(
        EodhdDailyPriceNormalizer normalizer,
        string symbol,
        string payloadName,
        Func<byte[], NormalizationInput> inputFor,
        byte[] bytes)
    {
        // Read the array once, for description only. No judgement is made here - every accept and
        // reject below is the normaliser's.
        var raw = ReadRows(bytes);

        var first = await normalizer.NormalizeAsync(inputFor(bytes));

        var firstRule = first.RuleId ?? "(accepted as archived)";
        var firstReason = first.Reason ?? Universe.Inv($"{first.Observations.Count} observation(s)");

        var surviving = Enumerable.Range(0, raw.Count).ToList();
        var rejections = new List<Rejection>();
        var verdict = first;
        var peels = 0;

        while (verdict.IsQuarantined && peels < MaxPeels)
        {
            var match = RowIndex.Match(verdict.Reason ?? string.Empty);

            if (!match.Success)
            {
                // A payload-scoped refusal - an empty array, an unexpected shape, an unstated
                // session. Nothing to peel, and nothing here will pretend otherwise.
                break;
            }

            var position = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1;

            if (position < 0 || position >= surviving.Count)
            {
                break;
            }

            var rawIndex = surviving[position];

            rejections.Add(new Rejection(
                rawIndex + 1,
                raw[rawIndex].Date,
                raw[rawIndex].Close,
                raw[rawIndex].Text,
                verdict.Reason ?? string.Empty));

            surviving.RemoveAt(position);

            verdict = await normalizer.NormalizeAsync(inputFor(Serialise(raw, surviving)));
            peels++;
        }

        var acceptedDates = surviving
            .Select(i => raw[i].Date)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        var parsed = acceptedDates
            .Select(d => DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : (DateOnly?)null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .OrderBy(d => d)
            .ToList();

        var unique = parsed.Distinct().Count();
        var duplicates = parsed.Count - unique;

        var zeroRows = raw.Where(x => x.IsZeroClose).ToList();
        var zeroIndexes = raw
            .Select((x, i) => (x, i))
            .Where(p => p.x.IsZeroClose)
            .Select(p => p.i)
            .ToList();

        var gate = parsed.Count > 0
            ? CoverageEvaluation.Evaluate(true, parsed, parsed[0], parsed[^1])
            : null;

        return new Replay(
            symbol,
            payloadName,
            bytes.Length,
            raw.Count,
            verdict.IsQuarantined ? 0 : verdict.Observations.Count,
            rejections.Count,
            zeroRows.Count,
            parsed.Count > 0 ? parsed[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-",
            parsed.Count > 0 ? parsed[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-",
            unique,
            duplicates == 0 ? "none" : Universe.Inv($"{duplicates}"),
            Position(zeroIndexes, raw.Count),
            firstRule,
            firstReason,
            gate is null ? "-" : Universe.Inv($"{gate.Faults.Count}"),
            gate is null ? "-" : GapText(gate.Reason),
            verdict.IsQuarantined
                ? Universe.Inv($"NO - still refused: {verdict.RuleId}")
                : gate is null || gate.Faults.Count == 0 ? "YES - no interior gap over tolerance" : "partly - gaps remain",
            verdict.IsQuarantined
                ? "n/a - the reduced payload is still refused"
                : Universe.Inv($"yes - {verdict.Observations.Count} observation(s) built, none stored"),
            peels >= MaxPeels ? "peel guard reached" : "none",
            rejections);
    }

    // ---- reading, for description only ---------------------------------------------------------

    private static List<Row> ReadRows(byte[] bytes)
    {
        var rows = new List<Row>();

        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var text = element.GetRawText();
                string? date = null;
                string close = "-";
                var zero = false;

                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        date = d.GetString();
                    }

                    if (element.TryGetProperty("close", out var c))
                    {
                        close = c.GetRawText();
                        zero = c.ValueKind == JsonValueKind.Number
                            && c.TryGetDecimal(out var value)
                            && value <= 0m;
                    }
                }

                rows.Add(new Row(date, close, text, zero));
            }
        }
        catch (JsonException)
        {
            // Not an array of rows. The normaliser says so too, and its verdict is the one reported.
        }

        return rows;
    }

    private static byte[] Serialise(List<Row> raw, List<int> surviving)
    {
        var builder = new StringBuilder("[");

        for (var i = 0; i < surviving.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(raw[surviving[i]].Text);
        }

        return Encoding.UTF8.GetBytes(builder.Append(']').ToString());
    }

    private static string Position(List<int> zeroIndexes, int total)
    {
        if (zeroIndexes.Count == 0 || total == 0)
        {
            return "none";
        }

        var leading = zeroIndexes.All(i => i < zeroIndexes.Count);
        var terminal = zeroIndexes.All(i => i >= total - zeroIndexes.Count);

        return terminal ? "terminal" : leading ? "leading" : "mixed/interior";
    }

    private static string GapText(string reason) =>
        reason.Contains("gap of", StringComparison.Ordinal)
            ? reason[(reason.IndexOf("gap of", StringComparison.Ordinal) + 7)..].Split(' ')[0]
            : "0";

    private sealed record Row(string? Date, string Close, string Text, bool IsZeroClose);

    private sealed record Rejection(int RawIndex, string? Date, string Close, string Row, string Reason);

    private sealed record Replay(
        string Symbol,
        string Payload,
        int Bytes,
        int RawRows,
        int Accepted,
        int Rejected,
        int ZeroClose,
        string FirstAccepted,
        string LastAccepted,
        int UniqueAccepted,
        string Duplicates,
        string ZeroPosition,
        string FirstVerdictRule,
        string FirstVerdictReason,
        string GapsOverTolerance,
        string LargestGap,
        string Contiguity,
        string WouldObserve,
        string OtherNotes,
        IReadOnlyList<Rejection> Rejections)
    {
        public static Replay Unclaimed(string payload, string rule, int bytes) =>
            new("(no price run claims this payload)", payload, bytes, 0, 0, 0, 0, "-", "-", 0,
                "none", "none", rule, "not replayed", "-", "-", "not replayed", "no", "no claimant run", []);

        public static Replay Absent(string payload, string symbols, string rule) =>
            new(symbols, payload, -1, 0, 0, 0, 0, "-", "-", 0,
                "none", "none", rule, "the archive holds no bytes for this hash", "-", "-",
                "not replayed", "no", "payload absent from the archive", []);
    }
}
