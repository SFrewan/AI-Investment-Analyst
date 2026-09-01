using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Operations;
using AI.Investment.Application.Operators;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The API host configured for a controlled historical backfill.
/// </summary>
/// <remarks>
/// <para>
/// Development configuration, so the real database and the real subscription are in play. The
/// scheduler and the outbox dispatcher are off: this makes the provider calls it decides to make
/// and nothing else makes any.
/// </para>
/// <para>
/// <strong>One substitution, and it is the transport adapter rather than the seam.</strong>
/// <see cref="IOperatorContext"/> normally reads the operator off an HTTP request, and there is no
/// HTTP request here. It is replaced with a fixed identity so that watch creation runs through
/// <see cref="OperatorConsole"/> exactly as the endpoint does - the privilege check, the
/// Action/Policy gate, the write guard and the audit trail are all the production ones, and every
/// proposal this raises is recorded against a named operator rather than against nobody.
/// </para>
/// </remarks>
public sealed class BackfillApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The operator every proposal in a backfill is recorded against.</summary>
    public const string OperatorId = "backfill@operator.local";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperationsHost:RunCycles"] = "false",
                ["OperationsHost:RunOutboxDispatcher"] = "false",
                ["DataPlane:RunRetentionSweep"] = "false",

                // Seeding is a BackgroundService, so with it on the registry fills at some
                // unspecified point after the host starts and a backfill that asks about a source
                // straight away races it. The backfill calls the same handler itself, in order,
                // before it asks for anything - see RegisterSourcesAsync.
                ["DataPlane:SeedSourcesOnStartup"] = "false",
            });
        });

        builder.ConfigureTestServices(services =>
            services.AddScoped<IOperatorContext, BackfillOperator>());
    }

    /// <summary>
    /// A named operator holding the two privileges a backfill needs, and no others.
    /// </summary>
    /// <remarks>
    /// Not every privilege. A backfill registers watches and activates sources; it has no business
    /// deciding opportunities, answering escalations or touching the kill switch, and handing it
    /// those would make the audit trail say something untrue about what this run could have done.
    /// </remarks>
    private sealed class BackfillOperator : IOperatorContext
    {
        public OperatorIdentity? Current { get; } = OperatorIdentity.Create(
            OperatorId,
            "Historical backfill",
            [OperatorPrivilege.AdministerWatches]);
    }
}

