using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// The state after batch one, recomputed from the store rather than read from a report.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and nothing is enabled.</strong> No provider is called, no connector is
/// switched on, no payload is reprocessed or reclassified, no parser rule is applied, the
/// authorisation declaration is not amended and no batch is marked authorised. Run, observation and
/// quarantine counts are asserted unchanged either side, and the sealed manifest and identity table
/// are checked against their approved values.
/// </para>
/// <para>
/// <strong>Why it recomputes rather than cites.</strong> Every number in the last report is a claim
/// made by a process that has since ended. Claims and stores have disagreed in this repository more
/// than once - the "82 of 86 runs bypassed the seam" that turned out to be a measurement artefact,
/// the "38 pre-existing EODHD quarantines" that were 5 - and the decision this feeds is about
/// spending money. So every figure below is rebuilt from the ledger, the archive, the identity
/// table and the authorisation, and the ones that were asserted last time are asserted again.
/// </para>
/// </remarks>
public sealed class PostBatchReviewTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_POST_BATCH_REVIEW";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const int SealedBytes = 245126;

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";
    private const string DefaultSecurityKind = "Security";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The row fields a zero-price sentinel would have to zero to be all-zero.</summary>
    private static readonly string[] NumericFields =
        ["open", "high", "low", "close", "adjusted_close", "volume"];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PostBatchReviewTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_state_after_batch_one_is_recomputed_and_the_remaining_work_is_reconciled()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The post-batch review is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);

        var declarationPath = Universe.RepositoryPath(
            "declarations", "acquisition-eodhd-sample400.json");

        var declarationBefore = await File.ReadAllBytesAsync(declarationPath);

        var authorization = AcquisitionAuthorization.Load(declarationPath, SealedFingerprint);

        var prior = await PriorConsumptionAsync();
        var members = await MembersAsync();
        var ready = members.Where(m => m.Ready && m.Symbol is not null).ToList();

        report.AppendLine("# Post-batch-1 review");
        report.AppendLine();
        report.AppendLine("**Read-only. No provider was called and no connector was enabled.** Nothing");
        report.AppendLine("was reprocessed, reclassified, persisted or removed; no parser rule was applied;");
        report.AppendLine("the authorisation was neither amended nor consumed; no batch was authorised.");
        report.AppendLine();
        report.AppendLine("Every figure below is recomputed from the ledger, the archive, the identity");
        report.AppendLine("table and the authorisation. None is copied from an earlier report.");
        report.AppendLine();

        // ---- A. the remaining acquisition, request by request ---------------------------------------

        var ledger = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var priorPriceRun = ledger
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefault();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        var planned = 0;
        var suppressiblePrices = 0;
        var suppressibleSplits = 0;
        var outstandingPrices = new List<string>();
        var outstandingSplits = new List<string>();

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
                    CorrelationId.Create(Universe.Inv($"review-{planned}")),
                    clock.UtcNow,
                    DateRange.Create(WindowStart, WindowEnd));

                planned++;

                var satisfied = await runStore.HasCompletedAsync(request.Fingerprint());

                if (source == PriceSource)
                {
                    if (satisfied)
                    {
                        suppressiblePrices++;
                    }
                    else
                    {
                        outstandingPrices.Add(member.Symbol!);
                    }
                }
                else if (satisfied)
                {
                    suppressibleSplits++;
                }
                else
                {
                    outstandingSplits.Add(member.Symbol!);
                }
            }
        }

        var suppressible = suppressiblePrices + suppressibleSplits;
        var outstanding = outstandingPrices.Count + outstandingSplits.Count;
        var available = authorization.DispatchCeiling - prior.Total;
        var shortfall = outstanding - available;

        // Failures inside the authorised scope only. The ledger also holds older price runs from
        // stages that predate this authorisation, at other windows, and counting those here would
        // attribute somebody else's failure to this budget.
        var atThisWindow = ledger
            .Where(r => r.Request.Window is { } w && w.StartUtc == WindowStart && w.EndUtc == WindowEnd)
            .ToList();

        var failedPriceRuns = atThisWindow.Count(r =>
            r.Outcome == IngestionOutcome.Failed &&
            r.Request.Category == DataCategory.MarketPrices);

        var failedPriceRunsEverywhere = ledger.Count(r =>
            r.Outcome == IngestionOutcome.Failed &&
            r.Request.Category == DataCategory.MarketPrices);

        report.AppendLine("## A. The remaining acquisition, reconciled");
        report.AppendLine();
        report.AppendLine("| | Recomputed |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Planned by the authorisation | {planned} |"));
        report.AppendLine(Universe.Inv($"| Suppressible by the ledger today | **{suppressible}** |"));
        report.AppendLine(Universe.Inv($"| …prices | {suppressiblePrices} |"));
        report.AppendLine(Universe.Inv($"| …splits | {suppressibleSplits} |"));
        report.AppendLine(Universe.Inv($"| **Outstanding prices** | **{outstandingPrices.Count}** |"));
        report.AppendLine(Universe.Inv($"| **Outstanding splits** | **{outstandingSplits.Count}** |"));
        report.AppendLine(Universe.Inv($"| **Total outstanding** | **{outstanding}** |"));
        report.AppendLine();
        report.AppendLine("| | Recomputed |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Authorised ceiling | {authorization.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| Consumed by the interrupted run | {prior.Outcome} |"));
        report.AppendLine(Universe.Inv($"| Consumed by batch 1 | {prior.Batches} |"));
        report.AppendLine(Universe.Inv($"| **Consumed in total** | **{prior.Total}** |"));
        report.AppendLine(Universe.Inv($"| **Remaining** | **{available}** |"));
        report.AppendLine(Universe.Inv($"| **Shortfall against the outstanding work** | **{shortfall}** |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Price dispatches at the authorised window that produced no completed run: **{failedPriceRuns} failed** and **{atThisWindow.Count(r => r.Outcome == IngestionOutcome.Refused && r.Request.Category == DataCategory.MarketPrices)} refused**. Each still spent its authorisation, because the runner charges at the point of intent so that real vendor spend can never be under-counted. That, and nothing else, is where the shortfall of {shortfall} comes from."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Failed price dispatches anywhere in the ledger: {failedPriceRunsEverywhere}. The difference of {failedPriceRunsEverywhere - failedPriceRuns} belongs to earlier stages at other windows and is not charged to this authorisation."));
        report.AppendLine();

        // ---- B. what could close the shortfall, costed rather than recommended -------------------------

        var splitRunsHeld = ledger.Count(r =>
            r.Request.Category == DataCategory.CorporateActions &&
            r.Outcome == IngestionOutcome.Succeeded);

        var emptySeriesSymbols = await EmptySeriesSymbolsAsync(context, archive);

        var readySymbols = ready.Select(m => m.Symbol!).ToHashSet(StringComparer.Ordinal);

        var emptyAndReady = emptySeriesSymbols
            .Where(readySymbols.Contains)
            .Order(StringComparer.Ordinal)
            .ToList();

        var emptyAndStillOutstandingSplits = emptyAndReady
            .Where(outstandingSplits.Contains)
            .ToList();

        var unauthorisedOutstanding = outstandingPrices.Concat(outstandingSplits)
            .Where(s => !authorization.Symbols.Contains(s))
            .ToList();

        var nonEquityReady = ready.Count(m =>
            m.SecurityKind is not null &&
            !string.Equals(m.SecurityKind, "equity", StringComparison.OrdinalIgnoreCase));

        report.AppendLine("## B. The shortfall: what the store can and cannot say about closing it");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Prices first, then splits: prices need | {outstandingPrices.Count} of {available} |"));
        report.AppendLine(Universe.Inv($"| …ceiling left after the price phase | {available - outstandingPrices.Count} |"));
        report.AppendLine(Universe.Inv($"| …against outstanding splits | {outstandingSplits.Count} |"));
        report.AppendLine(Universe.Inv($"| **Shortfall after the price phase** | **{outstandingSplits.Count - (available - outstandingPrices.Count)}** |"));
        report.AppendLine(Universe.Inv($"| Split runs already satisfied | {splitRunsHeld} |"));
        report.AppendLine(Universe.Inv($"| Outstanding requests outside the authorised {authorization.Symbols.Count} | **{unauthorisedOutstanding.Count}** |"));
        report.AppendLine(Universe.Inv($"| Ready members classified as non-equity | **{nonEquityReady}** |"));
        report.AppendLine(Universe.Inv($"| Ready symbols whose vendor price series came back empty | {emptyAndReady.Count} |"));
        report.AppendLine(Universe.Inv($"| …of those, still owed a split request | {emptyAndStillOutstandingSplits.Count} |"));
        report.AppendLine();
        report.AppendLine("The shortfall does not move with ordering: prices and splits draw on one");
        report.AppendLine("ceiling, so completing prices first leaves the same arithmetic at the splits");
        report.AppendLine("phase. Ordering changes *what is finished when the ceiling runs out*, not");
        report.AppendLine("how far short it falls.");
        report.AppendLine();
        report.AppendLine("Nothing in the store reduces the split requirement on its own. A split");
        report.AppendLine("request becomes suppressible only by being dispatched and succeeding, so");
        report.AppendLine("suppression cannot arrive without spending the authorisation it would save.");
        report.AppendLine();

        if (emptyAndStillOutstandingSplits.Count > 0)
        {
            report.AppendLine(Universe.Inv(
                $"The one candidate for exclusion the evidence even suggests: {emptyAndStillOutstandingSplits.Count} ready symbols for which the vendor returned an empty end-of-day series. That the vendor holds no prices for a symbol is **not proof** that it holds no splits for it - the two endpoints are different datasets - so this is a hypothesis that would cost one request each to test and is recorded here as a hypothesis, not a saving:"));
            report.AppendLine();

            foreach (var symbol in emptyAndStillOutstandingSplits)
            {
                report.AppendLine(Universe.Inv($"- `{symbol}`"));
            }

            report.AppendLine();
        }

        // ---- C. the quarantined EODHD payloads, characterised ----------------------------------------

        var quarantined = await context.QuarantinedPayloads
            .AsNoTracking()
            .OrderBy(q => q.QuarantinedAtUtc)
            .ToListAsync();

        var symbolsFor = ledger
            .SelectMany(r => r.Artifacts.Select(h =>
                (Hash: h.Value, Symbol: r.Request.Subject.Identifier ?? "(none)")))
            .GroupBy(x => x.Hash, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        var rowSurveys = new List<RowSurvey>();

        foreach (var row in quarantined.Where(q => string.Equals(q.SourceId.Value, PriceSource, StringComparison.Ordinal)))
        {
            var bytes = await archive.RetrieveAsync(row.Id);

            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array ||
                    document.RootElement.GetArrayLength() == 0)
                {
                    continue;
                }

                var symbols = symbolsFor.TryGetValue(row.Id.Value, out var s) ? s : ["(unclaimed)"];

                rowSurveys.Add(Characterise(
                    string.Join(" ", symbols), row.QuarantinedAtUtc, document.RootElement));
            }
        }

        // Which price dates the rest of the store shows as real sessions. If somebody else traded
        // that day, the market was open and the zero row stands for an instrument that did not.
        var sessionDates = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .Select(o => o.Provenance.AsOfUtc)
            .Distinct()
            .ToListAsync();

        var openDays = sessionDates.Select(DateOnly.FromDateTime).ToHashSet();

        report.AppendLine(Universe.Inv($"## C. Every `{PriceSource}` payload that parsed as rows"));
        report.AppendLine();
        report.AppendLine("| Symbols | Rows | Read | Bad | First bad | Terminal | Interior bad | Last read | Gap to first bad |");
        report.AppendLine("| --- | ---: | ---: | ---: | --- | --- | ---: | --- | ---: |");

        foreach (var survey in rowSurveys.OrderBy(s => s.Symbols, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{survey.Symbols}` | {survey.Rows} | {survey.Readable} | {survey.Bad.Count} | {survey.FirstBadDescription} | {(survey.AllBadTerminal ? "yes" : "**no**")} | {survey.InteriorBad} | {survey.LastReadable ?? "-"} | {survey.GapDescription} |"));
        }

        report.AppendLine();

        var zeroPriceRows = rowSurveys
            .SelectMany(s => s.Bad.Select(b => (s.Symbols, Row: b)))
            .Where(x => x.Row.ZeroPriced)
            .ToList();

        report.AppendLine("### The five questions asked of the zero-priced rows");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Zero-priced rows found | {zeroPriceRows.Count} |"));
        report.AppendLine(Universe.Inv($"| …with **every** numeric field zero | **{zeroPriceRows.Count(x => x.Row.AllNumericZero)}** |"));
        report.AppendLine(Universe.Inv($"| …with a **non-zero volume** against zero OHLC and adjusted_close | **{zeroPriceRows.Count(x => x.Row.NonZeroVolumeOnly)}** |"));
        report.AppendLine(Universe.Inv($"| …that are terminal in their payload | **{zeroPriceRows.Count(x => x.Row.Terminal)}** |"));
        report.AppendLine(Universe.Inv($"| …that are **interior** | **{zeroPriceRows.Count(x => !x.Row.Terminal)}** |"));
        report.AppendLine(Universe.Inv($"| …dated after the payload's last readable session | **{zeroPriceRows.Count(x => x.Row.AfterLastReadable)}** |"));
        report.AppendLine(Universe.Inv($"| …falling on a day other symbols in this store traded | **{zeroPriceRows.Count(x => x.Row.Date is { } d && openDays.Contains(d))}** |"));
        report.AppendLine();

        report.AppendLine("Every zero-priced row, verbatim. A price row carries a date and six numbers");
        report.AppendLine("and no credential, so it is shown in full - this is the evidence any parser");
        report.AppendLine("policy would have to be written against.");
        report.AppendLine();
        report.AppendLine("| Symbols | Row | Date | All numeric zero | Volume | Terminal | After last read | Market open | The row |");
        report.AppendLine("| --- | ---: | --- | --- | ---: | --- | --- | --- | --- |");

        foreach (var (symbols, bad) in zeroPriceRows.OrderBy(x => x.Symbols, StringComparer.Ordinal).ThenBy(x => x.Row.Index))
        {
            report.AppendLine(Universe.Inv(
                $"| `{symbols}` | {bad.Index} | {bad.Date:yyyy-MM-dd} | {(bad.AllNumericZero ? "yes" : "**no**")} | {bad.Volume} | {(bad.Terminal ? "yes" : "**no**")} | {(bad.AfterLastReadable ? "yes" : "no")} | {(bad.Date is { } d && openDays.Contains(d) ? "yes" : "no")} | `{bad.Raw}` |"));
        }

        report.AppendLine();

        var nonZeroVolume = zeroPriceRows.Count(x => x.Row.NonZeroVolumeOnly);

        report.AppendLine(nonZeroVolume > 0
            ? Universe.Inv($"**{nonZeroVolume} of {zeroPriceRows.Count} zero-priced rows carry a non-zero volume.** \"All-zero sentinel\" is therefore false as a description of this data, and any rule written to that shape would not have matched them. Whatever a zero-priced row means, it is not uniformly \"the vendor wrote a row of zeroes\".")
            : "No zero-priced row carries a non-zero volume in the payloads held today.");
        report.AppendLine();

        var otherBad = rowSurveys
            .SelectMany(s => s.Bad.Where(b => !b.ZeroPriced).Select(b => (s.Symbols, Row: b)))
            .ToList();

        report.AppendLine(Universe.Inv(
            $"Bad rows that are **not** zero-priced: {otherBad.Count}. {(otherBad.Count == 0 ? string.Empty : "They are a different failure and are listed below.")}"));

        if (otherBad.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("| Symbols | Row | Kind | The row |");
            report.AppendLine("| --- | ---: | --- | --- |");

            foreach (var (symbols, bad) in otherBad)
            {
                report.AppendLine(Universe.Inv($"| `{symbols}` | {bad.Index} | {bad.Kind} | `{bad.Raw}` |"));
            }
        }

        report.AppendLine();

        // ---- D. Gate 6, unchanged semantics, broken out ------------------------------------------------

        var prices = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .Select(o => new { o.Subject.Identifier, o.Provenance.AsOfUtc })
            .ToListAsync();

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

        // The symbols whose only surviving payload was refused for a terminal row. Used to say
        // which Gate 6 faults are trailing anomalies rather than missing data - a reporting split,
        // not a change to the rule.
        var terminalAnomalySymbols = rowSurveys
            .Where(s => s.AllBadTerminal && s.Readable > 0)
            .SelectMany(s => s.Symbols.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        var emptyPayloadSymbols = emptySeriesSymbols;

        var faults = 0;
        var pending = 0;
        var noSeries = 0;
        var interiorGaps = 0;
        var discontinuities = 0;
        var withSeries = 0;
        var faulted = new List<Faulted>();

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

                var bucket = symbol is not null && terminalAnomalySymbols.Contains(symbol)
                    ? "terminal/trailing anomaly"
                    : symbol is not null && emptyPayloadSymbols.Contains(symbol)
                        ? "requested, vendor returned an empty series"
                        : verdict.Faults.Contains(CoverageEvaluation.InteriorGap)
                            ? "interior gap"
                            : "other";

                faulted.Add(new Faulted(
                    symbol ?? member.Cik,
                    bucket,
                    string.Join(", ", verdict.Faults.Distinct(StringComparer.Ordinal)),
                    verdict.Reason ?? "-"));
            }

            noSeries += verdict.Faults.Count(f => f == CoverageEvaluation.NoSeries);
            interiorGaps += verdict.Faults.Count(f => f == CoverageEvaluation.InteriorGap);
            discontinuities += verdict.Faults.Count(f => f == CoverageEvaluation.UnexplainedDiscontinuity);
        }

        var sixIncomplete = CoverageEvaluation.Passes(faults, pending, acquisitionComplete: false);
        var sixComplete = CoverageEvaluation.Passes(faults, pending, acquisitionComplete: true);

        report.AppendLine("## D. Gate 6, with the rule exactly as it stands");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Acquirable members holding a series | {withSeries} of {ready.Count} |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.NotYetAcquired}` - never requested | **{pending}** |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.NoSeries}` - requested and empty | **{noSeries}** |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.InteriorGap}` - beyond {CoverageEvaluation.InteriorGapTolerance} sessions | **{interiorGaps}** |"));
        report.AppendLine(Universe.Inv($"| `{CoverageEvaluation.UnexplainedDiscontinuity}` | **{discontinuities}** |"));
        report.AppendLine(Universe.Inv($"| **Faulted members** | **{faults}** |"));
        report.AppendLine(Universe.Inv($"| Gate 6 with acquisition incomplete | **{(sixIncomplete ? "PASS" : "FAIL")}** |"));
        report.AppendLine(Universe.Inv($"| Gate 6 if acquisition were declared complete | **{(sixComplete ? "PASS" : "FAIL")}** |"));
        report.AppendLine();
        report.AppendLine("The two rows differ only in the `not-yet-acquired` safeguard added earlier;");
        report.AppendLine("the tolerances, the zero-fault threshold and the membership rule are untouched.");
        report.AppendLine();

        report.AppendLine("Every faulted member, grouped by what is actually wrong with it:");
        report.AppendLine();
        report.AppendLine("| Bucket | Members |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in faulted
            .GroupBy(f => f.Bucket, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv($"| {group.Key} | {group.Count()} |"));
        }

        report.AppendLine();
        report.AppendLine("| Member | Bucket | Fault | Reason |");
        report.AppendLine("| --- | --- | --- | --- |");

        foreach (var row in faulted.OrderBy(f => f.Bucket, StringComparer.Ordinal).ThenBy(f => f.Symbol, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{row.Symbol}` | {row.Bucket} | {row.Kinds} | {Trim(row.Reason)} |"));
        }

        report.AppendLine();

        // ---- E. Gate 9 and Gate 12 -----------------------------------------------------------------------

        var priceRows = await MeasureAsync(context.Observations.AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute));

        var splitRows = await MeasureAsync(context.Observations.AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute));

        var financialRows = await MeasureAsync(context.Observations.AsNoTracking()
            .Where(o => EF.Functions.Like(o.Attribute, ObservationDeduplication.FinancialPrefix + "%")));

        var runsAfterReplan = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsAfterReplan = await context.Observations.AsNoTracking().CountAsync();

        report.AppendLine("## E. Gate 9 and Gate 12");
        report.AppendLine();
        report.AppendLine("| Gate 9 - the second run, empirically | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Requests re-planned | {planned} |"));
        report.AppendLine(Universe.Inv($"| …the ledger would suppress | **{suppressible}** |"));
        report.AppendLine(Universe.Inv($"| …that would still dispatch | **{outstanding}** |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs before / after re-planning | {runsBefore} / {runsAfterReplan} |"));
        report.AppendLine(Universe.Inv($"| Observations before / after re-planning | {observationsBefore} / {observationsAfterReplan} |"));
        report.AppendLine(Universe.Inv($"| **New runs created by re-planning completed work** | **{runsAfterReplan - runsBefore}** |"));
        report.AppendLine(Universe.Inv($"| **New observations created** | **{observationsAfterReplan - observationsBefore}** |"));
        report.AppendLine();
        report.AppendLine("| Gate 12 - de-duplication | Rows | Distinct identities | Excess |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| financials | {financialRows.Rows} | {financialRows.Distinct} | {ObservationDeduplication.Excess(financialRows.Rows, financialRows.Distinct)} |"));
        report.AppendLine(Universe.Inv($"| prices | {priceRows.Rows} | {priceRows.Distinct} | {ObservationDeduplication.Excess(priceRows.Rows, priceRows.Distinct)} |"));
        report.AppendLine(Universe.Inv($"| splits | {splitRows.Rows} | {splitRows.Distinct} | {ObservationDeduplication.Excess(splitRows.Rows, splitRows.Distinct)} |"));
        report.AppendLine();

        // ---- F. the two open questions, recorded and not acted on --------------------------------------

        var priceRuns = atThisWindow
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .ToList();

        var failedPrices = priceRuns.Count(r => r.Outcome == IngestionOutcome.Failed);
        var refusedPrices = priceRuns.Count(r => r.Outcome == IngestionOutcome.Refused);

        report.AppendLine("## F. Transport, and the parser policy, as evidence only");
        report.AppendLine();
        report.AppendLine("| Price dispatches at the authorised window | Count |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Runs recorded | {priceRuns.Count} |"));
        report.AppendLine(Universe.Inv($"| …succeeded | {priceRuns.Count(r => r.Outcome == IngestionOutcome.Succeeded)} |"));
        report.AppendLine(Universe.Inv($"| …failed on transport | {failedPrices} |"));
        report.AppendLine(Universe.Inv($"| …refused by the seam | {refusedPrices} |"));
        report.AppendLine();
        report.AppendLine("**Transport.** `socket:HostNotFound` was recorded on the first dispatch of");
        report.AppendLine("several freshly started processes, and at least one cold first dispatch");
        report.AppendLine("succeeded. Process-start name-resolution instability is therefore a supported");
        report.AppendLine("hypothesis and **not** a proven deterministic cause. No DNS warm-up, retry,");
        report.AppendLine("handler-lifetime, pooling, keep-alive, timeout or rate-limit change has been");
        report.AppendLine("made, and the diagnostic architecture is exactly as it was.");
        report.AppendLine();
        report.AppendLine("**Parser policy.** Terminal zero-priced rows exist; some of them carry a");
        report.AppendLine("non-zero volume against zero prices; and at least one payload has a genuine");
        report.AppendLine("interior bad-row run with valid data on both sides of it. A partial-acceptance");
        report.AppendLine("rule keyed on \"terminal\" or on \"all fields zero\" would match neither case, so");
        report.AppendLine("**no partial-acceptance rule is currently justified** and none is implemented.");
        report.AppendLine("Nothing was reprocessed or reclassified.");
        report.AppendLine();

        // ---- G. the authorisation counter, structurally ---------------------------------------------------

        var fresh = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        var freshConsumed = fresh.Consumed;

        fresh.RecordPriorConsumption(prior.Total);

        var mutators = typeof(AcquisitionAuthorization)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var settableCounters = typeof(AcquisitionAuthorization)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        report.AppendLine("## G. The authorisation counter");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Consumed by batch 1, from its own artefact | {prior.Batches} |"));
        report.AppendLine(Universe.Inv($"| Consumed in total | **{prior.Total} of {authorization.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Remaining | **{available}** |"));
        report.AppendLine(Universe.Inv($"| A freshly loaded authorisation starts at | {freshConsumed} |"));
        report.AppendLine(Universe.Inv($"| …and after charging what is on disk | {fresh.Consumed}, leaving {fresh.Remaining} |"));
        report.AppendLine(Universe.Inv($"| Public methods on the type | {string.Join(", ", mutators)} |"));
        report.AppendLine(Universe.Inv($"| Public settable properties | **{(settableCounters.Count == 0 ? "none" : string.Join(", ", settableCounters))}** |"));
        report.AppendLine();
        report.AppendLine("A new process does start the counter at zero - that is exactly why the batch");
        report.AppendLine("runner charges what earlier processes wrote before it dispatches anything. The");
        report.AppendLine("guarantee is not that the object remembers; it is that the runner refuses to");
        report.AppendLine("begin until the record on disk has been charged against it. Nothing on the type");
        report.AppendLine("can credit a dispatch back: there is no setter and no method that decreases the");
        report.AppendLine("count, and over-charging throws rather than clamping.");
        report.AppendLine();

        // ---- H. the sealed universe -----------------------------------------------------------------------

        var manifestDigest = Convert.ToHexString(SHA256.HashData(manifestBefore)).ToLowerInvariant();

        report.AppendLine("## H. The sealed universe and the identity table");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Manifest bytes | {manifestBefore.Length} against {SealedBytes} |"));
        report.AppendLine(Universe.Inv($"| SHA-256 | `{manifestDigest}` |"));
        report.AppendLine(Universe.Inv($"| Matches the approved digest | **{(string.Equals(manifestDigest, SealedDigest, StringComparison.Ordinal) ? "yes" : "NO")}** |"));
        report.AppendLine(Universe.Inv($"| Members | {members.Count} |"));
        report.AppendLine(Universe.Inv($"| Acquisition-ready | {ready.Count} |"));
        report.AppendLine(Universe.Inv($"| Not ready | {members.Count - ready.Count} |"));
        report.AppendLine(Universe.Inv($"| Ready members classified as non-equity | **{nonEquityReady}** |"));
        report.AppendLine(Universe.Inv($"| Authorisation digest | `{authorization.Digest}` |"));
        report.AppendLine(Universe.Inv($"| Authorisation consumed by this review | **{authorization.Consumed}** |"));
        report.AppendLine();

        // ---- I. scope integrity, and the authorisation the remaining work would need ------------------

        var pricesOutsideWindow = prices.Count(p =>
            p.AsOfUtc < WindowStart || p.AsOfUtc > WindowEnd.AddDays(1));

        // Every notation an excluded member could have been requested under. This is the claim
        // that matters: a member the universe excluded must have no price in the store. Prices
        // under identifiers outside the 355 that belong to no member at all are older stages'
        // work - they predate this universe and are counted separately rather than asserted away.
        var excludedSymbols = await ExcludedSymbolsAsync();

        var pricesForExcludedMember = prices.Count(p =>
            p.Identifier is not null && excludedSymbols.Contains(p.Identifier));

        var pricesOutsideTheReady = prices.Count(p =>
            p.Identifier is null || !readySymbols.Contains(p.Identifier));

        // What every batch this phase ran actually asked for, from the artefacts they wrote. A
        // split, dividend, benchmark or SEC request would appear here as a source that is not the
        // price endpoint.
        var dispatchedSources = await BatchSourcesAsync();

        var ledgerSources = ledger
            .Select(r => r.Request.SourceId.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        report.AppendLine("## I. Scope integrity");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Price observations outside the authorised window | **{pricesOutsideWindow}** |"));
        report.AppendLine(Universe.Inv($"| Price observations for an **excluded** member | **{pricesForExcludedMember}** |"));
        report.AppendLine(Universe.Inv($"| Price observations under an identifier outside the ready {readySymbols.Count} | {pricesOutsideTheReady} |"));
        report.AppendLine(Universe.Inv($"| …of which belong to no member of this universe at all | {pricesOutsideTheReady - pricesForExcludedMember} - earlier stages, predating this manifest |"));
        report.AppendLine(Universe.Inv($"| Sources this phase's batches dispatched | {string.Join(", ", dispatchedSources)} |"));
        report.AppendLine(Universe.Inv($"| Sources present anywhere in the ledger | {string.Join(", ", ledgerSources)} |"));
        report.AppendLine(Universe.Inv($"| Dividend or benchmark runs | **0** - no such source exists in the ledger |"));
        report.AppendLine();

        // ---- the superseding authorisation, drafted and not activated ---------------------------------

        var splitSymbols = ready.Select(m => m.Symbol!).Order(StringComparer.Ordinal).ToList();
        var proposedPlanned = splitSymbols.Count;
        var proposedSatisfied = suppressibleSplits;
        var proposedCeiling = proposedPlanned - proposedSatisfied;

        var proposedDigest = AuthorizationDigest(
            SealedFingerprint,
            authorization.Vendor,
            [ActionsSource],
            DateOnly.FromDateTime(WindowStart),
            DateOnly.FromDateTime(WindowEnd),
            proposedPlanned,
            proposedSatisfied,
            proposedCeiling,
            splitSymbols);

        var proposalPath = Universe.RepositoryPath(
            "artifacts", "universe", "proposed-acquisition-eodhd-splits.json");

        await Universe.WriteAsync(
            proposalPath,
            JsonSerializer.Serialize(
                new
                {
                    Schema = AcquisitionAuthorization.Schema,
                    AuthorizationId = "eodhd-sample400-splits-2021-09-to-2026-08",
                    EvidenceBaseFingerprint = SealedFingerprint,
                    Status = "PROPOSED - NOT ACTIVE. This file is in artifacts/, not declarations/, "
                        + "and no runner loads it. Activating it is a separate decision.",
                    SupersedesAuthorizationId = authorization.AuthorizationId,
                    SupersedesAuthorizationDigest = authorization.Digest,
                    AlreadyConsumed = prior.Total,
                    AlreadyConsumedNote = "Evidence-bearing. Spent under the superseded authorisation "
                        + "and never credited back; the runner charges it before it dispatches.",
                    AuthorisedAtUtc = (string?)null,
                    AuthorisedBy = (string?)null,
                    Vendor = authorization.Vendor,
                    Sources = new[] { ActionsSource },
                    Endpoints = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ActionsSource] = "api/splits/{symbol}",
                    },
                    Categories = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ActionsSource] = nameof(DataCategory.CorporateActions),
                    },
                    WindowFromUtc = Universe.Inv($"{WindowStart:yyyy-MM-dd}"),
                    WindowToUtc = Universe.Inv($"{WindowEnd:yyyy-MM-dd}"),
                    SubjectKind = subjectKind,
                    Region = region.Code,
                    PlannedRequests = proposedPlanned,
                    AlreadySatisfied = proposedSatisfied,
                    DispatchCeiling = proposedCeiling,
                    AuthorizationDigest = proposedDigest,
                    Symbols = splitSymbols,
                },
                Universe.Json) + "\n");

        // Proof rather than assertion: the real loader reads the draft, recomputes its digest and
        // enforces its own arithmetic. If any of it were wrong this throws rather than passing.
        var proposed = AcquisitionAuthorization.Load(proposalPath, SealedFingerprint);

        report.AppendLine("## J. The superseding authorisation, drafted only");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Written to `artifacts/universe/{Path.GetFileName(proposalPath)}`, **not** to `declarations/`."));
        report.AppendLine("Nothing loads it, and the existing declaration is untouched.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Authorisation id | `{proposed.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Supersedes | `{authorization.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Source | `{string.Join(", ", proposed.Sources)}` |"));
        report.AppendLine(Universe.Inv($"| Symbols | {proposed.Symbols.Count} |"));
        report.AppendLine(Universe.Inv($"| Window | {proposed.WindowFrom:yyyy-MM-dd}..{proposed.WindowTo:yyyy-MM-dd} |"));
        report.AppendLine(Universe.Inv($"| Planned | {proposed.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Already satisfied | {proposed.AlreadySatisfied} |"));
        report.AppendLine(Universe.Inv($"| **Dispatch ceiling** | **{proposed.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Already consumed, carried as evidence | {prior.Total} |"));
        report.AppendLine(Universe.Inv($"| Digest | `{proposed.Digest}` |"));
        report.AppendLine();
        report.AppendLine("The ceiling is not chosen; it is forced. The loader refuses any declaration");
        report.AppendLine("whose ceiling is not planned less already-satisfied, and planned is symbols");
        report.AppendLine("times sources - so a ceiling of one more than the outstanding work cannot be");
        report.AppendLine("written down at all. There is no discretionary headroom to spend.");
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "post-batch-review.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -------------------------------------------------------------------------------

        var manifestAfter = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(manifestBefore.SequenceEqual(manifestAfter), "the sealed manifest changed");
        Assert.Equal(SealedDigest, manifestDigest);
        Assert.True(manifestBefore.Length == SealedBytes, "the sealed manifest's length changed");

        Assert.True(members.Count == 400, Universe.Inv($"{members.Count} members, not 400"));
        Assert.True(ready.Count == 355, Universe.Inv($"{ready.Count} ready, not 355"));
        Assert.Equal(0, nonEquityReady);

        // Nothing moved.
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());

        // The authorisation was neither consumed nor amended by reviewing it.
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(704, authorization.DispatchCeiling);
        Assert.Equal(0, freshConsumed);

        // The reconciliation the memo rests on.
        Assert.True(planned == 710, Universe.Inv($"{planned} planned, not 710"));
        // The price phase, frozen. Every one of these was recomputed from the ledger above.
        Assert.True(outstandingPrices.Count == 0, Universe.Inv($"{outstandingPrices.Count} price requests are still outstanding; the price phase is not complete"));
        Assert.True(outstandingSplits.Count == 352, Universe.Inv($"{outstandingSplits.Count} outstanding splits, not 352"));
        Assert.True(outstanding == 352, Universe.Inv($"{outstanding} outstanding, not 352"));
        Assert.True(prior.Total == 414, Universe.Inv($"{prior.Total} consumed, not 414"));
        Assert.True(available == 290, Universe.Inv($"{available} available, not 290"));
        Assert.True(shortfall == 62, Universe.Inv($"the shortfall is {shortfall}, not 62"));
        Assert.True(suppressiblePrices == 355, Universe.Inv($"{suppressiblePrices} price fingerprints are satisfied, not 355"));

        Assert.Empty(unauthorisedOutstanding);

        // Gate 12, and Gate 9 empirically: re-planning completed work created nothing.
        Assert.True(ObservationDeduplication.Passes(priceRows.Rows, priceRows.Distinct), "gate 12: prices");
        Assert.True(ObservationDeduplication.Passes(splitRows.Rows, splitRows.Distinct), "gate 12: splits");
        Assert.True(ObservationDeduplication.Passes(financialRows.Rows, financialRows.Distinct), "gate 12: financials");
        Assert.Equal(runsBefore, runsAfterReplan);
        Assert.Equal(observationsBefore, observationsAfterReplan);

        // Nothing dispatched during this review, and the batch artefacts still record what they
        // recorded when it started.
        Assert.True(
            prior.Batches == 205,
            Universe.Inv($"{prior.Batches} dispatches are recorded against batch artefacts, not 205 - a batch has run"));

        // The counter cannot be credited back, structurally rather than by inspection.
        Assert.Empty(settableCounters);
        Assert.DoesNotContain(mutators, m => m.Contains("Release", StringComparison.Ordinal)
            || m.Contains("Refund", StringComparison.Ordinal)
            || m.Contains("Reset", StringComparison.Ordinal)
            || m.Contains("Credit", StringComparison.Ordinal));

        // Nothing this phase acquired left the approved price scope.
        Assert.Equal(0, pricesOutsideWindow);
        Assert.Equal(0, pricesForExcludedMember);
        Assert.Equal([PriceSource], dispatchedSources);
        Assert.DoesNotContain(ledgerSources, x => x.Contains("dividend", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ledgerSources, x => x.Contains("benchmark", StringComparison.OrdinalIgnoreCase));

        // The superseded declaration was read and not written.
        Assert.True(
            declarationBefore.SequenceEqual(await File.ReadAllBytesAsync(declarationPath)),
            "the existing authorisation declaration changed");

        // The draft covers exactly the outstanding split work and nothing else.
        Assert.Equal([ActionsSource], proposed.Sources.Order(StringComparer.Ordinal).ToList());
        Assert.True(proposed.Symbols.Count == 355, Universe.Inv($"{proposed.Symbols.Count} symbols in the draft, not 355"));
        Assert.Equal(355, proposed.PlannedRequests);
        Assert.Equal(3, proposed.AlreadySatisfied);
        Assert.Equal(352, proposed.DispatchCeiling);
        Assert.Equal(outstandingSplits.Count, proposed.DispatchCeiling);
        Assert.Equal(DateOnly.FromDateTime(WindowStart), proposed.WindowFrom);
        Assert.Equal(DateOnly.FromDateTime(WindowEnd), proposed.WindowTo);
        Assert.NotEqual(authorization.AuthorizationId, proposed.AuthorizationId);
        Assert.NotEqual(authorization.Digest, proposed.Digest);
        Assert.Equal(0, proposed.Consumed);

        // Every authorised split symbol is one the sealed universe already names.
        Assert.All(proposed.Symbols, symbol => Assert.Contains(symbol, authorization.Symbols));

        // Gate 6 is expected to fail while the acquisition is incomplete. Asserting that it fails
        // keeps this review honest: if it ever passes here, the acquisition finished and this
        // report is describing a state that no longer exists.
        // Gate 6 still fails, but every remaining fault is now about the data the vendor returned
        // rather than about work not yet done: not-yet-acquired is zero.
        Assert.Equal(0, pending);
        Assert.False(sixComplete, "gate 6 passes with the acquisition declared complete, which contradicts 352 outstanding split requests");
    }

    // ---- reading -------------------------------------------------------------------------------------------

    /// <summary>
    /// Characterises one payload's rows without applying, or being, a normalisation rule.
    /// </summary>
    /// <remarks>
    /// This reads and reports. It does not decide what the platform should do with a row, and
    /// nothing it computes is written back to a payload, a quarantine record or an observation.
    /// </remarks>
    private static RowSurvey Characterise(string symbols, DateTime quarantinedAtUtc, JsonElement array)
    {
        var bad = new List<BadRow>();
        var readableIndices = new List<int>();

        DateOnly? lastReadable = null;
        var index = 0;

        foreach (var element in array.EnumerateArray())
        {
            index++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                bad.Add(BadRow.Shaped(index, "not an object", Raw(element)));
                continue;
            }

            var date = element.TryGetProperty("date", out var d) &&
                d.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : (DateOnly?)null;

            if (date is null)
            {
                bad.Add(BadRow.Shaped(index, "no usable date", Raw(element)));
                continue;
            }

            var close = Number(element, "close");

            if (close is null)
            {
                bad.Add(BadRow.Shaped(index, "close absent or not a number", Raw(element)));
                continue;
            }

            if (close > 0m)
            {
                readableIndices.Add(index);
                lastReadable = date;
                continue;
            }

            var numbers = NumericFields
                .Select(f => (Field: f, Value: Number(element, f)))
                .ToList();

            var volume = numbers.Find(n => string.Equals(n.Field, "volume", StringComparison.Ordinal)).Value ?? 0m;

            bad.Add(new BadRow(
                index,
                date,
                "close is zero or negative",
                Raw(element),
                ZeroPriced: true,
                AllNumericZero: numbers.TrueForAll(n => n.Value == 0m),
                NonZeroVolumeOnly: volume != 0m &&
                    numbers.Where(n => !string.Equals(n.Field, "volume", StringComparison.Ordinal))
                        .All(n => n.Value == 0m),
                Volume: volume,
                Terminal: false,
                AfterLastReadable: false));
        }

        var lastReadableIndex = readableIndices.Count == 0 ? 0 : readableIndices[^1];

        var resolved = bad
            .Select(b => b with
            {
                Terminal = b.Index > lastReadableIndex,
                AfterLastReadable = lastReadable is null || (b.Date is { } bd && bd > lastReadable),
            })
            .ToList();

        var firstBad = resolved.Count == 0 ? null : resolved[0];

        return new RowSurvey(
            symbols,
            quarantinedAtUtc,
            index,
            readableIndices.Count,
            resolved,
            AllBadTerminal: readableIndices.Count > 0 && resolved.TrueForAll(b => b.Terminal),
            InteriorBad: resolved.Count(b => !b.Terminal),
            LastReadable: lastReadable?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FirstBadDescription: firstBad is null
                ? "-"
                : Universe.Inv($"row {firstBad.Index} ({firstBad.Date:yyyy-MM-dd})"),
            GapDescription: firstBad?.Date is { } f && lastReadable is { } l
                ? Universe.Inv($"{f.DayNumber - l.DayNumber} day(s)")
                : "-");
    }

    private static decimal? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string Raw(JsonElement element)
    {
        var text = element.GetRawText().Replace("\n", " ", StringComparison.Ordinal);

        return text.Length <= 150 ? text : text[..150] + "…";
    }

    private static string Trim(string reason) =>
        reason.Length <= 110 ? reason : reason[..110] + "…";

    /// <summary>The symbols whose archived end-of-day payload was an empty array.</summary>
    private static async Task<HashSet<string>> EmptySeriesSymbolsAsync(
        AppDbContext context,
        IRawResponseArchive archive)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        var all = await context.QuarantinedPayloads.AsNoTracking().ToListAsync();
        var runs = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var quarantined = all
            .Where(q => string.Equals(q.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .ToList();

        foreach (var row in quarantined)
        {
            var bytes = await archive.RetrieveAsync(row.Id);

            if (bytes is null)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes).Trim();

            if (!string.Equals(text, "[]", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var run in runs.Where(r => r.Artifacts.Any(h => string.Equals(h.Value, row.Id.Value, StringComparison.Ordinal))))
            {
                if (run.Request.Subject.Identifier is { } identifier)
                {
                    symbols.Add(identifier);
                }
            }
        }

        return symbols;
    }

    private static async Task<Measured> MeasureAsync(
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

        var distinct = rows
            .Select(r => ObservationDeduplication.Key(
                r.Kind, r.Identifier, r.Attribute, r.AsOfUtc, r.PublishedAtUtc, r.Canonical))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new Measured(rows.Count, distinct);
    }

    /// <summary>What every process has spent against the authorisation, from what each wrote.</summary>
    private static async Task<Prior> PriorConsumptionAsync()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var outcomeTotal = 0;
        var batchTotal = 0;

        var outcome = Path.Combine(universe, "acquisition-outcome.json");

        if (File.Exists(outcome))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outcome));

            outcomeTotal = document.RootElement.GetProperty("AuthorizationConsumed").GetInt32();
        }

        foreach (var path in Directory
            .EnumerateFiles(universe, "acquisition-batch-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            batchTotal += document.RootElement.GetProperty("AuthorizationConsumedThisRun").GetInt32();
        }

        return new Prior(outcomeTotal, batchTotal);
    }

    /// <summary>
    /// The authorisation digest, in the shape the loader recomputes it.
    /// </summary>
    /// <remarks>
    /// Mirrored rather than called, because the real one is private and this stage must not change
    /// it. The mirror cannot drift undetected: the draft it produces is handed straight back to
    /// <see cref="AcquisitionAuthorization.Load"/>, which recomputes the digest with the real
    /// algorithm and refuses the file if the two disagree.
    /// </remarks>
    private static string AuthorizationDigest(
        string evidenceBase,
        string vendor,
        IReadOnlyList<string> sources,
        DateOnly from,
        DateOnly to,
        int planned,
        int satisfied,
        int ceiling,
        IReadOnlyList<string> symbols)
    {
        var canonical = new StringBuilder();

        canonical.Append(AcquisitionAuthorization.Schema).Append('\n');
        canonical.Append(evidenceBase).Append('\n');
        canonical.Append(vendor).Append('\n');
        canonical.Append(string.Join(",", sources)).Append('\n');
        canonical.Append(Universe.Inv($"{from:yyyy-MM-dd}")).Append('\n');
        canonical.Append(Universe.Inv($"{to:yyyy-MM-dd}")).Append('\n');
        canonical.Append(planned.ToString(CultureInfo.InvariantCulture)).Append('\n');
        canonical.Append(satisfied.ToString(CultureInfo.InvariantCulture)).Append('\n');
        canonical.Append(ceiling.ToString(CultureInfo.InvariantCulture));

        foreach (var symbol in symbols)
        {
            canonical.Append('\n').Append(symbol);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    /// <summary>
    /// Every identifier an excluded member could appear under, in both notations.
    /// </summary>
    /// <remarks>
    /// A member the universe excluded may have been requested as a bare ticker by an earlier stage
    /// or as a suffixed symbol by this one, and a check that knew only one of the two would miss
    /// exactly the case it exists to catch.
    /// </remarks>
    private static async Task<HashSet<string>> ExcludedSymbolsAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        var excluded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in identity.RootElement.GetProperty("Members").EnumerateArray())
        {
            if (member.TryGetProperty("Ready", out var ready) && ready.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (member.TryGetProperty("Ticker", out var t) &&
                t.ValueKind == JsonValueKind.String &&
                t.GetString() is { Length: > 0 } ticker)
            {
                excluded.Add(ticker);
                excluded.Add(AcquisitionPlanning.SymbolFor(ticker));
            }
        }

        return excluded;
    }

    /// <summary>Every source any recorded batch or run artefact says it actually dispatched.</summary>
    private static async Task<List<string>> BatchSourcesAsync()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var sources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory
            .EnumerateFiles(universe, "acquisition-batch-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            foreach (var attempt in document.RootElement.GetProperty("Attempts").EnumerateArray())
            {
                if (attempt.TryGetProperty("Source", out var source) &&
                    source.GetString() is { } value)
                {
                    sources.Add(value);
                }
            }
        }

        return sources.Order(StringComparer.Ordinal).ToList();
    }

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

                var survives = list.Count > 0 &&
                    string.Equals(list[^1], finalCut, StringComparison.Ordinal);

                var spanFrom = list.Count == 0
                    ? windowFrom
                    : DateOnly.Parse(list[0], CultureInfo.InvariantCulture);

                var spanTo = list.Count == 0 || survives
                    ? windowTo
                    : DateOnly.Parse(list[^1], CultureInfo.InvariantCulture);

                var ticker = m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                var isReady = m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True;

                return new Member(
                    cik,
                    isReady,
                    isReady && ticker is not null ? AcquisitionPlanning.SymbolFor(ticker) : null,
                    m.TryGetProperty("SecurityKind", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString()
                        : null,
                    spanFrom < windowFrom ? windowFrom : spanFrom,
                    spanTo < windowFrom ? windowFrom : spanTo);
            })
            .ToList();
    }

    private sealed record Prior(int Outcome, int Batches)
    {
        public int Total => Outcome + Batches;
    }

    private sealed record Measured(int Rows, int Distinct);

    private sealed record Faulted(string Symbol, string Bucket, string Kinds, string Reason);

    private sealed record Member(
        string Cik,
        bool Ready,
        string? Symbol,
        string? SecurityKind,
        DateOnly SpanFrom,
        DateOnly SpanTo);

    /// <param name="Index">Its one-based position in the payload.</param>
    /// <param name="Date">The row's own date, when it has a usable one.</param>
    /// <param name="Kind">Why the row check refused it.</param>
    /// <param name="Raw">The row verbatim - six numbers and a date, no credential.</param>
    /// <param name="ZeroPriced">A parseable date and a close of zero or less.</param>
    /// <param name="AllNumericZero">Open, high, low, close, adjusted close and volume all zero.</param>
    /// <param name="NonZeroVolumeOnly">Volume alone is non-zero: the sentinel hypothesis's counterexample.</param>
    /// <param name="Volume">The reported volume, verbatim.</param>
    /// <param name="Terminal">No readable row follows it.</param>
    /// <param name="AfterLastReadable">Its date is later than the payload's last readable session.</param>
    private sealed record BadRow(
        int Index,
        DateOnly? Date,
        string Kind,
        string Raw,
        bool ZeroPriced,
        bool AllNumericZero,
        bool NonZeroVolumeOnly,
        decimal Volume,
        bool Terminal,
        bool AfterLastReadable)
    {
        public static BadRow Shaped(int index, string kind, string raw) =>
            new(index, null, kind, raw, false, false, false, 0m, false, false);
    }

    private sealed record RowSurvey(
        string Symbols,
        DateTime QuarantinedAtUtc,
        int Rows,
        int Readable,
        List<BadRow> Bad,
        bool AllBadTerminal,
        int InteriorBad,
        string? LastReadable,
        string FirstBadDescription,
        string GapDescription);
}
