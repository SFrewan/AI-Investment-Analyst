using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// What the ledger and the artefacts actually record about every failed request. Reads only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called, nothing is dispatched, retried, reclassified or repaired.</strong>
/// Every figure comes from rows and files that already exist. Run, observation and quarantine
/// counts are asserted unchanged either side, and one report is written.
/// </para>
/// <para>
/// <strong>Why this exists.</strong> Four requests have failed across the acquisition in a way that
/// looked like a pattern - each the first request a freshly started process made. "Looked like" is
/// the problem: the claim was made from four data points and a memory of the order they happened
/// in. This stage measures it instead, against every attempt on record, and reports what the
/// evidence establishes separately from what it merely permits.
/// </para>
/// <para>
/// <strong>What the recorded reason is, exactly.</strong> <c>IngestionGateway.Describe</c> walks the
/// exception chain and writes each type's name joined by <c>" &lt;- "</c>, appending
/// <c>[token]</c> wherever a level carries an <c>ITransportDiagnostic</c>. So a reason's SHAPE is
/// evidence: one type name and no bracket means a single exception with no inner and no
/// classification reached the ledger. Nothing in that string can carry a URL, a key or a body,
/// which is why it is safe to quote verbatim here.
/// </para>
/// </remarks>
public sealed class TransportFailureForensicsTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_TRANSPORT_FORENSICS";

    /// <summary>The four failures this investigation was opened for.</summary>
    private static readonly string[] Accused = ["NXST.US", "SIRI.US", "GPC.US", "MYO.US"];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public TransportFailureForensicsTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_recorded_failure_is_described_from_the_evidence_that_exists()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The transport forensics are off. Set {GateVariable}=1 to run them. They read only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        var runs = (await context.IngestionRuns.AsNoTracking().ToListAsync())
            .OrderBy(r => r.StartedAtUtc)
            .ToList();

        var failed = runs.Where(r => r.Outcome == IngestionOutcome.Failed).ToList();
        var succeeded = runs.Where(r => r.Outcome == IngestionOutcome.Succeeded).ToList();

        // ---- 1. the census -------------------------------------------------------------------------

        report.AppendLine("# Transport failures: what the record actually says");
        report.AppendLine();
        report.AppendLine("**Read-only.** No provider was called, nothing was dispatched, retried,");
        report.AppendLine("reclassified or repaired. Every figure below comes from rows and files that");
        report.AppendLine("already existed when this stage started.");
        report.AppendLine();

        report.AppendLine("## 1. The ledger, by outcome and source");
        report.AppendLine();
        report.AppendLine("| Source | Runs | Succeeded | Failed | Refused | Other |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var group in runs
            .GroupBy(r => r.Request.SourceId.Value, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv(
                $"| `{group.Key}` | {group.Count()} | {group.Count(r => r.Outcome == IngestionOutcome.Succeeded)} | {group.Count(r => r.Outcome == IngestionOutcome.Failed)} | {group.Count(r => r.Outcome == IngestionOutcome.Refused)} | {group.Count(r => r.Outcome is not (IngestionOutcome.Succeeded or IngestionOutcome.Failed or IngestionOutcome.Refused))} |"));
        }

        report.AppendLine();

        // ---- 2. the shape of every recorded reason -------------------------------------------------

        report.AppendLine("## 2. Every distinct failure reason on record, verbatim");
        report.AppendLine();
        report.AppendLine("`Describe` writes one type name per level of the exception chain, joined by");
        report.AppendLine("`<-`, with `[token]` wherever a level carried a transport classification.");
        report.AppendLine("So the shape of these strings is itself the evidence.");
        report.AppendLine();
        report.AppendLine("| Reason | Runs | Chain depth | Carries a `[token]` |");
        report.AppendLine("| --- | ---: | ---: | --- |");

        foreach (var group in failed
            .GroupBy(r => r.Reason ?? "(null)", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            var depth = group.Key.Split(" <- ", StringSplitOptions.None).Length;
            var token = group.Key.Contains('[', StringComparison.Ordinal);

            report.AppendLine(Universe.Inv(
                $"| `{group.Key}` | {group.Count()} | {depth} | {(token ? "yes" : "**no**")} |"));
        }

        report.AppendLine();

        var withToken = failed.Count(r => (r.Reason ?? string.Empty).Contains('[', StringComparison.Ordinal));

        report.AppendLine(Universe.Inv($"Failures carrying a transport classification: **{withToken} of {failed.Count}**."));
        report.AppendLine(Universe.Inv($"Failures whose chain is a single exception with no inner: **{failed.Count(r => !(r.Reason ?? string.Empty).Contains("<-", StringComparison.Ordinal))}**."));
        report.AppendLine();

        // ---- 3. how long each outcome took ---------------------------------------------------------

        report.AppendLine("## 3. Duration, by outcome and source");
        report.AppendLine();
        report.AppendLine("A refusal a server chose comes back fast. A connection that never formed");
        report.AppendLine("takes as long as whatever gave up underneath it.");
        report.AppendLine();
        report.AppendLine("| Set | Runs | Min | Median | p90 | Max |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var (label, set) in new (string, List<IngestionRun>)[]
        {
            ("all succeeded", succeeded),
            ("all failed", failed),
            ("failed, eodhd-eod", failed.Where(r => Source(r) == "eodhd-eod").ToList()),
            ("failed, eodhd-splits", failed.Where(r => Source(r) == "eodhd-splits").ToList()),
            ("succeeded, eodhd-splits", succeeded.Where(r => Source(r) == "eodhd-splits").ToList()),
        })
        {
            report.AppendLine(Percentiles(label, set));
        }

        report.AppendLine();

        // ---- 4. attempt position, from the artefacts that record dispatch order --------------------

        var positions = await PositionsAsync();

        report.AppendLine("## 4. Attempt position within a batch");
        report.AppendLine();
        report.AppendLine("Position is exact, not inferred: each batch artefact lists its attempts in");
        report.AppendLine("the order they were dispatched, and each batch is one process.");
        report.AppendLine();
        report.AppendLine("| Position | Attempts | Failures | Failure rate |");
        report.AppendLine("| --- | ---: | ---: | ---: |");

        var first = positions.Where(p => p.Position == 1).ToList();
        var rest = positions.Where(p => p.Position > 1).ToList();

        foreach (var (label, set) in new (string, List<Dispatch>)[]
        {
            ("**1 - the first request of a process**", first),
            ("2 and later", rest),
        })
        {
            var failures = set.Count(p => !p.Succeeded);

            report.AppendLine(Universe.Inv(
                $"| {label} | {set.Count} | {failures} | {Rate(failures, set.Count)} |"));
        }

        report.AppendLine();
        report.AppendLine("| Artefact | Batch | Symbols | First symbol | First outcome | Failures in batch |");
        report.AppendLine("| --- | ---: | ---: | --- | --- | ---: |");

        foreach (var artefact in positions
            .GroupBy(p => p.Artefact, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = artefact.OrderBy(p => p.Position).ToList();
            var head = ordered[0];

            report.AppendLine(Universe.Inv(
                $"| `{artefact.Key}` | {head.Batch} | {ordered.Count} | `{head.Symbol}` | {(head.Succeeded ? "succeeded" : "**FAILED**")} | {ordered.Count(p => !p.Succeeded)} |"));
        }

        report.AppendLine();

        // ---- 5. the four named failures ------------------------------------------------------------

        report.AppendLine("## 5. The four failures this was opened for");
        report.AppendLine();
        report.AppendLine("Every run the ledger holds for each symbol, in order.");
        report.AppendLine();
        report.AppendLine("| Symbol | Source | Started (UTC) | Duration | Outcome | Artefacts | Reason |");
        report.AppendLine("| --- | --- | --- | ---: | --- | ---: | --- |");

        foreach (var symbol in Accused)
        {
            var mine = runs
                .Where(r => string.Equals(r.Request.Subject.Identifier, symbol, StringComparison.Ordinal))
                .ToList();

            if (mine.Count == 0)
            {
                report.AppendLine(Universe.Inv($"| `{symbol}` | - | - | - | **no run on record** | - | - |"));
                continue;
            }

            foreach (var run in mine)
            {
                report.AppendLine(Universe.Inv(
                    $"| `{symbol}` | `{Source(run)}` | {run.StartedAtUtc:yyyy-MM-dd HH:mm:ss} | {Duration(run)} | {run.Outcome} | {run.Artifacts.Count} | `{run.Reason ?? "-"}` |"));
            }
        }

        report.AppendLine();

        // ---- 6. did the same work later succeed? ---------------------------------------------------

        report.AppendLine("## 6. Whether the same work later succeeded");
        report.AppendLine();
        report.AppendLine("Matched on source, category, subject and window - the same fields the");
        report.AppendLine("fingerprint is built from, so this is the same question the ledger asks.");
        report.AppendLine();
        report.AppendLine("| Symbol | Source | Failed at | Later success | Attempts after |");
        report.AppendLine("| --- | --- | --- | --- | ---: |");

        var recoveredCount = 0;

        foreach (var run in failed.OrderBy(r => r.StartedAtUtc))
        {
            var later = runs
                .Where(o =>
                    o.StartedAtUtc > run.StartedAtUtc &&
                    SameWork(o, run))
                .ToList();

            var recovered = later.Any(o => o.Outcome == IngestionOutcome.Succeeded);

            if (recovered)
            {
                recoveredCount++;
            }

            if (!Accused.Contains(run.Request.Subject.Identifier ?? string.Empty, StringComparer.Ordinal))
            {
                continue;
            }

            report.AppendLine(Universe.Inv(
                $"| `{run.Request.Subject.Identifier}` | `{Source(run)}` | {run.StartedAtUtc:yyyy-MM-dd HH:mm:ss} | {(recovered ? "**yes**" : "no - still outstanding")} | {later.Count} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Across every failure on record: **{recoveredCount} of {failed.Count}** were later satisfied by a successful run for the same work."));
        report.AppendLine();

        // ---- 7. what was archived before the failure -----------------------------------------------

        var failedWithArtefacts = failed.Count(r => r.Artifacts.Count > 0);

        report.AppendLine("## 7. Did a response body ever arrive?");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Failed runs | {failed.Count} |"));
        report.AppendLine(Universe.Inv($"| …that archived at least one payload | **{failedWithArtefacts}** |"));
        report.AppendLine(Universe.Inv($"| …that archived nothing | **{failed.Count - failedWithArtefacts}** |"));
        report.AppendLine();
        report.AppendLine("A run archives its payload after the response is read. A failed run holding");
        report.AppendLine("no artefact therefore failed at or before the response, not while reading it.");
        report.AppendLine();

        // ---- 8. the endpoints -----------------------------------------------------------------------

        report.AppendLine("## 8. Which endpoints are involved");
        report.AppendLine();
        report.AppendLine("| Declaration | Source | Endpoint template |");
        report.AppendLine("| --- | --- | --- |");

        foreach (var name in new[]
        {
            "acquisition-eodhd-sample400.json",
            "acquisition-eodhd-splits-sample400.json",
            "acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json",
        })
        {
            var path = Universe.RepositoryPath("declarations", name);

            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            if (!document.RootElement.TryGetProperty("Endpoints", out var endpoints))
            {
                continue;
            }

            foreach (var endpoint in endpoints.EnumerateObject())
            {
                report.AppendLine(Universe.Inv(
                    $"| `{name}` | `{endpoint.Name}` | `{endpoint.Value.GetString()}` |"));
            }
        }

        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "transport-forensics.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- read-only, proven either side ----------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());

        // This stage changes no approval. It does not require that none is open - batch 2 is spent
        // and still carries its flag, and clearing it is an authorisation change nobody asked for -
        // only that reading the evidence did not open a second one.
        Assert.True(
            AcquisitionSplitBatchTests.Partition.Count(b => b.Authorised) <= 1,
            "more than one split batch is marked authorised");
    }

    // ---- reading -----------------------------------------------------------------------------------

    private static string Source(IngestionRun run) => run.Request.SourceId.Value;

    private static bool SameWork(IngestionRun a, IngestionRun b) =>
        string.Equals(Source(a), Source(b), StringComparison.Ordinal) &&
        a.Request.Category == b.Request.Category &&
        string.Equals(a.Request.Subject.Identifier, b.Request.Subject.Identifier, StringComparison.Ordinal) &&
        a.Request.Window?.StartUtc == b.Request.Window?.StartUtc &&
        a.Request.Window?.EndUtc == b.Request.Window?.EndUtc;

    private static string Duration(IngestionRun run) =>
        run.CompletedAtUtc is { } done
            ? Universe.Inv($"{(done - run.StartedAtUtc).TotalMilliseconds:F0}ms")
            : "-";

    private static string Rate(int part, int whole) =>
        whole == 0 ? "-" : Universe.Inv($"{100.0 * part / whole:F1} %");

    private static string Percentiles(string label, List<IngestionRun> set)
    {
        var durations = set
            .Where(r => r.CompletedAtUtc is not null)
            .Select(r => (r.CompletedAtUtc!.Value - r.StartedAtUtc).TotalMilliseconds)
            .Order()
            .ToList();

        if (durations.Count == 0)
        {
            return Universe.Inv($"| {label} | {set.Count} | - | - | - | - |");
        }

        return Universe.Inv(
            $"| {label} | {durations.Count} | {durations[0]:F0}ms | {At(durations, 0.50):F0}ms | {At(durations, 0.90):F0}ms | {durations[^1]:F0}ms |");
    }

    private static double At(List<double> ordered, double quantile)
    {
        var index = (int)Math.Round((ordered.Count - 1) * quantile, MidpointRounding.AwayFromZero);

        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    /// <summary>
    /// Every dispatched attempt, with the position it held in its batch.
    /// </summary>
    /// <remarks>
    /// Read from the batch artefacts rather than reconstructed from timestamps: each artefact lists
    /// its attempts in dispatch order, and each batch ran as its own process, so position 1 is
    /// exactly "the first request this process made".
    /// </remarks>
    private static async Task<List<Dispatch>> PositionsAsync()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var positions = new List<Dispatch>();

        foreach (var path in Directory
            .EnumerateFiles(universe, "acquisition-*-attempt-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            if (!document.RootElement.TryGetProperty("Attempts", out var attempts) ||
                attempts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var batch = document.RootElement.TryGetProperty("BatchIndex", out var index) &&
                index.ValueKind == JsonValueKind.Number
                ? index.GetInt32()
                : 0;

            var ordinal = 0;

            foreach (var attempt in attempts.EnumerateArray())
            {
                // Only dispatched attempts hold a position: a suppressed one never left.
                if (!attempt.TryGetProperty("Kind", out var kind) ||
                    !string.Equals(kind.GetString(), "dispatched", StringComparison.Ordinal))
                {
                    continue;
                }

                ordinal++;

                positions.Add(new Dispatch(
                    Path.GetFileName(path),
                    batch,
                    ordinal,
                    attempt.TryGetProperty("Symbol", out var symbol) ? symbol.GetString() ?? "?" : "?",
                    attempt.TryGetProperty("Succeeded", out var ok) && ok.ValueKind == JsonValueKind.True));
            }
        }

        return positions;
    }

    /// <summary>One dispatched attempt and where it sat in its batch.</summary>
    private sealed record Dispatch(
        string Artefact,
        int Batch,
        int Position,
        string Symbol,
        bool Succeeded);
}