/// <summary>
/// The controlled twenty-instrument, two-year historical backfill.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This makes real, billable provider calls, and only when explicitly asked.</strong> It
/// is skipped unless <c>AIINV_BACKFILL=1</c>, so an ordinary run of the suite reports it as
/// skipped and reaches no vendor.
/// </para>
/// <para>
/// <strong>Splits before prices, per symbol, deliberately.</strong> The stored close is the raw
/// one, so a series spanning a split carries a step that
/// <see cref="SplitAdjustment"/> refuses rather than screens. Ingesting the corporate actions
/// first means the explanation is already in the store the moment the prices land, and no window
/// is ever briefly unreadable.
/// </para>
/// <para>
/// <strong>Idempotent and resumable.</strong> Each request carries a correlation derived from the
/// symbol, the category and the window rather than a fresh identifier, so the idempotency key is
/// stable across runs. Before fetching anything the ingestion ledger is asked whether that exact
/// request already completed; if it did, the symbol is skipped and no call is made. A rerun after
/// a partial failure therefore costs only the calls that did not succeed the first time, and the
/// seam would suppress a duplicate even if the ledger check were removed.
/// </para>
/// <para>
/// Nothing here manufactures an adjustment. A series that still carries an unexplained step after
/// the known splits are applied is reported as refused and left refused.
/// </para>
/// </remarks>
public sealed class HistoryBackfillTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_BACKFILL";

    /// <summary>The deliberate initial universe: twenty liquid US large caps, spread by sector.</summary>
    /// <remarks>
    /// Chosen rather than screened. Twenty is enough breadth that the price-recovery rule will
    /// occasionally fire and enough that a validation run can accumulate predictions, and few
    /// enough that every name here was looked at. AAPL is first because it is the one the platform
    /// already holds history for, which makes it the control: if the backfill changes what is
    /// stored for AAPL, something is wrong with the backfill rather than with the vendor.
    /// </remarks>
    private static readonly string[] Universe =
    [
        "AAPL.US", "MSFT.US", "GOOGL.US", "AMZN.US", "NVDA.US",
        "META.US", "TSLA.US", "JPM.US", "V.US", "JNJ.US",
        "WMT.US", "PG.US", "XOM.US", "UNH.US", "HD.US",
        "MA.US", "KO.US", "PEP.US", "CVX.US", "MRK.US",
    ];

    /// <summary>Two years, as decided. One call returns the whole range.</summary>
    private static readonly TimeSpan History = TimeSpan.FromDays(730);

    /// <summary>The least usable history an instrument must end with to count as covered.</summary>
    private const int MinimumUsableSessions = 50;

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public HistoryBackfillTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_universe_is_backfilled_with_two_years_of_history()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Backfill is off. Set {GateVariable}=1 to run it. This makes real, billable provider " +
            "calls - roughly two per instrument that is not already covered.");

        var root = _factory.Services;
        var report = new StringBuilder();
        var calls = new CallLedger();

        var now = Clock(root).UtcNow;
        var window = DateRange.Create(now.Date.AddDays(-History.TotalDays), now.Date);

        Line(report, "# Block 2B - controlled historical backfill");
        Line(report, string.Empty);
        Line(report, Inv($"Run at {now:yyyy-MM-dd HH:mm:ss}Z. Window {window.StartUtc:yyyy-MM-dd} to {window.EndUtc:yyyy-MM-dd}."));
        Line(report, string.Empty);

        await RegisterSourcesAsync(root, report);
        await ActivateSourcesAsync(root, report);
        await EnsureWatchesAsync(root, report);
        await IngestAsync(root, window, report, calls);
        var coverage = await VerifyAsync(root, now, report);

        Line(report, string.Empty);
        Line(report, "## Provider calls");
        Line(report, string.Empty);
        Line(report, Inv($"- splits fetched: {calls.SplitsFetched}, already complete and skipped: {calls.SplitsSkipped}"));
        Line(report, Inv($"- prices fetched: {calls.PricesFetched}, already complete and skipped: {calls.PricesSkipped}"));
        Line(report, Inv($"- refusals and failures recorded: {calls.NotFetched}"));
        Line(report, Inv($"- **total billable calls this run: {calls.SplitsFetched + calls.PricesFetched}**"));

        await WriteReportAsync(report.ToString());

        _output.WriteLine(report.ToString());

        // Fail-closed rather than silent: an instrument that cannot be screened is named, and the
        // run fails so the report is read rather than filed.
        Assert.True(
            coverage.Short.Count == 0,
            "These instruments do not have " + MinimumUsableSessions + " usable sessions: "
            + string.Join(", ", coverage.Short));
    }

    // ---- 1. registration, through the seam ---------------------------------

    /// <summary>
    /// Puts the shipped source definitions in the registry, in order, before anything asks.
    /// </summary>
    /// <remarks>
    /// The production handler, not a shortcut around it: one <c>source.register</c> proposal per
    /// source through the same gate the start-up seeder uses. It is called here rather than left to
    /// that seeder because the seeder is a background service with no ordering guarantee against
    /// this test, and a source that is not registered yet cannot be activated - which would surface
    /// as a puzzling "not registered" refusal rather than as the race it is. Registration is
    /// idempotent, so a rerun reports every source as already registered and proposes nothing.
    /// </remarks>
    private static async Task RegisterSourcesAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, "## Source registration");
        Line(report, string.Empty);
        Line(report, "Through `RegisterKnownSourcesHandler`. Sources are registered inactive;");
        Line(report, "activation is the separate act below.");
        Line(report, string.Empty);

        using var scope = root.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<RegisterKnownSourcesHandler>();

        foreach (var result in await handler.HandleAsync())
        {
            Line(report, Inv($"- `{result.SourceId}` -> {result.Outcome}"));
        }
    }

    // ---- 2. activation, through the seam -----------------------------------

    private static async Task ActivateSourcesAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, "## Source activation");
        Line(report, string.Empty);
        Line(report, "Through `ActivateSourceHandler`, which proposes `source.activate` and lets the");
        Line(report, "policy engine decide. Nothing here writes to the registry directly.");
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

    // ---- 3. the universe ---------------------------------------------------

    private static async Task EnsureWatchesAsync(IServiceProvider root, StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## Watches");
        Line(report, string.Empty);

        using var scope = root.CreateScope();

        var console = scope.ServiceProvider.GetRequiredService<OperatorConsole>();
        var watches = scope.ServiceProvider.GetRequiredService<IWatchStore>();

        var existing = (await watches.GetAllAsync())
            .Select(w => w.Target.Identifier)
            .Where(identifier => identifier is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var already = 0;

        foreach (var symbol in Universe)
        {
            if (existing.Contains(symbol))
            {
                already++;

                continue;
            }

            // The same definition the operator endpoint builds, through the same console: the
            // privilege is checked, the proposal goes through the gate, and the write happens
            // inside an authorisation window or not at all.
            var outcome = await console.CreateScheduledWatchAsync(new ScheduledWatchDefinition(
                Inv($"{symbol} daily review"),
                PortfolioSubjectKind,
                symbol,
                TimeSpan.FromDays(1),
                TimeSpan.FromHours(4),
                Capability.OpportunityManagement,
                EquityReviewWorkPlan.Template));

            if (outcome.Succeeded)
            {
                created++;
            }
            else
            {
                Line(report, Inv($"- REFUSED {symbol}: {outcome.Reason}"));
            }
        }

        Line(report, Inv($"- already watched: {already}"));
        Line(report, Inv($"- created this run: {created}"));
        Line(report, Inv($"- universe size: {Universe.Length}"));
        Line(report, Inv($"- cooldown unchanged at 4 hours; template `{EquityReviewWorkPlan.Template}`"));
    }

    private const string PortfolioSubjectKind = "Security";

    // ---- 4. ingestion, splits before prices --------------------------------

    private static async Task IngestAsync(
        IServiceProvider root,
        DateRange window,
        StringBuilder report,
        CallLedger calls)
    {
        Line(report, string.Empty);
        Line(report, "## Ingestion");
        Line(report, string.Empty);
        Line(report, "| Instrument | Splits | Prices |");
        Line(report, "| --- | --- | --- |");

        foreach (var symbol in Universe)
        {
            using var scope = root.CreateScope();

            var settings = scope.ServiceProvider.GetRequiredService<DiscoverySettings>();

            // Corporate actions first, so the explanation is in the store before the prices that
            // need it. See the class remarks.
            var splits = await AcquireAsync(
                scope.ServiceProvider,
                settings.SplitSourceId,
                DataCategory.CorporateActions,
                symbol,
                window,
                calls,
                isSplits: true);

            var prices = await AcquireAsync(
                scope.ServiceProvider,
                settings.PriceSourceId,
                DataCategory.MarketPrices,
                symbol,
                window,
                calls,
                isSplits: false);

            Line(report, Inv($"| {symbol} | {splits} | {prices} |"));
        }
    }

    private static async Task<string> AcquireAsync(
        IServiceProvider services,
        string sourceId,
        DataCategory category,
        string symbol,
        DateRange window,
        CallLedger calls,
        bool isSplits)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return "no source configured";
        }

        var request = IngestionRequest.Create(
            SourceId.Create(sourceId),
            category,
            Region.Global,

            // A FRESH subject and a FRESH window on every request, never a shared instance.
            //
            // EF associates an owned entity with its owner by REFERENCE identity, and both of these
            // are owned types under `IngestionRun.Request`. Handing the same DateRange object to
            // two requests inside one scope makes the second save look like re-parenting the first
            // run's window, and EF refuses it: "The property 'DateRange.IngestionRequestIngestionRunId'
            // is part of a key and so cannot be modified". The provider call has already been made
            // and paid for by then, which is what makes this worth a comment rather than a shrug.
            //
            // Third time this platform has met this rule - LedgerAccount, then IngestionSubject,
            // now DateRange. See the Block 2B report: the production work plan does not share
            // instances today only because it makes exactly one request per cycle.
            IngestionSubject.Create(PortfolioSubjectKind, symbol),

            // Stable, so a rerun produces the same idempotency key rather than a second fetch.
            Correlation(symbol, category, window),
            services.GetRequiredService<IClock>().UtcNow,
            DateRange.Create(window.StartUtc, window.EndUtc));

        // Asked before fetching rather than after: the seam would suppress the duplicate anyway,
        // but only once the call had been made and paid for.
        var runs = services.GetRequiredService<IIngestionRunStore>();

        if (await runs.HasCompletedAsync(request.Fingerprint()))
        {
            calls.Skip(isSplits);

            return "already complete, skipped";
        }

        var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

        if (result.WasFetched)
        {
            calls.Fetch(isSplits);

            return Inv($"{result.Run.Outcome}, {result.ObservationsRecorded} observations");
        }

        calls.NotFetched++;

        return Inv($"{result.Run.Outcome}: {result.Run.RefusalRuleId ?? result.Run.Reason ?? "no reason recorded"}");
    }

    /// <summary>
    /// A correlation that is a function of what is being asked for, not of when.
    /// </summary>
    /// <remarks>
    /// The idempotency key is the request fingerprint scoped to this correlation, so a stable
    /// correlation is what makes a rerun free. Dots are not permitted in a correlation identifier,
    /// so the symbol's separator becomes a hyphen.
    /// </remarks>
    private static CorrelationId Correlation(string symbol, DataCategory category, DateRange window) =>
        CorrelationId.Create(Inv(
            $"backfill-{category}-{symbol.Replace('.', '-')}-{window.StartUtc:yyyyMMdd}-{window.EndUtc:yyyyMMdd}"));

    // ---- 5. verification ---------------------------------------------------

    private static async Task<Coverage> VerifyAsync(
        IServiceProvider root,
        DateTime now,
        StringBuilder report)
    {
        Line(report, string.Empty);
        Line(report, "## Coverage after the run");
        Line(report, string.Empty);
        Line(report, "Read through the same point-in-time, split-adjusted path the screen uses.");
        Line(report, string.Empty);
        Line(report, "| Instrument | Usable sessions | Splits known | Verdict |");
        Line(report, "| --- | ---: | ---: | --- |");

        using var scope = root.CreateScope();

        var settings = scope.ServiceProvider.GetRequiredService<DiscoverySettings>();
        var reader = scope.ServiceProvider.GetRequiredService<PriceSeriesReader>();
        var observations = scope.ServiceProvider.GetRequiredService<IObservationStore>();

        var coverage = new Coverage();

        foreach (var symbol in Universe)
        {
            var subject = IngestionSubject.Create(PortfolioSubjectKind, symbol);

            var stored = await observations.ForSubjectAsync(subject, now);

            var splits = stored.Count(o =>
                string.Equals(o.Attribute, settings.SplitAttribute, StringComparison.Ordinal));

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

                Line(report, Inv($"| {symbol} | 0 | {splits} | REFUSED: {adjusted.Explanation} |"));

                continue;
            }

            var sessions = adjusted.Observations.Count;

            if (sessions < MinimumUsableSessions)
            {
                coverage.Short.Add(symbol);
            }

            Line(report, Inv(
                $"| {symbol} | {sessions} | {splits} | {(sessions >= MinimumUsableSessions ? "covered" : "SHORT")} |"));
        }

        return coverage;
    }

    private static async Task WriteReportAsync(string report)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "backfill.md");

        var full = Path.GetFullPath(path);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        await File.WriteAllTextAsync(full, report);
    }

    private static IClock Clock(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IClock>();
    }

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    /// <summary>Invariant text. Named distinctly so an interpolated string cannot bind elsewhere.</summary>
    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private sealed class Coverage
    {
        public List<string> Short { get; } = [];

        public List<string> Refused { get; } = [];
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
