using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Why fifty-seven requests failed and five payloads could not be read, from evidence only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only. No provider is called and no connector is enabled.</strong> Run,
/// observation, quarantine and audit counts are asserted unchanged either side. Nothing is
/// reclassified, nothing is reprocessed, and no quarantined payload is discarded.
/// </para>
/// <para>
/// <strong>What the ledger cannot tell us, and why.</strong> The connector throws
/// <c>HttpRequestException</c> for every failure it can have - a transport error, a 401, a 429, a
/// 404, and an empty body all arrive as the same type - and the gateway deliberately records the
/// exception's type name and nothing else, so that a URL carrying an API key can never reach an
/// append-only ledger. That is the right trade, and it means the stored reason cannot separate
/// throttling from a socket error. What can separate them is <em>timing</em>, which the ledger
/// does keep: a refused status comes back in milliseconds, a connect failure in tens of
/// milliseconds, and a stalled read sits until the thirty-second client timeout. This reads those
/// timings rather than guessing from the type name.
/// </para>
/// </remarks>
public sealed class AcquisitionFailureDiagnosisTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_FAILURE_DIAGNOSIS";

    /// <summary>The correlation prefix the authorised acquisition stamped on every run.</summary>
    private const string CorrelationPrefix = "acquire-";

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public AcquisitionFailureDiagnosisTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_failures_and_the_quarantines_are_explained_from_what_was_recorded()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The failure diagnosis is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();
        var eodhd = services.GetRequiredService<IOptions<EodhdOptions>>().Value;

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        report.AppendLine("# Diagnosis: 57 transport failures, 5 unreadable payloads");
        report.AppendLine();
        report.AppendLine("**Read-only. No provider was called and no connector was enabled.**");
        report.AppendLine("Nothing was reclassified, reprocessed or discarded.");
        report.AppendLine();

        // ---- the runs this acquisition made, by their own correlation prefix -----------------------

        var all = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var mine = all
            .Where(r => r.Request.CorrelationId.Value.StartsWith(CorrelationPrefix, StringComparison.Ordinal))
            .OrderBy(r => r.StartedAtUtc)
            .Select(r => new Attempt(
                r.Request.Subject.Identifier ?? "(none)",
                r.Request.SourceId.Value,
                r.Outcome,
                r.StartedAtUtc,
                r.CompletedAtUtc,
                r.Artifacts.Select(h => h.Value).ToList(),
                r.Reason))
            .ToList();

        Assert.True(mine.Count > 0, "no run carries this acquisition's correlation prefix");

        var succeeded = mine.Where(a => a.Outcome == IngestionOutcome.Succeeded).ToList();
        var failed = mine.Where(a => a.Outcome == IngestionOutcome.Failed).ToList();

        report.AppendLine("## 1. What the ledger recorded");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Runs carrying `{CorrelationPrefix}` | {mine.Count} |"));
        report.AppendLine(Universe.Inv($"| Succeeded | {succeeded.Count} |"));
        report.AppendLine(Universe.Inv($"| Failed | {failed.Count} |"));
        report.AppendLine(Universe.Inv($"| Refused | {mine.Count(a => a.Outcome == IngestionOutcome.Refused)} |"));
        report.AppendLine(Universe.Inv($"| First started | {First(mine):yyyy-MM-dd HH:mm:ss} |"));
        report.AppendLine(Universe.Inv($"| Last started | {Last(mine):yyyy-MM-dd HH:mm:ss} |"));
        report.AppendLine(Universe.Inv($"| Wall clock | {(Last(mine) - First(mine)).TotalMinutes:F1} minutes |"));
        report.AppendLine(Universe.Inv($"| Distinct recorded reasons | {failed.Select(a => a.Reason).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine();

        // ---- the decisive measurement: how long a failure took --------------------------------------

        report.AppendLine("## 2. How long each outcome took");
        report.AppendLine();
        report.AppendLine("The client timeout is 30s and a timeout would surface as");
        report.AppendLine("`TaskCanceledException`, not `HttpRequestException`. So the question these");
        report.AppendLine("numbers answer is whether the failures came back immediately - a status the");
        report.AppendLine("server chose to return, such as 429 or 404 - or after a delay, which would");
        report.AppendLine("mean a connection that was accepted and then went nowhere.");
        report.AppendLine();
        report.AppendLine("| Outcome | Count | Min | Median | p90 | Max |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        report.AppendLine(Durations("Succeeded", succeeded));
        report.AppendLine(Durations("Failed", failed));
        report.AppendLine();

        var fastFailures = failed.Count(a => a.Duration is { } d && d.TotalMilliseconds < 1500);
        var slowFailures = failed.Count(a => a.Duration is { } d && d.TotalMilliseconds >= 1500);

        report.AppendLine(Universe.Inv(
            $"Failures returning in under 1.5s: **{fastFailures}**. Failures taking longer: **{slowFailures}**. A server that answers quickly with a refusal looks like the first; a connection problem that has to time out or retry looks like the second."));
        report.AppendLine();

        // ---- the shape of the run over time ----------------------------------------------------------

        report.AppendLine("## 3. When the failures happened");
        report.AppendLine();
        report.AppendLine("| Minute of run | Attempts | Failures | Failure rate |");
        report.AppendLine("| ---: | ---: | ---: | ---: |");

        var start = First(mine);

        foreach (var bucket in mine
            .GroupBy(a => (int)(a.StartedAtUtc - start).TotalMinutes / 5)
            .OrderBy(g => g.Key))
        {
            var attempts = bucket.Count();
            var fails = bucket.Count(a => a.Outcome == IngestionOutcome.Failed);

            report.AppendLine(Universe.Inv(
                $"| {bucket.Key * 5}-{(bucket.Key * 5) + 4} | {attempts} | {fails} | {(attempts == 0 ? 0 : (double)fails / attempts):P0} |"));
        }

        report.AppendLine();

        var gaps = new List<double>();

        for (var i = 1; i < mine.Count; i++)
        {
            gaps.Add((mine[i].StartedAtUtc - mine[i - 1].StartedAtUtc).TotalSeconds);
        }

        report.AppendLine(Universe.Inv(
            $"Gap between consecutive dispatches: min {gaps.Min():F2}s, median {Median(gaps):F2}s, max {gaps.Max():F2}s, against the {eodhd.MaxRequestsPerMinute} a minute the connector declares - that is {60.0 / Math.Max(Median(gaps), 0.001):F1} a minute at the median."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Client configuration: timeout 30s, no retry policy, typed `HttpClient` from the factory with its default two-minute handler lifetime. Base address `{eodhd.BaseAddress}`. Nothing here rotates a connection per request, and nothing retries one."));
        report.AppendLine();

        // ---- the five payloads that could not be read -------------------------------------------------

        report.AppendLine("## 4. The five payloads that produced no observation");
        report.AppendLine();

        var quarantined = await context.QuarantinedPayloads.AsNoTracking().ToListAsync();

        var recent = quarantined
            .Where(q => q.QuarantinedAtUtc >= start)
            .OrderBy(q => q.QuarantinedAtUtc)
            .ToList();

        // Which symbols each quarantined payload belongs to, by the hash their runs recorded.
        //
        // A LIST, not a dictionary, and the first draft of this got it wrong in a way worth
        // keeping: several runs archived the SAME hash, because the archive de-duplicates by
        // content and several symbols returned byte-identical bodies. One payload, many symbols.
        // A one-to-one map threw on the duplicate key - which is how the shared body was found.
        var symbolsFor = mine
            .SelectMany(a => a.Artifacts.Select(h => (Hash: h, a.Symbol)))
            .GroupBy(x => x.Hash, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        report.AppendLine("| Symbols sharing this payload | Rule | Bytes | What the payload actually is |");
        report.AppendLine("| --- | --- | ---: | --- |");

        foreach (var row in recent)
        {
            var bytes = await archive.RetrieveAsync(row.Id);
            var body = bytes is null ? null : Encoding.UTF8.GetString(bytes);

            var symbols = symbolsFor.TryGetValue(row.Id.Value, out var s)
                ? string.Join(", ", s)
                : "(no run of this acquisition claims this hash)";

            report.AppendLine(Universe.Inv(
                $"| `{symbols}` | `{row.RuleId}` | {bytes?.Length.ToString(CultureInfo.InvariantCulture) ?? "absent"} | {Describe(body)} |"));
        }

        report.AppendLine();
        report.AppendLine("The recorded reason for each, verbatim:");
        report.AppendLine();

        foreach (var row in recent)
        {
            report.AppendLine(Universe.Inv($"- `{row.Id.Abbreviated}`: {Redact(row.Reason)}"));
        }

        report.AppendLine();

        var shared = symbolsFor.Where(kv => kv.Value.Count > 1).ToList();

        report.AppendLine(Universe.Inv(
            $"Archived payloads this acquisition shared between two or more symbols: **{shared.Count}**. Content-hash de-duplication is working as designed - one stored body, one quarantine record, several runs pointing at it - and it means a count of quarantine ROWS is not a count of affected SYMBOLS."));

        foreach (var entry in shared)
        {
            report.AppendLine(Universe.Inv($"- `{entry.Key[..12]}` is shared by {entry.Value.Count} symbols: {string.Join(", ", entry.Value)}"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Quarantined payloads in total: {quarantined.Count}, of which {recent.Count} were recorded during this acquisition. None has been reprocessed, reclassified or removed."));
        report.AppendLine();

        // ---- what a partial acceptance would and would not preserve -------------------------------------

        report.AppendLine("## 5. What is actually wrong inside each quarantined payload");
        report.AppendLine();
        report.AppendLine("Counted by re-applying the normaliser's own three row checks to the");
        report.AppendLine("archived bytes - is it an object, does it carry an ISO date, is the close a");
        report.AppendLine("positive number - **without writing anything and without changing the");
        report.AppendLine("normaliser.** This is the evidence a partial-persistence decision needs:");
        report.AppendLine("how much good data sits behind each bad row, and where.");
        report.AppendLine();
        report.AppendLine("| Symbols | Rows | Readable | Unreadable | First bad row | Span of readable rows |");
        report.AppendLine("| --- | ---: | ---: | ---: | --- | --- |");

        foreach (var row in recent)
        {
            var bytes = await archive.RetrieveAsync(row.Id);

            var symbols = symbolsFor.TryGetValue(row.Id.Value, out var s)
                ? string.Join(", ", s)
                : row.Id.Abbreviated;

            var shape = Inspect(bytes);

            report.AppendLine(Universe.Inv(
                $"| `{symbols}` | {shape.Rows} | {shape.Readable} | **{shape.Unreadable}** | {shape.FirstBad ?? "-"} | {shape.Span} |"));
        }

        report.AppendLine();

        // ---- the gate 6 wart: members against entries ---------------------------------------------------

        report.AppendLine("## 5. Gate 6: faulted members against fault entries");
        report.AppendLine();
        report.AppendLine("The gate counts a member once however many faults it carries - the arithmetic");
        report.AppendLine("was always right. The report printed fault *entries* beside faulted *members*");
        report.AppendLine("in one table, which reads as a contradiction. Both, separately:");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Payloads quarantined during this acquisition: {recent.Count}. Each is one faulted member carrying one `{CoverageEvaluation.NoSeries}` entry, so members and entries agree there. An interior gap is different: a member with two holes contributes two entries and one fault, which is why 46 entries came from 2 members. No arithmetic changes; the report will name the two units separately."));
        report.AppendLine();

        _output.WriteLine(report.ToString());

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "acquisition-failure-diagnosis.md"),
            report.ToString());

        // ---- reading is not writing ----------------------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// Re-applies the normaliser's row checks to archived bytes, and writes nothing.
    /// </summary>
    /// <remarks>
    /// A deliberate second implementation, and a read-only one. The point is not to normalise -
    /// that would be reprocessing a quarantined payload, which this stage must not do - but to
    /// say how many rows a partial acceptance would have kept and how many it would have dropped.
    /// A number, so the decision is not taken on an impression.
    /// </remarks>
    private static Shape Inspect(byte[]? bytes)
    {
        if (bytes is null)
        {
            return new Shape(0, 0, 0, null, "the archive holds no bytes");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return new Shape(0, 0, 0, null, "not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new Shape(0, 0, 0, null, "not a JSON array");
            }

            var rows = 0;
            var readable = 0;
            var unreadable = 0;
            string? firstBad = null;
            string? firstGood = null;
            string? lastGood = null;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                rows++;

                var date = element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("date", out var d) &&
                    d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null;

                var closeIsPositive =
                    element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("close", out var c) &&
                    c.ValueKind == JsonValueKind.Number &&
                    c.TryGetDecimal(out var close) &&
                    close > 0m;

                if (date is not null && closeIsPositive)
                {
                    readable++;
                    firstGood ??= date;
                    lastGood = date;
                }
                else
                {
                    unreadable++;
                    firstBad ??= Universe.Inv($"row {rows} ({date ?? "no date"})");
                }
            }

            return new Shape(
                rows,
                readable,
                unreadable,
                firstBad,
                firstGood is null ? "none readable" : Universe.Inv($"{firstGood}..{lastGood}"));
        }
    }

    private static DateTime First(List<Attempt> attempts) => attempts.Min(a => a.StartedAtUtc);

    private static DateTime Last(List<Attempt> attempts) => attempts.Max(a => a.StartedAtUtc);

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();

        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static string Durations(string label, List<Attempt> attempts)
    {
        var measured = attempts
            .Where(a => a.Duration is not null)
            .Select(a => a.Duration!.Value.TotalMilliseconds)
            .Order()
            .ToList();

        if (measured.Count == 0)
        {
            return Universe.Inv($"| {label} | {attempts.Count} | - | - | - | none carries a completion time |");
        }

        return Universe.Inv(
            $"| {label} | {measured.Count} | {measured[0]:F0}ms | {measured[measured.Count / 2]:F0}ms | {measured[(int)(measured.Count * 0.9)]:F0}ms | {measured[^1]:F0}ms |");
    }

    /// <summary>What a payload is, said from its bytes rather than from what was expected.</summary>
    private static string Describe(string? body)
    {
        if (body is null)
        {
            return "the archive holds no bytes under this hash";
        }

        var trimmed = body.Trim();

        if (trimmed.Length == 0)
        {
            return "**zero bytes** - the response carried no body at all";
        }

        if (string.Equals(trimmed, "[]", StringComparison.Ordinal))
        {
            return "**an empty JSON array** - the vendor has no rows for this symbol and window";
        }

        var shape = trimmed[0] switch
        {
            '[' => "a JSON array",
            '{' => "a JSON object",
            '<' => "markup, not JSON",
            _ => "neither JSON nor markup",
        };

        return Universe.Inv($"{shape}, {trimmed.Length} chars, beginning `{Redact(trimmed[..Math.Min(90, trimmed.Length)])}`");
    }

    /// <summary>
    /// Removes anything shaped like a credential before it reaches a file.
    /// </summary>
    /// <remarks>
    /// This report is written to the repository. A vendor error body is one of the likelier places
    /// for an echoed request URL, and an echoed request URL is where the API key would be.
    /// </remarks>
    private static string Redact(string text)
    {
        var redacted = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var marker = text.IndexOf("token=", index, StringComparison.OrdinalIgnoreCase);

            if (marker < 0)
            {
                redacted.Append(text, index, text.Length - index);
                break;
            }

            var valueStart = marker + "token=".Length;
            var valueEnd = valueStart;

            while (valueEnd < text.Length && text[valueEnd] is not ('&' or ' ' or '"' or '\'' or '\n'))
            {
                valueEnd++;
            }

            redacted.Append(text, index, valueStart - index).Append("REDACTED");
            index = valueEnd;
        }

        return redacted.ToString();
    }

    private sealed record Shape(
        int Rows,
        int Readable,
        int Unreadable,
        string? FirstBad,
        string Span);

    private sealed record Attempt(
        string Symbol,
        string Source,
        IngestionOutcome Outcome,
        DateTime StartedAtUtc,
        DateTime? CompletedAtUtc,
        List<string> Artifacts,
        string? Reason)
    {
        public TimeSpan? Duration =>
            CompletedAtUtc is { } done ? done - StartedAtUtc : null;
    }
}
