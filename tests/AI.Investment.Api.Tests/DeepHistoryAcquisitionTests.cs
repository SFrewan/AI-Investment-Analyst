using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
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
/// The five-year acquisition: the forty requests the dry run fingerprinted, actually issued.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same requests, built the same way.</strong> Window, universe, source ids, subject
/// kind and correlation are constructed identically to <see cref="PriceAcquisitionPlanTests"/>, so
/// the fingerprints issued here are the forty that were checked against the ledger before a key
/// was used. A backfill that built its requests independently of the plan would be verifying one
/// thing and spending on another.
/// </para>
/// <para>
/// <strong>Splits before prices, per symbol.</strong> The stored close is the raw one, so a series
/// spanning a split carries a step that the adjusted reader refuses rather than screens. Ingesting
/// the corporate actions first means the explanation is already in the store the moment the prices
/// land, and no window is ever briefly unreadable.
/// </para>
/// <para>
/// <strong>The ledger is asked before each fetch, not after.</strong> The gateway's permanent
/// <c>ProcessedAction</c> key would suppress a duplicate anyway - but only once the call had been
/// made and paid for. Asking first is what makes a rerun after a partial failure cost only the
/// requests that did not succeed.
/// </para>
/// <para>
/// <strong>Every write goes through the seam.</strong> Registration and activation are proposals
/// judged by the policy engine; ingestion goes through <see cref="IDataAcquisition"/> and the
/// action gateway, under <c>GuardWrites</c>. Nothing here writes to the registry or the store
/// directly, and no control is bypassed, relaxed or special-cased for this run.
/// </para>
/// <para>
/// <strong>No watches are created.</strong> The twenty already exist from the earlier backfill.
/// Acquiring evidence is not scheduling work, and a run that quietly created watches would be
/// changing what the platform does on a timer while claiming to be filling a table.
/// </para>
/// <para>
/// Gated on <c>AIINV_DEEP_BACKFILL=1</c>. It makes up to forty real, billable calls.
/// </para>
/// </remarks>
public sealed class DeepHistoryAcquisitionTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_DEEP_BACKFILL";

    private const string SecuritySubjectKind = "Security";

    /// <summary>Five years, as planned and as verified available by the depth check.</summary>
    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Roughly 252 sessions a year.</summary>
    private const int ExpectedSessionsPerInstrument = 1260;

    /// <summary>
    /// Below this, a response was truncated rather than a market being quiet.
    /// </summary>
    /// <remarks>
    /// The figure the plan committed to before the acquisition ran, kept unchanged afterwards.
    /// Moving a threshold once the results are in is how a short answer becomes an acceptable one.
    /// </remarks>
    private const int MinimumUsableSessions = 1150;

    /// <summary>The frozen twenty. Re-cutting them would be a different evidence base.</summary>
    private static readonly string[] Universe =
    {
        "AAPL.US", "MSFT.US", "GOOGL.US", "AMZN.US", "NVDA.US", "META.US", "TSLA.US", "JPM.US",
        "V.US", "JNJ.US", "WMT.US", "PG.US", "XOM.US", "UNH.US", "HD.US", "MA.US", "KO.US",
        "PEP.US", "CVX.US", "MRK.US",
    };

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public DeepHistoryAcquisitionTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_frozen_universe_is_backfilled_with_five_years_of_history()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The deep acquisition is off. Set {GateVariable}=1 to run it. It makes up to forty " +
            "real, billable calls.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;
        var report = new StringBuilder();
        var calls = new CallLedger();

        var now = Resolve<IClock>(root).UtcNow;
        var before = await SnapshotAsync(root);

        Line(report, "# Five-year price acquisition — the forty requests, issued");
        Line(report, string.Empty);
        Line(report, Inv($"Run {now:yyyy-MM-dd HH:mm:ss}Z. Window {WindowStart:yyyy-MM-dd} to {WindowEnd:yyyy-MM-dd}."));
        Line(report, "The same requests `PriceAcquisitionPlanTests` fingerprinted, built the same way.");
        Line(report, "No paper trading. No execution. No watches created. No control bypassed.");
        Line(report, string.Empty);
        Line(report, Inv($"Before: {before.Runs:N0} ingestion runs, {before.Observations:N0} observations, {before.Opportunities} opportunities, {before.Quarantines} quarantines."));

        await RegisterSourcesAsync(root, report);
        await ActivateSourcesAsync(root, report);

        var issued = await IngestAsync(root, report, calls);

        Line(report, string.Empty);
        Line(report, "## Provider calls");
        Line(report, string.Empty);
        Line(report, Inv($"- splits fetched: {calls.SplitsFetched}, already complete and skipped: {calls.SplitsSkipped}"));
        Line(report, Inv($"- prices fetched: {calls.PricesFetched}, already complete and skipped: {calls.PricesSkipped}"));
        Line(report, Inv($"- refusals and failures recorded: {calls.NotFetched}"));
        Line(report, Inv($"- **billable calls this run: {calls.SplitsFetched + calls.PricesFetched}**"));

        var after = await SnapshotAsync(root);
        var incomplete = await IncompleteAsync(root, issued);
        var invariants = await PointInTimeAsync(root);
        var coverage = await VerifyCoverageAsync(root, now, report);

        Compose(report, before, after, incomplete, invariants, coverage, watch.Elapsed);

        await WriteAsync(report.ToString());
        _output.WriteLine(report.ToString());

        // ---- the checks the plan committed to, in the order it listed them ----

        Assert.True(
            coverage.Short.Count == 0,
            "These instruments hold fewer than " + MinimumUsableSessions + " usable sessions: " +
            string.Join(", ", coverage.Short) +
            ". That is a truncated response, not a quiet market.");

        Assert.True(
            invariants.PublishedBeforePeriod == 0,
            Inv($"{invariants.PublishedBeforePeriod} observations claim to have been public before ")
                + "the session they describe. Every point-in-time judgement in the platform is "
                + "made from that instant.");

        Assert.True(
            invariants.PublishedAfterRetrieval == 0,
            Inv($"{invariants.PublishedAfterRetrieval} observations claim to have been public after ")
                + "they were retrieved.");

        Assert.True(
            coverage.WithSplits > 0,
            "No instrument came back with a corporate action. An empty splits response everywhere " +
            "is the answer the free tier gave, and would mean the paid plan was not in force for " +
            "this run.");

        Assert.True(
            incomplete.Count == 0,
            "These requests did not complete, so a rerun would fetch them again: " +
            string.Join(", ", incomplete) + ".");

        Assert.Equal(before.Opportunities, after.Opportunities);
    }

    // ---- registration and activation, through the seam ------------------------------------

    private static async Task RegisterSourcesAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## Source registration");
        Line(report, string.Empty);
        Line(report, "Through `RegisterKnownSourcesHandler` — one `source.register` proposal per");
        Line(report, "source, through the gate the start-up seeder uses. Idempotent.");
        Line(report, string.Empty);

        using var scope = root.CreateScope();

        foreach (var result in await scope.ServiceProvider
            .GetRequiredService<RegisterKnownSourcesHandler>()
            .HandleAsync())
        {
            Line(report, Inv($"- `{result.SourceId}` -> {result.Outcome}"));
        }
    }

    private static async Task ActivateSourcesAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## Source activation");
        Line(report, string.Empty);
        Line(report, "Through `ActivateSourceHandler`, which proposes `source.activate` and lets the");
        Line(report, "policy engine decide. Nothing writes to the registry directly.");
        Line(report, string.Empty);

        using var scope = root.CreateScope();

        var settings = scope.ServiceProvider.GetRequiredService<DiscoverySettings>();
        var handler = scope.ServiceProvider.GetRequiredService<ActivateSourceHandler>();

        foreach (var sourceId in new[] { settings.SplitSourceId, settings.PriceSourceId })
        {
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                continue;
            }

            var result = await handler.HandleAsync(SourceId.Create(sourceId));

            Line(report, Inv($"- `{sourceId}` -> {result.Status}: {result.Reason}"));
        }
    }

    // ---- the acquisition ------------------------------------------------------------------

    private static async Task<List<Issued>> IngestAsync(
        IServiceProvider root,
        StringBuilder report,
        CallLedger calls)
    {
        var issued = new List<Issued>();

        Line(report, string.Empty);
        Line(report, "## Ingestion");
        Line(report, string.Empty);
        Line(report, "Corporate actions first, then prices, per instrument.");
        Line(report, string.Empty);
        Line(report, "| Instrument | Corporate actions | Prices |");
        Line(report, "| --- | --- | --- |");

        foreach (var symbol in Universe)
        {
            // A scope per instrument, so the owned entities on one request can never be handed to
            // another. EF associates an owned entity by REFERENCE identity, and this repository has
            // met that rule three times - LedgerAccount, IngestionSubject, then DateRange. The
            // provider call has already been paid for by the time the save refuses.
            using var scope = root.CreateScope();

            var settings = scope.ServiceProvider.GetRequiredService<DiscoverySettings>();

            var splits = await AcquireAsync(
                scope.ServiceProvider,
                settings.SplitSourceId,
                DataCategory.CorporateActions,
                symbol,
                calls,
                isSplits: true,
                issued);

            var prices = await AcquireAsync(
                scope.ServiceProvider,
                settings.PriceSourceId,
                DataCategory.MarketPrices,
                symbol,
                calls,
                isSplits: false,
                issued);

            Line(report, Inv($"| `{symbol}` | {splits} | {prices} |"));
        }

        return issued;
    }

    private static async Task<string> AcquireAsync(
        IServiceProvider services,
        string sourceId,
        DataCategory category,
        string symbol,
        CallLedger calls,
        bool isSplits,
        List<Issued> issued)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return "no source configured";
        }

        // Fresh subject, fresh window, stable correlation - identical to the planned request, so
        // this carries the fingerprint the dry run checked against the ledger.
        var request = IngestionRequest.Create(
            SourceId.Create(sourceId),
            category,
            Region.Global,
            IngestionSubject.Create(SecuritySubjectKind, symbol),
            Correlation(symbol, category),
            services.GetRequiredService<IClock>().UtcNow,
            DateRange.Create(WindowStart, WindowEnd));

        var fingerprint = request.Fingerprint();

        issued.Add(new Issued(symbol, category, fingerprint));

        // Asked before fetching. The seam would suppress a duplicate anyway, but only after the
        // call had been made and paid for.
        if (await services.GetRequiredService<IIngestionRunStore>().HasCompletedAsync(fingerprint))
        {
            calls.Skip(isSplits);

            return "already complete, skipped";
        }

        var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

        if (result.WasFetched)
        {
            calls.Fetch(isSplits);

            return Inv($"{result.Run.Outcome}, {result.ObservationsRecorded:N0} observations");
        }

        calls.NotFetched++;

        return Inv($"{result.Run.Outcome}: {result.Run.RefusalRuleId ?? result.Run.Reason ?? "no reason recorded"}");
    }

    /// <summary>
    /// A correlation that is a function of what is asked for, never of when.
    /// </summary>
    /// <remarks>
    /// Byte-identical to the planned one. The gateway's idempotency key is the request fingerprint
    /// scoped to this correlation, so a correlation that drifted from the plan would make every
    /// request look new and the dry run's ledger check meaningless.
    /// </remarks>
    private static CorrelationId Correlation(string instrument, DataCategory category) =>
        CorrelationId.Create(Inv(
            $"deep-{category}-{instrument.Replace('.', '-')}-{WindowStart:yyyyMMdd}-{WindowEnd:yyyyMMdd}"));

    // ---- verification ---------------------------------------------------------------------

    /// <summary>Requests whose runs did not complete, so a rerun would fetch them again.</summary>
    private static async Task<List<string>> IncompleteAsync(
        IServiceProvider root,
        List<Issued> issued)
    {
        using var scope = root.CreateScope();

        var runs = scope.ServiceProvider.GetRequiredService<IIngestionRunStore>();
        var incomplete = new List<string>();

        foreach (var request in issued)
        {
            if (!await runs.HasCompletedAsync(request.Fingerprint))
            {
                incomplete.Add(Inv($"{request.Instrument}/{request.Category}"));
            }
        }

        return incomplete;
    }

    /// <summary>
    /// The two point-in-time invariants, counted over stored rows rather than a fixture.
    /// </summary>
    /// <remarks>
    /// Nothing may claim to have been public before the session it describes, and nothing may claim
    /// to have been public after it was retrieved. The first is the one that matters for a
    /// backfill: it is what stops a strategy acting on a price before anybody could have seen it.
    /// </remarks>
    private static async Task<Invariants> PointInTimeAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return new Invariants(
            await context.Observations
                .AsNoTracking()
                .CountAsync(o => o.Provenance.PublishedAtUtc < o.Provenance.AsOfUtc),
            await context.Observations
                .AsNoTracking()
                .CountAsync(o => o.Provenance.PublishedAtUtc > o.Provenance.RetrievedAtUtc));
    }

    private static async Task<Coverage> VerifyCoverageAsync(
        IServiceProvider root,
        DateTime now,
        StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## Coverage after the run");
        Line(report, string.Empty);
        Line(report, "Read through the same point-in-time, split-adjusted path the screen uses —");
        Line(report, "not by counting rows, which would not notice a series the reader refuses.");
        Line(report, string.Empty);
        Line(report, "| Instrument | Usable sessions | Earliest | Latest | Splits known | Verdict |");
        Line(report, "| --- | ---: | --- | --- | ---: | --- |");

        using var scope = root.CreateScope();

        var settings = scope.ServiceProvider.GetRequiredService<DiscoverySettings>();
        var reader = scope.ServiceProvider.GetRequiredService<PriceSeriesReader>();
        var observations = scope.ServiceProvider.GetRequiredService<IObservationStore>();

        var coverage = new Coverage();

        foreach (var symbol in Universe)
        {
            var subject = IngestionSubject.Create(SecuritySubjectKind, symbol);
            var stored = await observations.ForSubjectAsync(subject, now);

            var splits = stored.Count(o =>
                string.Equals(o.Attribute, settings.SplitAttribute, StringComparison.Ordinal));

            if (splits > 0)
            {
                coverage.WithSplits++;
            }

            var adjusted = await reader.ReadAdjustedAsync(
                subject,
                settings.PriceAttribute,
                settings.SplitAttribute,
                int.MaxValue,
                now,
                settings.MaxUnexplainedMove);

            if (!adjusted.IsUsable)
            {
                // Reported, never papered over. An unexplained step stays refused.
                coverage.Short.Add(symbol);
                coverage.Refused.Add(symbol);

                Line(report, Inv($"| `{symbol}` | 0 | — | — | {splits} | REFUSED: {adjusted.Explanation} |"));

                continue;
            }

            var sessions = adjusted.Observations.Count;
            var earliest = sessions == 0 ? (DateTime?)null : adjusted.Observations.Min(o => o.Provenance.AsOfUtc);
            var latest = sessions == 0 ? (DateTime?)null : adjusted.Observations.Max(o => o.Provenance.AsOfUtc);

            coverage.Sessions += sessions;

            if (sessions < MinimumUsableSessions)
            {
                coverage.Short.Add(symbol);
            }

            Line(report, Inv(
                $"| `{symbol}` | {sessions:N0} | {Day(earliest)} | {Day(latest)} | {splits} | {(sessions >= MinimumUsableSessions ? "covered" : "SHORT")} |"));
        }

        return coverage;
    }

    private static void Compose(
        StringBuilder report,
        Snapshot before,
        Snapshot after,
        List<string> incomplete,
        Invariants invariants,
        Coverage coverage,
        TimeSpan elapsed)
    {
        Line(report, string.Empty);
        Line(report, "## The plan's seven checks");
        Line(report, string.Empty);
        Line(report, "| Check | Passing looks like | Result |");
        Line(report, "| --- | --- | --- |");
        Line(report, Inv($"| Coverage | {Universe.Length} instruments, each about {ExpectedSessionsPerInstrument} sessions, nothing under {MinimumUsableSessions} | {(coverage.Short.Count == 0 ? Inv($"**PASS** — {coverage.Sessions:N0} usable sessions across {Universe.Length}") : Inv($"**FAIL** — short: {string.Join(", ", coverage.Short)}"))} |"));
        Line(report, Inv($"| Point-in-time | both invariants zero, over stored rows | {(invariants.PublishedBeforePeriod == 0 && invariants.PublishedAfterRetrieval == 0 ? "**PASS** — 0 and 0" : Inv($"**FAIL** — {invariants.PublishedBeforePeriod} and {invariants.PublishedAfterRetrieval}"))} |"));
        Line(report, Inv($"| Corporate actions | a non-empty splits response somewhere | {(coverage.WithSplits > 0 ? Inv($"**PASS** — {coverage.WithSplits} instruments carry splits") : "**FAIL** — none")} |"));
        Line(report, Inv($"| Split adjustment | no series refused for an unexplained move | {(coverage.Refused.Count == 0 ? "**PASS** — none refused" : Inv($"**FAIL** — refused: {string.Join(", ", coverage.Refused)}"))} |"));
        Line(report, Inv($"| Idempotency | every issued request now complete, so a rerun fetches zero | {(incomplete.Count == 0 ? "**PASS** — all complete" : Inv($"**FAIL** — {incomplete.Count} incomplete"))} |"));
        Line(report, Inv($"| Opportunities | unchanged; acquiring evidence discovers nothing | {(before.Opportunities == after.Opportunities ? Inv($"**PASS** — {after.Opportunities}") : Inv($"**FAIL** — {before.Opportunities} to {after.Opportunities}"))} |"));
        Line(report, "| Declarations | the register's seals still verify | checked separately by `gate-declarations.cmd` |");

        Line(report, string.Empty);
        Line(report, "## What the store holds now");
        Line(report, string.Empty);
        Line(report, "| | Before | After | Change |");
        Line(report, "| --- | ---: | ---: | ---: |");
        Line(report, Inv($"| Ingestion runs | {before.Runs:N0} | {after.Runs:N0} | +{after.Runs - before.Runs:N0} |"));
        Line(report, Inv($"| Observations | {before.Observations:N0} | {after.Observations:N0} | +{after.Observations - before.Observations:N0} |"));
        Line(report, Inv($"| Quarantined payloads | {before.Quarantines:N0} | {after.Quarantines:N0} | +{after.Quarantines - before.Quarantines:N0} |"));
        Line(report, Inv($"| Opportunities | {before.Opportunities:N0} | {after.Opportunities:N0} | +{after.Opportunities - before.Opportunities:N0} |"));

        Line(report, string.Empty);
        Line(report, "The observation count rises by more than the session count, and that is expected rather than duplication: the new window overlaps the year already stored, and an overlapping session is **published a second time** rather than replaced — same session, a later retrieval, both rows kept. The point-in-time reader takes the latest publication at or before the decision instant.");

        Line(report, string.Empty);
        Line(report, Inv($"Completed in {elapsed.TotalMinutes:F1} minutes."));
        Line(report, string.Empty);
        Line(report, "No paper trading was started. No order was placed. No execution path was touched.");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static async Task<Snapshot> SnapshotAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return new Snapshot(
            await context.IngestionRuns.AsNoTracking().CountAsync(),
            await context.Observations.AsNoTracking().CountAsync(),
            await context.Opportunities.AsNoTracking().CountAsync(),
            await context.QuarantinedPayloads.AsNoTracking().CountAsync());
    }

    private static T Resolve<T>(IServiceProvider root)
        where T : notnull
    {
        using var scope = root.CreateScope();

        return scope.ServiceProvider.GetRequiredService<T>();
    }

    private static string Day(DateTime? instant) => instant is null
        ? "—"
        : instant.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "deep-backfill.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report);
    }

    private sealed record Issued(string Instrument, DataCategory Category, string Fingerprint);

    private sealed record Snapshot(int Runs, int Observations, int Opportunities, int Quarantines);

    private sealed record Invariants(int PublishedBeforePeriod, int PublishedAfterRetrieval);

    private sealed class Coverage
    {
        public List<string> Short { get; } = [];

        public List<string> Refused { get; } = [];

        public int Sessions { get; set; }

        public int WithSplits { get; set; }
    }

    private sealed class CallLedger
    {
        public int SplitsFetched { get; private set; }

        public int SplitsSkipped { get; private set; }

        public int PricesFetched { get; private set; }

        public int PricesSkipped { get; private set; }

        public int NotFetched { get; set; }

        public void Fetch(bool isSplits)
        {
            if (isSplits)
            {
                SplitsFetched++;
            }
            else
            {
                PricesFetched++;
            }
        }

        public void Skip(bool isSplits)
        {
            if (isSplits)
            {
                SplitsSkipped++;
            }
            else
            {
                PricesSkipped++;
            }
        }
    }
}
