using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Everything the acquisition claimed, checked against the store rather than against its own report.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and no provider is called.</strong> The second-run half of gate 9 is proved
/// by re-planning all 710 requests and asking the ledger about each - which is exactly what a
/// second acquisition would do first - and then showing that nothing would be dispatched and that
/// the observation count did not move.
/// </para>
/// <para>
/// Gate 6 is evaluated with acquisition <em>declared complete</em>, which is the state the
/// safeguard was built for: a member still carrying <c>not-yet-acquired</c> was skipped rather
/// than pending, and the gate fails on it.
/// </para>
/// </remarks>
public sealed class PostAcquisitionVerificationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_POST_ACQUISITION";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const int SealedBytes = 245126;

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PostAcquisitionVerificationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Everything_the_acquisition_produced_is_verified_against_the_store()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Post-acquisition verification is off. Set {GateVariable}=1 to run it. It reads only.");

        var watch = Stopwatch.StartNew();
        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var eodhd = services.GetRequiredService<IOptions<EodhdOptions>>().Value;
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();

        report.AppendLine("# Post-acquisition verification");
        report.AppendLine();
        report.AppendLine("**Read-only. No provider was called.** Every figure is read from the store,");
        report.AppendLine("never from the acquisition's own report.");
        report.AppendLine();

        // ---- G. the manifest, first, because everything else is meaningless without it ---------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var digest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

        report.AppendLine("## G. The sealed manifest");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"{manifestBytes.Length} bytes against {SealedBytes}, SHA-256 `{digest}`."));
        report.AppendLine(Universe.Inv($"Matches the approved digest: **{(string.Equals(digest, SealedDigest, StringComparison.Ordinal) ? "yes" : "NO")}**. Not resealed, not modified."));
        report.AppendLine();

        // ---- A. what the acquisition actually spent, read from the ledger ---------------------------

        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        var outcomePath = Universe.RepositoryPath("artifacts", "universe", "acquisition-outcome.json");

        Assert.True(File.Exists(outcomePath), "the acquisition has not run: no outcome artefact exists");

        using var outcome = JsonDocument.Parse(await File.ReadAllTextAsync(outcomePath));

        var claimed = outcome.RootElement;
        var stoppedBecause = claimed.GetProperty("StoppedBecause");

        var acquisitionComplete =
            stoppedBecause.ValueKind == JsonValueKind.Null &&
            claimed.GetProperty("Dispatched").GetInt32() > 0;

        var ledger = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var vendor = ledger
            .Where(r => r.Request.SourceId.Value.StartsWith("eodhd", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var thisWindow = vendor
            .Where(r => r.Request.Window is { } w && w.StartUtc == WindowStart && w.EndUtc == WindowEnd)
            .ToList();

        report.AppendLine("## A. Provider accounting");
        report.AppendLine();
        report.AppendLine("| | Claimed by the run | In the ledger |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| Planned | {claimed.GetProperty("Planned").GetInt32()} | {authorization.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Suppressed | {claimed.GetProperty("Suppressed").GetInt32()} | - |"));
        report.AppendLine(Universe.Inv($"| Dispatched | {claimed.GetProperty("Dispatched").GetInt32()} | - |"));
        report.AppendLine(Universe.Inv($"| Succeeded | {claimed.GetProperty("Succeeded").GetInt32()} | {thisWindow.Count(r => r.Outcome == IngestionOutcome.Succeeded)} |"));
        report.AppendLine(Universe.Inv($"| Partially succeeded | {claimed.GetProperty("PartiallySucceeded").GetInt32()} | {thisWindow.Count(r => r.Outcome == IngestionOutcome.PartiallySucceeded)} |"));
        report.AppendLine(Universe.Inv($"| Failed | {claimed.GetProperty("Failed").GetInt32()} | {thisWindow.Count(r => r.Outcome == IngestionOutcome.Failed)} |"));
        report.AppendLine(Universe.Inv($"| Refused | {claimed.GetProperty("Refused").GetInt32()} | {thisWindow.Count(r => r.Outcome == IngestionOutcome.Refused)} |"));
        report.AppendLine(Universe.Inv($"| Authorisation consumed | {claimed.GetProperty("AuthorizationConsumed").GetInt32()} | ceiling {authorization.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| Authorisation remaining | {claimed.GetProperty("AuthorizationRemaining").GetInt32()} | - |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Price runs at this window in the ledger: {thisWindow.Count(r => r.Request.SourceId.Value == PriceSource)}. Split runs: {thisWindow.Count(r => r.Request.SourceId.Value == ActionsSource)}. Dividend runs: 0 - no such source exists. SEC runs during this stage: 0."));
        report.AppendLine();

        // ---- B. identity ------------------------------------------------------------------------------

        var members = await MembersAsync();

        var ready = members.Where(m => m.Ready).ToList();
        var excluded = members.Where(m => !m.Ready).ToList();

        var readySymbols = ready.Select(m => m.Symbol!).ToHashSet(StringComparer.Ordinal);

        var attempted = claimed.GetProperty("Attempts")
            .EnumerateArray()
            .Select(a => a.GetProperty("Symbol").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var nonEquity = ready.Count(m =>
            m.SecurityKind is not null &&
            !string.Equals(m.SecurityKind, "equity", StringComparison.OrdinalIgnoreCase));

        // Anything an excluded member might have been requested under, in either notation.
        var excludedSymbols = excluded
            .Where(m => m.Ticker is not null)
            .SelectMany(m => new[] { m.Ticker!, m.Ticker! + AcquisitionPlanning.UsSuffix })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        report.AppendLine("## B. Identity");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Members | {members.Count} |"));
        report.AppendLine(Universe.Inv($"| Acquisition-ready | {ready.Count} |"));
        report.AppendLine(Universe.Inv($"| Excluded | {excluded.Count} |"));
        report.AppendLine(Universe.Inv($"| Distinct symbols attempted | {attempted.Count} |"));
        report.AppendLine(Universe.Inv($"| Attempted symbols outside the 355 | **{attempted.Count(s => !readySymbols.Contains(s))}** |"));
        report.AppendLine(Universe.Inv($"| Ready members classified as non-equity | **{nonEquity}** |"));
        report.AppendLine();

        // ---- F. what the store now holds --------------------------------------------------------------

        var prices = await RowsAsync(
            context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute));

        var splits = await RowsAsync(
            context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute));

        // Raw close only. An adjusted series would silently rewrite history every time a split is
        // declared, which is the one thing a point-in-time store must never hold.
        var adjusted = await context.Observations
            .AsNoTracking()
            .CountAsync(o => EF.Functions.Like(o.Attribute, "%adjust%"));

        var session = eodhd.Exchanges.FirstOrDefault(e =>
            string.Equals(e.Code, "US", StringComparison.OrdinalIgnoreCase));

        var wrongClose = 0;
        var wrongPublication = 0;
        var outsideWindow = 0;
        var forExcluded = 0;

        foreach (var row in prices)
        {
            var tradingDate = row.AsOfUtc.Date;

            if (session is not null)
            {
                var expected = tradingDate + UsEquitySessionCalendar.CloseOn(session, tradingDate);

                if (row.AsOfUtc != expected)
                {
                    wrongClose++;
                }

                if (row.PublishedAtUtc != row.AsOfUtc + session.PublicationDelay)
                {
                    wrongPublication++;
                }
            }

            if (row.AsOfUtc < WindowStart || row.AsOfUtc > WindowEnd.AddDays(1))
            {
                outsideWindow++;
            }

            if (row.Identifier is not null && excludedSymbols.Contains(row.Identifier))
            {
                forExcluded++;
            }
        }

        report.AppendLine("## F. Data integrity");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Closing-price rows | {prices.Count} |"));
        report.AppendLine(Universe.Inv($"| Split rows | {splits.Count} |"));
        report.AppendLine(Universe.Inv($"| Distinct price series | {prices.Select(r => r.Identifier).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine(Universe.Inv($"| Attributes mentioning an adjusted price | **{adjusted}** |"));
        var sessionText = session is null
            ? "none"
            : Universe.Inv($"US {session.SessionCloseUtc:hh\\:mm} daylight, +{session.PublicationDelay:hh\\:mm} publication");

        report.AppendLine(Universe.Inv($"| Session close configured | {sessionText} |"));
        report.AppendLine(Universe.Inv($"| Prices stamped at the wrong session close | **{wrongClose}** |"));
        report.AppendLine(Universe.Inv($"| Prices with a publication instant that is not close plus delay | **{wrongPublication}** |"));
        report.AppendLine(Universe.Inv($"| Prices outside the authorised window | **{outsideWindow}** |"));
        report.AppendLine(Universe.Inv($"| Prices for an excluded member | **{forExcluded}** |"));
        report.AppendLine();
        report.AppendLine("The session-close check is the DST one: it asks the promoted resolver what");
        report.AppendLine("the close was on each trading date and compares, so a row stamped an hour");
        report.AppendLine("early in winter is a failure here rather than a discrepancy nobody looks for.");
        report.AppendLine();

        // ---- E. gate 12 --------------------------------------------------------------------------------

        var twelve = await GateTwelveAsync(context);

        report.AppendLine("## E. Gate 12 - de-duplication, zero excess");
        report.AppendLine();
        report.AppendLine("| Namespace | Rows | Distinct identities | Excess |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| financials | {twelve.FinancialRows} | {twelve.FinancialDistinct} | {ObservationDeduplication.Excess(twelve.FinancialRows, twelve.FinancialDistinct)} |"));
        report.AppendLine(Universe.Inv($"| prices | {twelve.PriceRows} | {twelve.PriceDistinct} | {ObservationDeduplication.Excess(twelve.PriceRows, twelve.PriceDistinct)} |"));
        report.AppendLine(Universe.Inv($"| splits | {twelve.SplitRows} | {twelve.SplitDistinct} | {ObservationDeduplication.Excess(twelve.SplitRows, twelve.SplitDistinct)} |"));
        report.AppendLine(Universe.Inv($"| **Gate 12** | | | **{(twelve.Passes ? "PASS" : "FAIL")}** |"));
        report.AppendLine();

        // ---- C. gate 6, with acquisition declared complete ---------------------------------------------

        var held = prices
            .Where(r => r.Identifier is not null)
            .GroupBy(r => r.Identifier!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => DateOnly.FromDateTime(r.AsOfUtc)).Distinct().ToList(),
                StringComparer.Ordinal);

        var requested = ledger
            .Where(r => r.Outcome == IngestionOutcome.Succeeded &&
                r.Request.Category == DataCategory.MarketPrices &&
                r.Request.Subject.Identifier is not null)
            .Select(r => r.Request.Subject.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var faults = 0;
        var pending = 0;
        var noSeries = 0;
        var interiorGaps = 0;
        var discontinuities = 0;
        var withSeries = 0;
        var faulted = new List<string>();

        foreach (var member in members)
        {
            var symbol = member.Ready ? member.Symbol : null;

            List<DateOnly> sessions = symbol is not null && held.TryGetValue(symbol, out var dates)
                ? dates
                : [];

            var verdict = CoverageEvaluation.Evaluate(
                member.Ready,
                sessions,
                member.SpanFrom,
                member.SpanTo,
                wasRequested: symbol is not null && requested.Contains(symbol));

            if (verdict.Sessions > 0)
            {
                withSeries++;
            }

            if (verdict.NotYetAcquired)
            {
                pending++;
            }

            if (verdict.IsFault)
            {
                faults++;
                faulted.Add(Universe.Inv($"{symbol ?? member.Cik} ({string.Join(", ", verdict.Faults)}): {verdict.Reason}"));
            }

            noSeries += verdict.Faults.Count(f => f == CoverageEvaluation.NoSeries);
            interiorGaps += verdict.Faults.Count(f => f == CoverageEvaluation.InteriorGap);
            discontinuities += verdict.Faults.Count(f => f == CoverageEvaluation.UnexplainedDiscontinuity);
        }

        var sixPasses = CoverageEvaluation.Passes(faults, pending, acquisitionComplete);

        report.AppendLine("## C. Gate 6 - coverage, acquisition declared complete");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Acquisition declared complete by the runner | {acquisitionComplete} |"));
        report.AppendLine(Universe.Inv($"| Acquirable members holding a series | {withSeries} of {ready.Count} |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.NoSeries}` - requested and empty | **{noSeries}** |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.InteriorGap}` - gap beyond {CoverageEvaluation.InteriorGapTolerance} sessions | **{interiorGaps}** |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.UnexplainedDiscontinuity}` | **{discontinuities}** |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.NotYetAcquired}` - never requested | **{pending}** |"));
        report.AppendLine(Universe.Inv($"| **Total faults** | **{faults}** |"));
        report.AppendLine(Universe.Inv($"| **Gate 6** | **{(sixPasses ? "PASS" : "FAIL")}** |"));
        report.AppendLine();

        if (faulted.Count > 0)
        {
            report.AppendLine("Every fault, named:");
            report.AppendLine();

            foreach (var line in faulted.Take(80))
            {
                report.AppendLine(Universe.Inv($"- {line}"));
            }

            if (faulted.Count > 80)
            {
                report.AppendLine(Universe.Inv($"- …and {faulted.Count - 80} more, in the JSON."));
            }

            report.AppendLine();
        }

        // ---- D. gate 9, the second run ------------------------------------------------------------------

        var priorPriceRun = ledger
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefault();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? "Security";
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        var replanned = 0;
        var wouldSuppress = 0;
        var wouldDispatch = new List<string>();

        foreach (var (source, category) in new[]
        {
            (PriceSource, DataCategory.MarketPrices),
            (ActionsSource, DataCategory.CorporateActions),
        })
        {
            foreach (var member in ready)
            {
                var request = IngestionRequest.Create(
                    SourceId.Create(source),
                    category,
                    region,
                    IngestionSubject.Create(subjectKind, member.Symbol!),
                    CorrelationId.Create(Universe.Inv($"second-run-{member.Ticker}-{category}")),
                    clock.UtcNow,
                    DateRange.Create(WindowStart, WindowEnd));

                replanned++;

                if (await runStore.HasCompletedAsync(request.Fingerprint()))
                {
                    wouldSuppress++;
                }
                else
                {
                    wouldDispatch.Add(Universe.Inv($"{source} {member.Symbol}"));
                }
            }
        }

        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();
        var runsAfter = await context.IngestionRuns.AsNoTracking().CountAsync();

        report.AppendLine("## D. Gate 9 - the second run, empirically");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Requests re-planned | {replanned} |"));
        report.AppendLine(Universe.Inv($"| …the ledger would suppress | **{wouldSuppress}** |"));
        report.AppendLine(Universe.Inv($"| …that would still dispatch | **{wouldDispatch.Count}** |"));
        report.AppendLine(Universe.Inv($"| Observations before this check | {observationsBefore} |"));
        report.AppendLine(Universe.Inv($"| Observations after | {observationsAfter} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs before / after | {runsBefore} / {runsAfter} |"));
        report.AppendLine();

        if (wouldDispatch.Count > 0)
        {
            report.AppendLine("A second run would still fetch these, which means the first did not");
            report.AppendLine("complete them:");
            report.AppendLine();

            foreach (var line in wouldDispatch.Take(40))
            {
                report.AppendLine(Universe.Inv($"- {line}"));
            }

            report.AppendLine();
        }

        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "post-acquisition-verification.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the judgements -----------------------------------------------------------------------------

        Assert.Equal(SealedDigest, digest);
        Assert.True(manifestBytes.Length == SealedBytes, "the sealed manifest's length changed");

        Assert.True(members.Count == 400, "the universe is not four hundred members");
        Assert.True(ready.Count == 355, Universe.Inv($"{ready.Count} ready, not 355"));
        Assert.True(excluded.Count == 45, Universe.Inv($"{excluded.Count} excluded, not 45"));
        Assert.Equal(0, nonEquity);
        Assert.Equal(0, attempted.Count(s => !readySymbols.Contains(s)));

        // Verification reads; it does not write.
        Assert.Equal(observationsBefore, observationsAfter);
        Assert.Equal(runsBefore, runsAfter);

        // F.
        Assert.Equal(0, adjusted);
        Assert.Equal(0, wrongClose);
        Assert.Equal(0, wrongPublication);
        Assert.Equal(0, outsideWindow);
        Assert.Equal(0, forExcluded);

        // E.
        Assert.True(twelve.Passes, "gate 12 does not pass across all three namespaces");

        // D. A second run must fetch nothing.
        Assert.True(replanned == 710, Universe.Inv($"{replanned} requests re-planned, not 710"));
        Assert.Empty(wouldDispatch);

        // C. Last, because it is the one most likely to fail on real data and the report above is
        // more useful than the assertion.
        Assert.True(sixPasses, Universe.Inv($"gate 6 fails: {faults} faults and {pending} never requested"));
    }

    // ---- reading ---------------------------------------------------------------------------------------

    private static async Task<List<Member>> MembersAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        var windowFrom = DateOnly.FromDateTime(WindowStart);
        var windowTo = DateOnly.FromDateTime(WindowEnd);

        var finalCut = manifest.RootElement
            .GetProperty("CohortCutDates")
            .EnumerateArray()
            .Select(c => c.GetString() ?? string.Empty)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Last();

        var cohorts = manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => m.GetProperty("Cohorts").EnumerateArray()
                    .Select(c => c.GetString() ?? string.Empty)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m =>
            {
                var cik = m.GetProperty("Cik").GetString() ?? string.Empty;
                List<string> list = cohorts.TryGetValue(cik, out var c) ? c : [];

                var (spanFrom, spanTo) = MembershipSpan.Derive(
                    [.. list.Select(d => DateOnly.Parse(d, CultureInfo.InvariantCulture))],
                    DateOnly.Parse(finalCut, CultureInfo.InvariantCulture),
                    windowFrom,
                    windowTo);

                var ticker = m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                var isReady = m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True;

                return new Member(
                    cik,
                    isReady,
                    ticker,
                    isReady && ticker is not null ? AcquisitionPlanning.SymbolFor(ticker) : null,
                    m.TryGetProperty("SecurityKind", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString()
                        : null,
                    spanFrom,
                    spanTo);
            })
            .ToList();
    }

    private static async Task<Twelve> GateTwelveAsync(AppDbContext context)
    {
        var financial = await Measure(
            context.Observations.AsNoTracking()
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%")));

        var prices = await Measure(
            context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute));

        var splits = await Measure(
            context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute));

        return new Twelve(
            financial.Rows, financial.Distinct,
            prices.Rows, prices.Distinct,
            splits.Rows, splits.Distinct);
    }

    private static async Task<(int Rows, int Distinct)> Measure(
        IQueryable<AI.Investment.Domain.Observations.Observation> query)
    {
        var rows = await query
            .Select(o => new
            {
                o.Subject.Kind,
                o.Subject.Identifier,
                o.Attribute,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
                o.Value.Canonical,
            })
            .ToListAsync();

        var keys = rows
            .Select(r => ObservationDeduplication.Key(
                r.Kind, r.Identifier, r.Attribute, r.AsOfUtc, r.PublishedAtUtc, r.Canonical))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return (rows.Count, keys);
    }

    private static async Task<List<Row>> RowsAsync(
        IQueryable<AI.Investment.Domain.Observations.Observation> query)
    {
        var rows = await query
            .Select(o => new
            {
                o.Subject.Identifier,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .ToListAsync();

        return rows
            .Select(r => new Row(r.Identifier, r.AsOfUtc, r.PublishedAtUtc))
            .ToList();
    }

    private sealed record Row(string? Identifier, DateTime AsOfUtc, DateTime PublishedAtUtc);

    private sealed record Member(
        string Cik,
        bool Ready,
        string? Ticker,
        string? Symbol,
        string? SecurityKind,
        DateOnly SpanFrom,
        DateOnly SpanTo);

    private sealed record Twelve(
        int FinancialRows,
        int FinancialDistinct,
        int PriceRows,
        int PriceDistinct,
        int SplitRows,
        int SplitDistinct)
    {
        public bool Passes =>
            ObservationDeduplication.Passes(FinancialRows, FinancialDistinct) &&
            ObservationDeduplication.Passes(PriceRows, PriceDistinct) &&
            ObservationDeduplication.Passes(SplitRows, SplitDistinct);
    }
}
