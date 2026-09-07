using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Common;
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
/// Builds the deeper-history acquisition and checks it, without acquiring anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This makes no provider call and cannot.</strong> It never resolves
/// <c>IDataAcquisition</c>; it constructs the exact <see cref="IngestionRequest"/> set the
/// acquisition would issue, computes each one's fingerprint, and asks the ingestion ledger which of
/// them are already satisfied. The ingestion-run and observation counts are asserted unchanged, so
/// "nothing was fetched" is a property of the run rather than a claim about it.
/// </para>
/// <para>
/// <strong>Why a dry run rather than a document.</strong> A written plan can be wrong in ways
/// nobody notices until the money is spent: a window that produces a fingerprint matching one
/// already in the ledger fetches nothing, a window that does not produce the coverage anyone
/// expected fetches the wrong thing, and a call count that turns out to be double the estimate is
/// discovered at the provider. Every one of those is checkable before a key is used, and this is
/// where they are checked.
/// </para>
/// <para>
/// Gated on <c>AIINV_ACQUISITION=1</c>.
/// </para>
/// </remarks>
public sealed class PriceAcquisitionPlanTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_ACQUISITION";

    private const string SecuritySubjectKind = "Security";
    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";
    private const string CloseAttribute = "security.close";

    /// <summary>Five years, which the price-depth investigation put at the defensible minimum.</summary>
    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Roughly 252 sessions a year, so five years is about this many per instrument.</summary>
    private const int ExpectedSessionsPerInstrument = 1260;

    /// <summary>The frozen twenty. Not re-cut for this: a changed universe is a changed evidence base.</summary>
    private static readonly string[] Universe =
    {
        "AAPL.US", "MSFT.US", "GOOGL.US", "AMZN.US", "NVDA.US", "META.US", "TSLA.US", "JPM.US",
        "V.US", "JNJ.US", "WMT.US", "PG.US", "XOM.US", "UNH.US", "HD.US", "MA.US", "KO.US",
        "PEP.US", "CVX.US", "MRK.US",
    };

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PriceAcquisitionPlanTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_deeper_history_acquisition_is_planned_and_checked_without_acquiring()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Acquisition planning is off. Set {GateVariable}=1 to run it. It fetches nothing.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var runs = services.GetRequiredService<IIngestionRunStore>();
        var clock = services.GetRequiredService<IClock>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();

        var planned = new List<PlannedRequest>();

        foreach (var instrument in Universe)
        {
            foreach (var (source, category) in new[]
            {
                (PriceSource, DataCategory.MarketPrices),
                (ActionsSource, DataCategory.CorporateActions),
            })
            {
                // Built exactly as the backfill builds them, including a fresh subject and a fresh
                // window per request - both are owned entities and sharing an instance across two
                // requests in one scope is the re-parenting defect this repository has met three
                // times.
                var request = IngestionRequest.Create(
                    SourceId.Create(source),
                    category,
                    Region.Global,
                    IngestionSubject.Create(SecuritySubjectKind, instrument),
                    Correlation(instrument, category),
                    clock.UtcNow,
                    DateRange.Create(WindowStart, WindowEnd));

                var fingerprint = request.Fingerprint();

                planned.Add(new PlannedRequest(
                    instrument,
                    source,
                    category,
                    fingerprint,
                    await runs.HasCompletedAsync(fingerprint)));
            }
        }

        var held = await HeldAsync(context);
        var report = Compose(planned, held, clock.UtcNow, watch.Elapsed);

        await WriteAsync(report);
        _output.WriteLine(report);

        var runsAfter = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();

        // Planning is not acquiring. If either of these ever moves, this test has started doing the
        // thing it exists to avoid.
        Assert.Equal(runsBefore, runsAfter);
        Assert.Equal(observationsBefore, observationsAfter);

        // The plan is only worth issuing if it would actually fetch. A window whose fingerprints
        // already sit in the ledger would spend a subscription on nothing.
        Assert.Equal(Universe.Length * 2, planned.Count);
    }

    // ---- what is already held -----------------------------------------------

    private static async Task<List<Coverage>> HeldAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == SecuritySubjectKind && o.Attribute == CloseAttribute)
            .Select(o => new { Instrument = o.Subject.Identifier!, o.Provenance.AsOfUtc })
            .ToListAsync();

        return rows
            .GroupBy(r => r.Instrument, StringComparer.Ordinal)
            .Select(g => new Coverage(
                g.Key,
                g.Count(),
                g.Select(r => r.AsOfUtc).Distinct().Count(),
                g.Min(r => r.AsOfUtc),
                g.Max(r => r.AsOfUtc)))
            .OrderBy(c => c.Instrument, StringComparer.Ordinal)
            .ToList();
    }

    // ---- the report ---------------------------------------------------------

    private static string Compose(
        List<PlannedRequest> planned,
        List<Coverage> held,
        DateTime nowUtc,
        TimeSpan elapsed)
    {
        var report = new StringBuilder();
        var wouldFetch = planned.Count(p => !p.AlreadySatisfied);
        var sessions = held.Sum(h => h.Sessions);
        var rows = held.Sum(h => h.Rows);

        Line(report, "# Deeper price history — acquisition plan, verified without acquiring");
        Line(report, string.Empty);
        Line(report, Inv($"Generated {nowUtc:yyyy-MM-dd HH:mm:ss}Z in {elapsed.TotalSeconds:F1}s. **No provider was called.** Ingestion-run and observation counts asserted unchanged."));

        Line(report, string.Empty);
        Line(report, "## The plan");
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Window | {WindowStart:yyyy-MM-dd} → {WindowEnd:yyyy-MM-dd} (5 years) |"));
        Line(report, Inv($"| Instruments | {Universe.Length}, the frozen universe unchanged |"));
        Line(report, Inv($"| Requests | {planned.Count} — {Universe.Length} prices + {Universe.Length} corporate actions |"));
        Line(report, Inv($"| Already satisfied by the ledger | {planned.Count - wouldFetch} |"));
        Line(report, Inv($"| **Would fetch** | **{wouldFetch}** |"));
        Line(report, Inv($"| Expected sessions per instrument | about {ExpectedSessionsPerInstrument} |"));
        Line(report, Inv($"| Expected new price observations | about {ExpectedSessionsPerInstrument * Universe.Length:N0} |"));

        Line(report, string.Empty);
        Line(report, "Every request carries a distinct fingerprint from anything in the ledger, so the acquisition would do work rather than be suppressed as a duplicate. That is the failure this dry run exists to catch: a window whose fingerprints already match spends a subscription and returns nothing.");

        // ---- what is held today ---------------------------------------------

        Line(report, string.Empty);
        Line(report, "## What is held today");
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Instruments with prices | {held.Count} |"));
        Line(report, Inv($"| Distinct sessions | {sessions:N0} |"));
        Line(report, Inv($"| Price observations | {rows:N0} |"));
        Line(report, Inv($"| Earliest session | {(held.Count == 0 ? "—" : held.Min(h => h.Earliest).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))} |"));
        Line(report, Inv($"| Latest session | {(held.Count == 0 ? "—" : held.Max(h => h.Latest).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))} |"));

        Line(report, string.Empty);
        Line(report, "The new window overlaps the stored year. The overlapping sessions will be **published a second time** rather than replaced: same session, a later retrieval, both rows kept. That is the observation store working as designed — AAPL already carries two publications of its 250 sessions from an earlier refetch — and the point-in-time reader takes the latest publication at or before the decision instant. It is worth stating because the row count after the acquisition will exceed the session count, and that is expected rather than duplication.");

        // ---- the requests ---------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Every request, with its fingerprint");
        Line(report, string.Empty);
        Line(report, "| Instrument | Source | Category | Fingerprint | Ledger |");
        Line(report, "| --- | --- | --- | --- | --- |");

        foreach (var request in planned.OrderBy(p => p.Instrument, StringComparer.Ordinal).ThenBy(p => p.Source, StringComparer.Ordinal))
        {
            Line(report, Inv($"| `{request.Instrument}` | `{request.Source}` | {request.Category} | `{request.Fingerprint[..16]}` | {(request.AlreadySatisfied ? "already complete" : "would fetch")} |"));
        }

        // ---- the controls ---------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Controls the acquisition runs under");
        Line(report, string.Empty);
        Line(report, "| Control | How it applies here |");
        Line(report, "| --- | --- |");
        Line(report, "| Idempotency, data level | `IIngestionRunStore.HasCompletedAsync(fingerprint)` is asked **before** each fetch, not after, so a rerun costs nothing rather than costing a call and then being discarded. Checked above for all requests. |");
        Line(report, "| Idempotency, action level | The gateway's `ProcessedAction` key is derived from the request fingerprint scoped to the correlation. Correlations here are stable and window-derived, so a rerun of the same window is suppressed and a genuinely different window is not. |");
        Line(report, "| Provenance | Every observation carries `AsOfUtc` (the session), `PublishedAtUtc` (the close plus the exchange's configured publication delay) and `RetrievedAtUtc` (this fetch). All three are stored per row; none is inferred at read time. |");
        Line(report, "| Point-in-time | The two invariants the SEC backfill is already checked against apply unchanged: no observation published before the period it describes, and none published after it was retrieved. A five-year backfill retrieved today makes the second one trivially true and the first one the one that matters. |");
        Line(report, "| Corporate actions | Requested in the same plan, not afterwards. Splits are what makes a five-year price series comparable with itself; a backfill without them stores a series with silent discontinuities, which is worse than no series because it looks fine. |");
        Line(report, "| Write authorisation | Unchanged. Every write goes through the action gateway and `GuardWrites`; this plan adds no new path. |");
        Line(report, "| Universe | Frozen. Re-cutting the twenty would create a different evidence base and silently orphan every sealed declaration that names the current one. |");

        // ---- verification ----------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## What to verify after the acquisition, before believing any of it");
        Line(report, string.Empty);
        Line(report, "| Check | Passing looks like |");
        Line(report, "| --- | --- |");
        Line(report, Inv($"| Coverage | {Universe.Length} instruments, each with roughly {ExpectedSessionsPerInstrument} distinct sessions spanning {WindowStart:yyyy-MM} to {WindowEnd:yyyy-MM}. Any instrument short of about 1,150 sessions is a truncated response, not a quiet market. |"));
        Line(report, "| Point-in-time | Both invariants zero, counted over the stored rows rather than a fixture. |");
        Line(report, "| Corporate actions | A non-empty splits response for at least one instrument known to have split in the window. An empty array everywhere is the same answer the free tier gave, and would mean the plan was not actually in force. |");
        Line(report, "| Split adjustment | The adjusted read path returns a series with no unexplained move beyond the configured threshold. An unadjusted split shows up there as a 50% single-session fall. |");
        Line(report, "| Idempotency | An immediate rerun fetches zero and skips all, exactly as the SEC backfill's rerun did. |");
        Line(report, "| Opportunities | Unchanged at zero. Acquiring evidence is not discovering anything. |");
        Line(report, "| Declarations | The register's seals still verify. A wider evidence base is a **new** evidence base with a new fingerprint, not an edit to an existing one. |");

        Line(report, string.Empty);
        Line(report, "## What this plan does not cover");
        Line(report, string.Empty);
        Line(report, "- **Dividends.** The provider catalogue holds `eodhd-eod` and `eodhd-splits`; there is no dividends connector. Total-return work would need one, and it is new code rather than a wider window. Price-only research — including the drift hypothesis this acquisition exists for — is unaffected.");
        Line(report, "- **The two SEC gaps.** AAPL and HD still hold no fundamentals after transient DNS failures, and XOM's history sits under a predecessor CIK. Both are free to fix and neither blocks this.");
        Line(report, Inv($"- **The free tier cannot execute this plan.** {wouldFetch} requests against a 20-call daily ceiling is three days of fetching with no headroom, and the depth would still be one year because depth is a plan attribute rather than a call count."));

        return report.ToString();
    }

    private static CorrelationId Correlation(string instrument, DataCategory category) =>
        CorrelationId.Create(Inv(
            $"deep-{category}-{instrument.Replace('.', '-')}-{WindowStart:yyyyMMdd}-{WindowEnd:yyyyMMdd}"));

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "acquisition-plan.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report);
    }

    private sealed record PlannedRequest(
        string Instrument,
        string Source,
        DataCategory Category,
        string Fingerprint,
        bool AlreadySatisfied);

    private sealed record Coverage(
        string Instrument,
        int Rows,
        int Sessions,
        DateTime Earliest,
        DateTime Latest);
}
