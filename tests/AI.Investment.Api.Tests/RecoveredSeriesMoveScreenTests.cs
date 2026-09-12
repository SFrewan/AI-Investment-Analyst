using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The eleven locally recoverable price series, put through the production split adjuster.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, in memory, and nothing is kept.</strong> No connector is enabled and no
/// request leaves the process. Nothing is persisted, reclassified, released or removed: run,
/// observation and quarantine counts are asserted unchanged either side, and every quarantined
/// payload is asserted still quarantined under the same rule. The normaliser,
/// <see cref="SplitAdjustment"/>, <see cref="CoverageEvaluation"/> and the sealed Gate 6
/// declaration are read and not touched.
/// </para>
/// <para>
/// <strong>What this answers, and why it had to be asked.</strong> The re-read diagnostic
/// established that 6,772 rows are recoverable from eleven quarantined payloads once the rows the
/// normaliser refuses are held back. It projected that ten of the eleven would then be clean under
/// Gate 6. That projection was conditional on a screen it did not run:
/// <see cref="SplitAdjustment.Apply"/> refuses a whole series on any session-to-session move of
/// <see cref="SplitAdjustment.DefaultMaxUnexplainedMove"/> or more that no known split explains,
/// and such a refusal is itself a Gate 6 fault. All eleven members hold their splits in the same
/// store this reads, so the screen can be run now, on the recovered rows, with nothing acquired.
/// </para>
/// <para>
/// <strong>The recovered rows are the normaliser's, not this test's.</strong> Each payload is
/// replayed exactly as <see cref="PricePayloadRereadTests"/> replays it - the normaliser fails
/// fast, so the row it names is removed from the array in memory and the reduced array is replayed
/// again, until the normaliser accepts what is left. The observations screened below are the ones
/// that final accepting replay returned. No row is repaired, coerced, reordered or invented, and
/// no gap is filled or interpolated.
/// </para>
/// <para>
/// <strong>The screen is production's, called the way production calls it.</strong> Splits are read
/// from the observation store under their own attribute and resolved to
/// <see cref="ShareSplit"/> by the same rules
/// <c>PriceSeriesReader.Splits</c> uses - one per effective instant, the latest published winning,
/// oldest first - and the closes are resolved by the same rules its <c>Resolve</c> uses. The
/// tolerance is <see cref="SplitAdjustment.DefaultMaxUnexplainedMove"/> read from the domain, never
/// a literal. The one deliberate difference is that production would first window the series to
/// its screen's session count; this passes the <em>whole</em> recovered series, which is the
/// strictest form of the question - every session pair the member has, not just the recent ones.
/// </para>
/// <para>
/// <strong>Gate 6 is executed, not projected.</strong> Section 5 of the report runs the existing
/// <see cref="CoverageEvaluation"/> over all 355 members twice: once on what the store holds, and
/// once on an in-memory dictionary that adds the recovered dates for the eleven. Both fault counts
/// are read from the same implementation. The as-is run is asserted against the count the previous
/// stages measured, so a mistake in reconstructing the membership spans fails this test rather than
/// quietly biasing the hypothetical.
/// </para>
/// </remarks>
public sealed class RecoveredSeriesMoveScreenTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_MOVE_SCREEN";

    private const string PriceSource = "eodhd-eod";

    /// <summary>A guard on the peel loop, so a reason this cannot parse cannot spin.</summary>
    private const int MaxPeels = 200;

    /// <summary>Fault members Gate 6 reports today, measured by the stages before this one.</summary>
    private const int MeasuredFaultMembers = 24;

    /// <summary>Members holding a price series today, measured by the stages before this one.</summary>
    private const int MeasuredWithSeries = 339;

    private static readonly Regex RowIndex = new(
        @"^Row (\d+):", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public RecoveredSeriesMoveScreenTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Recovered_price_series_are_screened_by_the_production_split_adjuster()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The recovered-series move screen is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var normalizer = services.GetServices<INormalizer>()
            .OfType<EodhdDailyPriceNormalizer>()
            .Single();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        // ================================================================================
        // 1. Rebuild the recovered series, exactly as the re-read produced them
        // ================================================================================

        var everyQuarantine = await context.QuarantinedPayloads
            .AsNoTracking()
            .OrderBy(q => q.QuarantinedAtUtc)
            .ToListAsync();

        var quarantined = everyQuarantine
            .Where(q => string.Equals(q.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .ToList();

        var runs = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var priceRuns = runs
            .Where(r => string.Equals(r.Request.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .SelectMany(r => r.Artifacts.Select(h => (Hash: h.Value, Run: r)))
            .GroupBy(x => x.Hash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Run).ToList(), StringComparer.Ordinal);

        // ---- the splits the platform actually holds, resolved production's way -------------------

        var splitRows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute)
            .Select(o => new
            {
                o.Subject.Identifier,
                o.Value.Kind,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
                o.Provenance.RetrievedAtUtc,
            })
            .ToListAsync();

        var recovered = new List<Screened>();

        foreach (var row in quarantined)
        {
            if (!priceRuns.TryGetValue(row.Id.Value, out var claimants) || claimants.Count == 0)
            {
                continue;
            }

            var bytes = await archive.RetrieveAsync(row.Id);

            if (bytes is null)
            {
                continue;
            }

            foreach (var run in claimants
                .GroupBy(r => r.Request.Subject.Identifier ?? "(none)", StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(r => r.Request.Subject.Identifier, StringComparer.Ordinal))
            {
                var request = run.Request;
                var retrievedAtUtc = run.StartedAtUtc;
                var hash = row.Id;
                var symbol = request.Subject.Identifier ?? "(none)";

                var replay = await ReplayAsync(
                    normalizer,
                    payload => new NormalizationInput(
                        request.SourceId,
                        request.Category,
                        request.Subject,
                        hash,
                        payload,
                        retrievedAtUtc),
                    bytes);

                // A payload that is still refused after every named row has been peeled recovers
                // nothing. The empty arrays and the free-tier probe land here, and this screen has
                // nothing to say about them: there is no series to adjust.
                if (replay.Accepted.Count == 0)
                {
                    continue;
                }

                // Resolved by the rules `PriceSeriesReader.Splits` uses, in its order: numeric
                // values only, one per effective instant with the latest published winning, oldest
                // first. Nothing is inferred - a member with no split rows gets an empty list, and
                // the adjuster is then an identity that still walks the series for discontinuities.
                var splits = splitRows
                    .Where(s => string.Equals(s.Identifier, symbol, StringComparison.Ordinal))
                    .Where(s => s.Kind == ObservationValueKind.Number)
                    .GroupBy(s => s.AsOfUtc)
                    .Select(g => g
                        .OrderByDescending(s => s.PublishedAtUtc)
                        .ThenByDescending(s => s.RetrievedAtUtc)
                        .First())
                    .OrderBy(s => s.AsOfUtc)
                    .Select(s => new ShareSplit(
                        s.AsOfUtc,
                        decimal.Parse(s.Canonical, NumberStyles.Float, CultureInfo.InvariantCulture)))
                    .ToList();

                recovered.Add(Screen(symbol, row.Id.Abbreviated, replay, splits));
            }
        }

        recovered = recovered.OrderBy(r => r.Symbol, StringComparer.Ordinal).ToList();

        var passed = recovered.Count(r => r.Usable);
        var failed = recovered.Count - passed;

        // ================================================================================
        // 2. Gate 6, executed against the same implementation, three ways
        // ================================================================================

        var members = await MembersAsync();

        var priceRowsHeld = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .Select(o => new { o.Subject.Identifier, o.Provenance.AsOfUtc })
            .ToListAsync();

        var held = priceRowsHeld
            .Where(r => r.Identifier is not null)
            .GroupBy(r => r.Identifier!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => DateOnly.FromDateTime(r.AsOfUtc)).Distinct().ToList(),
                StringComparer.Ordinal);

        var requested = runs
            .Where(r => r.Outcome == IngestionOutcome.Succeeded
                && r.Request.Category == DataCategory.MarketPrices
                && r.Request.Subject.Identifier is not null)
            .Select(r => r.Request.Subject.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var asIs = EvaluateGateSix(members, held, requested);

        // The hypothetical dictionary is a copy. The store is not touched, and `held` above is not
        // mutated, so the as-is verdict cannot be contaminated by the one below it.
        var hypothetical = new Dictionary<string, List<DateOnly>>(held, StringComparer.Ordinal);

        foreach (var r in recovered)
        {
            hypothetical[r.Symbol] = r.Dates.ToList();
        }

        var withRecovered = EvaluateGateSix(members, hypothetical, requested);

        // The same again, but only crediting the members whose recovered series the production
        // screen actually accepts. A refused series is one the platform will not read, so counting
        // its dates as coverage would be claiming something the adjuster has just denied.
        var screened = new Dictionary<string, List<DateOnly>>(held, StringComparer.Ordinal);

        foreach (var r in recovered.Where(r => r.Usable))
        {
            screened[r.Symbol] = r.Dates.ToList();
        }

        var withScreened = EvaluateGateSix(members, screened, requested);

        // ================================================================================
        // 3. The report
        // ================================================================================

        report.AppendLine("# The eleven recovered price series, screened by the production split adjuster");
        report.AppendLine();
        report.AppendLine("**In-memory and read-only.** No provider was called, no connector enabled,");
        report.AppendLine("nothing persisted, reclassified, released or removed. The normaliser,");
        report.AppendLine("`SplitAdjustment`, `CoverageEvaluation` and the sealed Gate 6 declaration are");
        report.AppendLine("unchanged. Every verdict below is production code's own, obtained by calling it");
        report.AppendLine("and reading what it returned.");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Threshold in force: `SplitAdjustment.DefaultMaxUnexplainedMove` = **{Percent(SplitAdjustment.DefaultMaxUnexplainedMove)}**, read from the domain, not restated here."));
        report.AppendLine();

        // ---- Section 1 ---------------------------------------------------------------------------

        report.AppendLine("## Section 1 — Executive summary");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Recovered series screened | **{recovered.Count}** |"));
        report.AppendLine(Universe.Inv($"| `SplitAdjustment.Apply` returns a usable series | **{passed}** |"));
        report.AppendLine(Universe.Inv($"| `SplitAdjustment.Apply` refuses the series | **{failed}** |"));
        report.AppendLine(Universe.Inv($"| …refused as an unexplained discontinuity | {recovered.Count(r => r.Refusal == SeriesRefusal.UnexplainedDiscontinuity)} |"));
        report.AppendLine(Universe.Inv($"| …refused for an unusable split ratio | {recovered.Count(r => r.Refusal == SeriesRefusal.UnusableSplitRatio)} |"));
        report.AppendLine(Universe.Inv($"| Recovered observations across all of them | **{recovered.Sum(r => r.Dates.Count)}** |"));
        report.AppendLine(Universe.Inv($"| Split observations held locally for these members | **{recovered.Sum(r => r.Splits)}** |"));
        report.AppendLine();

        if (failed == 0)
        {
            report.AppendLine("**Every recovered series passes.** The production adjuster returns a usable");
            report.AppendLine("series for all of them, so no member acquires an `unexplained-discontinuity`");
            report.AppendLine("fault by being recovered, and the re-read's conditional projection survives");
            report.AppendLine("this screen unchanged.");
        }
        else
        {
            report.AppendLine(Universe.Inv(
                $"**{failed} of {recovered.Count} recovered series are refused by the production adjuster.**"));
            report.AppendLine("A refusal is itself a Gate 6 fault, so for these members recovering the rows");
            report.AppendLine("does not clear the fault - it changes which fault is reported. Section 3 gives");
            report.AppendLine("the exact session pair and move behind each refusal.");
        }

        report.AppendLine();
        report.AppendLine("Whether the re-read's *\"10 of 11 clean\"* conclusion survives is answered in");
        report.AppendLine("Section 5 from an executed Gate 6 run, not from arithmetic on this table.");
        report.AppendLine();

        // ---- Section 2 ---------------------------------------------------------------------------

        report.AppendLine("## Section 2 — Per-member results");
        report.AppendLine();
        report.AppendLine("| # | Symbol | Payload | Recovered obs. | First | Last | Invalid rows removed | Splits held | The splits, as held | Apply succeeds? | Adjusted obs. | Unexplained move? | Date(s) | Previous close | Current close | Move | Threshold | Failure class | Control: splits removed | Cause |");
        report.AppendLine("| ---: | --- | --- | ---: | --- | --- | ---: | ---: | --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- | --- |");

        var index = 0;
        var tolerance = Percent(SplitAdjustment.DefaultMaxUnexplainedMove);

        foreach (var r in recovered)
        {
            index++;

            var succeeds = r.Usable ? "**yes**" : "**no**";
            var adjusted = r.Usable ? r.Adjusted.ToString(CultureInfo.InvariantCulture) : "0 - refused";

            report.AppendLine(Universe.Inv(
                $"| {index} | `{r.Symbol}` | `{r.Payload}` | **{r.Dates.Count}** | {Text(r.Dates[0])} | {Text(r.Dates[^1])} | {r.Removed} | {r.Splits} | {r.SplitDetail} | {succeeds} | {adjusted} | {r.MoveFound} | {r.MoveDates} | {r.PreviousClose} | {r.CurrentClose} | {r.MovePercent} | {tolerance} | {r.FailureClass} | {r.Control} | {r.Cause} |"));
        }

        report.AppendLine();
        report.AppendLine("**Reading the columns.** *Invalid rows removed* is the count of rows the");
        report.AppendLine("normaliser itself named and refused, removed in memory only. *Splits held* is");
        report.AppendLine("what the observation store holds for that symbol under");
        report.AppendLine("`security.split-ratio`, resolved the way the production reader resolves it.");
        report.AppendLine("*Adjusted obs.* is the length of the series `SplitAdjustment.Apply` returned;");
        report.AppendLine("a refusal returns an empty list by design, so it reads 0. *Previous close* and");
        report.AppendLine("*current close* are the two closes at the sessions the refusal names, looked up");
        report.AppendLine("in the series the adjuster was given, and are blank when there is no refusal to");
        report.AppendLine("name them. Where a member holds no split - which is the case for every member");
        report.AppendLine("below - the adjustment is the identity, so those closes are both the raw and the");
        report.AppendLine("adjusted ones.");
        report.AppendLine();

        // ---- Section 3 ---------------------------------------------------------------------------

        report.AppendLine("## Section 3 — Unexplained-move detail");
        report.AppendLine();

        var refusals = recovered.Where(r => !r.Usable).ToList();

        if (refusals.Count == 0)
        {
            report.AppendLine("**No refusals.** `SplitAdjustment.Apply` returned a usable series for every");
            report.AppendLine("one of the recovered members, so there is no unexplained move to detail.");
            report.AppendLine();
            report.AppendLine("That is a stronger statement than it looks, and it is worth saying why it is");
            report.AppendLine("not circular. The adjuster walks *every* consecutive session pair in the");
            report.AppendLine("series it is given and refuses on the first move over the tolerance. These");
            report.AppendLine("series were passed whole rather than windowed, so every pair each member");
            report.AppendLine("holds was examined. The table below reports, for each member, the largest");
            report.AppendLine("single-session move actually present, so the margin against the threshold is");
            report.AppendLine("visible rather than implied.");
            report.AppendLine();
        }
        else
        {
            report.AppendLine("Each refusal below is `SplitAdjustment.Apply`'s own, quoted verbatim. The");
            report.AppendLine("adjuster stops at the first move over the tolerance, so the named pair is the");
            report.AppendLine("earliest one, not necessarily the largest.");
            report.AppendLine();
            report.AppendLine("Each carries a **control**: the same recovered rows passed to the same");
            report.AppendLine("function with the split list emptied. That is what attributes the refusal to a");
            report.AppendLine("cause without anyone reasoning about the arithmetic. Still refused means the");
            report.AppendLine("discontinuity is in the data; accepted means restating the data by a stored");
            report.AppendLine("split is what produced it.");
            report.AppendLine();

            foreach (var r in refusals)
            {
                report.AppendLine(Universe.Inv($"### `{r.Symbol}`"));
                report.AppendLine();
                report.AppendLine("| | |");
                report.AppendLine("| --- | --- |");
                report.AppendLine(Universe.Inv($"| Refusal | `{r.Refusal}` |"));
                report.AppendLine(Universe.Inv($"| Session pair | {r.MoveDates} |"));
                report.AppendLine(Universe.Inv($"| Previous close | {r.PreviousClose} |"));
                report.AppendLine(Universe.Inv($"| Current close | {r.CurrentClose} |"));
                report.AppendLine(Universe.Inv($"| Move | {r.MovePercent} |"));
                report.AppendLine(Universe.Inv($"| Threshold | {Percent(SplitAdjustment.DefaultMaxUnexplainedMove)} |"));
                report.AppendLine(Universe.Inv($"| Splits held locally for this symbol | {r.Splits} - {r.SplitDetail} |"));
                report.AppendLine(Universe.Inv($"| Recovered observations | {r.Dates.Count} |"));
                report.AppendLine(Universe.Inv($"| Control - the same rows with the split list emptied | **{r.Control}** |"));
                report.AppendLine(Universe.Inv($"| Cause | {r.Cause} |"));
                report.AppendLine();
                report.AppendLine("The adjuster's own words:");
                report.AppendLine();
                report.AppendLine(Universe.Inv($"> {r.Explanation}"));
                report.AppendLine();
            }
        }

        report.AppendLine("**Largest single-session move present in each recovered series.** Enumerated over");
        report.AppendLine("the series the adjuster was given, with the adjuster's own arithmetic - absolute");
        report.AppendLine("change over the previous close, non-positive closes skipped. For a member holding");
        report.AppendLine("no splits the adjustment is the identity, so this is the adjusted series and not");
        report.AppendLine("a substitute for it. Where a split restated a series that was then refused, the");
        report.AppendLine("adjuster returned nothing to walk, and the *raw* series is walked instead - the");
        report.AppendLine("basis column says which, rather than letting one stand in for the other.");
        report.AppendLine();
        report.AppendLine("| Symbol | Basis | Session pairs walked | Moves over threshold | Largest move | Where | Margin to threshold |");
        report.AppendLine("| --- | --- | ---: | ---: | ---: | --- | ---: |");

        foreach (var r in recovered)
        {
            report.AppendLine(Universe.Inv(
                $"| `{r.Symbol}` | {r.Basis} | {Math.Max(r.Dates.Count - 1, 0)} | {r.OverThreshold} | {r.LargestMove} | {r.LargestMoveWhere} | {r.Margin} |"));
        }

        report.AppendLine();

        // ---- Section 4 ---------------------------------------------------------------------------

        report.AppendLine("## Section 4 — `SHPW.US`, the interior invalid block");
        report.AppendLine();

        var shpw = recovered.FirstOrDefault(r =>
            string.Equals(r.Symbol, "SHPW.US", StringComparison.Ordinal));

        if (shpw is null)
        {
            report.AppendLine("`SHPW.US` did not appear among the recovered series in this run, which");
            report.AppendLine("contradicts the re-read. That is reported rather than explained away.");
            report.AppendLine();
        }
        else
        {
            report.AppendLine("The twenty rows the normaliser refuses in this payload are not terminal. They");
            report.AppendLine("are a contiguous interior block, and removing them leaves a hole in the middle");
            report.AppendLine("of the series rather than shortening its end. Nothing here fills or");
            report.AppendLine("interpolates that hole: the two closes either side of it simply become");
            report.AppendLine("consecutive, which is exactly the input the adjuster would be given.");
            report.AppendLine();
            report.AppendLine("| | |");
            report.AppendLine("| --- | --- |");
            report.AppendLine(Universe.Inv($"| Recovered observations | **{shpw.Dates.Count}** |"));
            report.AppendLine(Universe.Inv($"| Invalid rows removed | {shpw.Removed} |"));
            report.AppendLine(Universe.Inv($"| Recovered span | {Text(shpw.Dates[0])} .. {Text(shpw.Dates[^1])} |"));
            report.AppendLine(Universe.Inv($"| Splits held locally | **{shpw.Splits}** - {shpw.SplitDetail} |"));
            report.AppendLine(Universe.Inv($"| Last close before the block | {shpw.BeforeGapClose} on {shpw.BeforeGapDate} |"));
            report.AppendLine(Universe.Inv($"| First close after the block | {shpw.AfterGapClose} on {shpw.AfterGapDate} |"));
            var shpwVerdict = shpw.Usable
                ? "usable"
                : Universe.Inv($"refused - {shpw.Refusal}");

            report.AppendLine(Universe.Inv($"| Move across the join | **{shpw.GapMove}** |"));
            report.AppendLine(Universe.Inv($"| Threshold | {Percent(SplitAdjustment.DefaultMaxUnexplainedMove)} |"));
            report.AppendLine(Universe.Inv($"| Does the join breach the threshold? | **{shpw.GapBreaches}** |"));
            report.AppendLine(Universe.Inv($"| `SplitAdjustment.Apply` verdict | **{shpwVerdict}** |"));
            var shpwJoinPair = Universe.Inv($"{shpw.BeforeGapDate} → {shpw.AfterGapDate}");

            var shpwSamePair = string.Equals(shpw.MoveDates, shpwJoinPair, StringComparison.Ordinal)
                ? "yes"
                : "NO - a different pair entirely";

            report.AppendLine(Universe.Inv($"| Session pair the refusal actually names | **{shpw.MoveDates}** |"));
            report.AppendLine(Universe.Inv($"| Is that the join across the block? | **{shpwSamePair}** |"));
            report.AppendLine(Universe.Inv($"| Control - the same rows with the split list emptied | **{shpw.Control}** |"));
            report.AppendLine(Universe.Inv($"| Cause of the refusal | {shpw.Cause} |"));
            report.AppendLine();
            report.AppendLine("**What this settles.** A gap is not by itself a discontinuity: the adjuster");
            report.AppendLine("compares consecutive *observations*, not consecutive calendar sessions, and");
            report.AppendLine("has no notion of how much time passed between them. So the interior block");
            report.AppendLine("matters to this screen only through the size of the step across the join. The");
            report.AppendLine("row above states that step and whether it breached the tolerance; the Gate 6");
            report.AppendLine("consequence of the hole itself - an interior gap beyond the four sessions a");
            report.AppendLine("holiday run can explain - is a separate fault, and Section 5 measures it by");
            report.AppendLine("running the gate rather than by reasoning about it.");
            report.AppendLine();
        }

        // ---- Section 5 ---------------------------------------------------------------------------

        report.AppendLine("## Section 5 — Gate 6 impact, executed");
        report.AppendLine();
        report.AppendLine("Nothing in this section is projected. `CoverageEvaluation` - the sealed rule made");
        report.AppendLine("executable, unmodified - was run three times over all 355 members, with");
        report.AppendLine("acquisition declared complete, against three in-memory views of coverage. The");
        report.AppendLine("store was read once and never written; the second and third views are copies of");
        report.AppendLine("the first with recovered dates added.");
        report.AppendLine();
        report.AppendLine("| | As the store holds it | + all 11 recovered | + only the 11 the adjuster accepts |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| **Fault members** | **{asIs.FaultMembers}** | **{withRecovered.FaultMembers}** | **{withScreened.FaultMembers}** |"));
        report.AppendLine(Universe.Inv($"| `no-series` | {asIs.NoSeries} | {withRecovered.NoSeries} | {withScreened.NoSeries} |"));
        report.AppendLine(Universe.Inv($"| `interior-gap` (fault events) | {asIs.InteriorGaps} | {withRecovered.InteriorGaps} | {withScreened.InteriorGaps} |"));
        report.AppendLine(Universe.Inv($"| `interior-gap` (fault members) | {asIs.GapMembers} | {withRecovered.GapMembers} | {withScreened.GapMembers} |"));
        report.AppendLine(Universe.Inv($"| `unexplained-discontinuity` | {asIs.Discontinuities} | {withRecovered.Discontinuities} | {withScreened.Discontinuities} |"));
        report.AppendLine(Universe.Inv($"| `not-yet-acquired` | {asIs.Pending} | {withRecovered.Pending} | {withScreened.Pending} |"));
        report.AppendLine(Universe.Inv($"| Members holding a series | {asIs.WithSeries} / 355 | {withRecovered.WithSeries} / 355 | {withScreened.WithSeries} / 355 |"));
        report.AppendLine(Universe.Inv($"| Total fault events | {asIs.FaultEvents} | {withRecovered.FaultEvents} | {withScreened.FaultEvents} |"));
        report.AppendLine(Universe.Inv($"| **Gate 6 passes?** | **{(asIs.Passes ? "YES" : "NO")}** | **{(withRecovered.Passes ? "YES" : "NO")}** | **{(withScreened.Passes ? "YES" : "NO")}** |"));
        report.AppendLine();
        report.AppendLine("**An important limit on the middle and right columns.** `CoverageEvaluation` is");
        report.AppendLine("the only gate executed here. It reads sessions and judges gaps; it does not call");
        report.AppendLine("the split adjuster. A member whose recovered series the adjuster refuses would");
        report.AppendLine("carry an `unexplained-discontinuity` fault under the sealed rule, and the middle");
        report.AppendLine("column does not know that - which is precisely why the third column exists, and");
        report.AppendLine("why the `unexplained-discontinuity` row is reported from the gate's own count");
        report.AppendLine("rather than from this screen's.");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"The as-is column reproduces the coverage the previous stages measured independently ({MeasuredFaultMembers} fault members, {MeasuredWithSeries} of 355 holding a series). That agreement is asserted, not assumed: if the membership spans were reconstructed wrongly here, this run fails rather than reporting a hypothetical built on a broken baseline."));
        report.AppendLine();

        if (asIs.Faulted.Count > 0)
        {
            report.AppendLine("<details><summary>Every faulted member, as the store holds it</summary>");
            report.AppendLine();

            foreach (var line in asIs.Faulted)
            {
                report.AppendLine(Universe.Inv($"- {line}"));
            }

            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }

        // ---- Section 6 ---------------------------------------------------------------------------

        report.AppendLine("## Section 6 — Remediation implications");
        report.AppendLine();
        report.AppendLine("**Nothing below is implemented, proposed or scheduled.** This is a classification");
        report.AppendLine("of what the local evidence now supports about each recovered member, and nothing");
        report.AppendLine("more. The classes are:");
        report.AppendLine();
        report.AppendLine("- **safe candidate** — the production adjuster accepts the recovered series, and");
        report.AppendLine("  Gate 6 finds no interior gap over its tolerance in it. Every check available");
        report.AppendLine("  locally passes on this member's recovered rows.");
        report.AppendLine("- **requires further investigation** — every check that could be run passes, but");
        report.AppendLine("  one is unresolved: the series carries a Gate 6 interior gap, so recovering it");
        report.AppendLine("  changes which fault the member reports rather than clearing it.");
        report.AppendLine("- **unsafe to recover without additional evidence** — the production adjuster");
        report.AppendLine("  refuses the recovered series. The platform's own screen says it cannot read");
        report.AppendLine("  this series as it stands.");
        report.AppendLine();
        report.AppendLine("| Symbol | Adjuster | Gate 6 on the recovered series | Classification |");
        report.AppendLine("| --- | --- | --- | --- |");

        foreach (var r in recovered)
        {
            var adjuster = r.Usable
                ? "accepts"
                : Universe.Inv($"**refuses** ({r.Refusal})");

            var coverage = r.GateFaults == 0
                ? "clean"
                : Universe.Inv($"**{r.GateFaults} interior-gap fault(s)**, largest {r.LargestGap} session(s)");

            report.AppendLine(Universe.Inv(
                $"| `{r.Symbol}` | {adjuster} | {coverage} | **{Classify(r)}** |"));
        }

        report.AppendLine();
        report.AppendLine("| Classification | Members |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| safe candidate for future row-level quarantine policy consideration | **{recovered.Count(r => Classify(r) == SafeCandidate)}** |"));
        report.AppendLine(Universe.Inv($"| requires further investigation | **{recovered.Count(r => Classify(r) == NeedsInvestigation)}** |"));
        report.AppendLine(Universe.Inv($"| unsafe to recover without additional evidence | **{recovered.Count(r => Classify(r) == Unsafe)}** |"));
        report.AppendLine();
        report.AppendLine("The five members sharing the empty `[]` payload are absent from this table on");
        report.AppendLine("purpose. They recover no rows, so there is no series to screen and no");
        report.AppendLine("classification the local evidence could support. Their status is unchanged:");
        report.AppendLine("empty response confirmed, cause unresolved.");
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "gate6-recovered-series-move-screen.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -----------------------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());

        var after = (await context.QuarantinedPayloads
                .AsNoTracking()
                .OrderBy(q => q.QuarantinedAtUtc)
                .ToListAsync())
            .Where(q => string.Equals(q.SourceId.Value, PriceSource, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            quarantined.Select(q => q.Id.Value + "|" + q.RuleId).ToList(),
            after.Select(q => q.Id.Value + "|" + q.RuleId).ToList());

        // The re-read established eleven recoverable series and 6,772 recoverable rows. If this run
        // rebuilt a different set, every number above describes something else and the report would
        // be quietly wrong rather than loudly absent.
        Assert.Equal(
            "CCF.US EVBG.US GPP.US LGIQ.US NGM.US ONEM.US QUMU.US SDC.US SHPW.US VLDR.US WIRE.US",
            string.Join(" ", recovered.Select(r => r.Symbol)));

        Assert.Equal(6772, recovered.Sum(r => r.Dates.Count));

        // The baseline has to reproduce what the previous stages measured, or the hypothetical is
        // built on a membership reconstruction that does not match the one Gate 6 actually uses.
        Assert.Equal(MeasuredFaultMembers, asIs.FaultMembers);
        Assert.Equal(MeasuredWithSeries, asIs.WithSeries);

        // The tolerance is the domain's, never a literal in this file. Stated as a guard so that a
        // change to it would fail here rather than silently restate every figure above.
        Assert.Equal(0.5m, SplitAdjustment.DefaultMaxUnexplainedMove);
    }

    // ---- classification --------------------------------------------------------------------------

    private const string SafeCandidate = "safe candidate";
    private const string NeedsInvestigation = "requires further investigation";
    private const string Unsafe = "unsafe to recover without additional evidence";

    private static string Classify(Screened r) =>
        !r.Usable ? Unsafe : r.GateFaults > 0 ? NeedsInvestigation : SafeCandidate;

    // ---- the screen ------------------------------------------------------------------------------

    private static Screened Screen(
        string symbol,
        string payload,
        Replay replay,
        List<ShareSplit> splits)
    {
        // Resolved the way the production reader resolves a stored series: one close per session
        // instant, the latest published winning, oldest first. The re-read found no duplicate dates
        // in any of these payloads, so this is expected to be an identity - and the counts either
        // side are reported so that expectation is visible rather than trusted.
        var closes = replay.Accepted
            .GroupBy(o => o.Provenance.AsOfUtc)
            .Select(g => g
                .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                .ThenByDescending(o => o.Provenance.RetrievedAtUtc)
                .First())
            .OrderBy(o => o.Provenance.AsOfUtc)
            .Select(o => new ClosingPrice(o.Provenance.AsOfUtc, o.Value.AsNumber()))
            .ToList();

        var adjustment = SplitAdjustment.Apply(
            closes,
            splits,
            SplitAdjustment.DefaultMaxUnexplainedMove);

        // The control. The same recovered rows, the same production function, the same tolerance -
        // with the split list emptied. This is what separates "the data carries a discontinuity"
        // from "restating the data by a stored split introduced one", and it separates them by
        // asking the adjuster twice rather than by reasoning about its arithmetic. For a member
        // holding no split it is the same call, and says so trivially; for one that does, it is the
        // only evidence available locally that attributes the refusal to a cause.
        var control = SplitAdjustment.Apply(
            closes,
            Array.Empty<ShareSplit>(),
            SplitAdjustment.DefaultMaxUnexplainedMove);

        var controlNamed = Regex.Match(
            control.Explanation,
            @"moved ([\d.]+)% between (\d{4}-\d{2}-\d{2}) and (\d{4}-\d{2}-\d{2})",
            RegexOptions.CultureInvariant);

        var controlText = control.IsUsable
            ? "accepted with no split applied"
            : controlNamed.Success
                ? Universe.Inv($"still refused - {controlNamed.Groups[1].Value}% on {controlNamed.Groups[2].Value} → {controlNamed.Groups[3].Value}")
                : Universe.Inv($"still refused - {control.Refusal}");

        // The series the adjuster judged. On acceptance it hands it back; on refusal it returns an
        // empty list by design, and the input is the adjusted series only when no split restates it.
        IReadOnlyList<ClosingPrice> judged = adjustment.IsUsable
            ? adjustment.Prices
            : splits.Count == 0 ? closes : Array.Empty<ClosingPrice>();

        // A refusal returns no series, and where a split restated one this test cannot reconstruct
        // what the adjuster saw without re-implementing the arithmetic - which it will not do. In
        // that one case the raw series is walked instead, and the basis is stated rather than
        // quietly substituted.
        var basis = judged.Count > 0 ? "adjusted" : "**raw (unadjusted)**";
        var walked = Walk(
            judged.Count > 0 ? judged : closes,
            SplitAdjustment.DefaultMaxUnexplainedMove);

        // Exactly what the store holds, printed rather than characterised. A split nobody expected
        // is the difference between "the data did this" and "the adjustment logic did this".
        var splitText = splits.Count == 0
            ? "none held"
            : string.Join(
                "; ",
                splits.Select(s => Universe.Inv($"{Text(s.EffectiveAtUtc)} x{Number(s.Ratio)}")));

        var dates = closes
            .Select(c => DateOnly.FromDateTime(c.SessionCloseUtc))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var gate = CoverageEvaluation.Evaluate(true, dates, dates[0], dates[^1]);

        // The refusal names the two sessions and the move. The closes are then read back out of the
        // very series the adjuster was handed, so the figures in the report are the ones it saw
        // rather than a second calculation of them.
        var named = Regex.Match(
            adjustment.Explanation,
            @"moved ([\d.]+)% between (\d{4}-\d{2}-\d{2}) and (\d{4}-\d{2}-\d{2})",
            RegexOptions.CultureInvariant);

        var lookup = judged.Count > 0 ? judged : closes;

        var moveDates = named.Success
            ? Universe.Inv($"{named.Groups[2].Value} → {named.Groups[3].Value}")
            : "-";

        var movePercent = named.Success
            ? Universe.Inv($"{named.Groups[1].Value}%")
            : "-";

        var previous = named.Success ? CloseOn(lookup, named.Groups[2].Value) : "-";
        var current = named.Success ? CloseOn(lookup, named.Groups[3].Value) : "-";

        // The join across the interior block, for the member that has one. Found by asking which
        // consecutive pair in the recovered series spans the most weekdays, which is the hole the
        // removed rows left - not by naming dates this test decided in advance.
        var join = WidestJoin(closes);

        return new Screened(
            symbol,
            payload,
            dates,
            replay.Removed,
            splits.Count,
            adjustment.IsUsable,
            adjustment.Refusal,
            adjustment.Explanation,
            adjustment.Prices.Count,
            adjustment.Refusal == SeriesRefusal.UnexplainedDiscontinuity ? "**yes**" : "none",
            moveDates,
            previous,
            current,
            movePercent,
            adjustment.IsUsable
                ? "no unexplained move"
                : adjustment.Refusal == SeriesRefusal.UnexplainedDiscontinuity
                    ? "unexplained move"
                    : "another deterministic failure",
            adjustment.IsUsable
                ? "n/a"
                : !control.IsUsable
                    ? "**the recovered data itself** - refused identically with the split list emptied"
                    : "**the existing adjustment logic** - accepted once the split list is emptied",
            gate.Faults.Count(f => f == CoverageEvaluation.InteriorGap),
            LargestGap(gate.Reason),
            walked.Over,
            walked.Largest,
            walked.Where,
            walked.Margin,
            join.Before,
            join.BeforeClose,
            join.After,
            join.AfterClose,
            join.Move,
            join.Breaches,
            splitText,
            basis,
            controlText);
    }

    /// <summary>Every session pair, with the adjuster's own arithmetic. Reports, never judges.</summary>
    private static Walked Walk(IReadOnlyList<ClosingPrice> prices, decimal threshold)
    {
        if (prices.Count < 2)
        {
            return new Walked(0, "-", "-", "-");
        }

        var over = 0;
        var largest = 0m;
        var where = "-";

        for (var i = 1; i < prices.Count; i++)
        {
            var previous = prices[i - 1];
            var current = prices[i];

            if (previous.Close <= 0m || current.Close <= 0m)
            {
                continue;
            }

            var move = Math.Abs(current.Close - previous.Close) / previous.Close;

            if (move > threshold)
            {
                over++;
            }

            if (move > largest)
            {
                largest = move;
                where = Universe.Inv($"{Text(previous.SessionCloseUtc)} → {Text(current.SessionCloseUtc)}");
            }
        }

        return new Walked(over, Percent(largest), where, Percent(threshold - largest));
    }

    /// <summary>The consecutive pair spanning the most weekdays - the hole the removed rows left.</summary>
    private static Join WidestJoin(List<ClosingPrice> prices)
    {
        if (prices.Count < 2)
        {
            return new Join("-", "-", "-", "-", "-", "-");
        }

        var widest = 0;
        var at = 1;

        for (var i = 1; i < prices.Count; i++)
        {
            var missing = CoverageEvaluation.ExpectedSessions(
                DateOnly.FromDateTime(prices[i - 1].SessionCloseUtc).AddDays(1),
                DateOnly.FromDateTime(prices[i].SessionCloseUtc).AddDays(-1));

            if (missing > widest)
            {
                widest = missing;
                at = i;
            }
        }

        var before = prices[at - 1];
        var after = prices[at];

        var move = before.Close <= 0m || after.Close <= 0m
            ? -1m
            : Math.Abs(after.Close - before.Close) / before.Close;

        return new Join(
            Text(before.SessionCloseUtc),
            Number(before.Close),
            Text(after.SessionCloseUtc),
            Number(after.Close),
            move < 0m ? "-" : Percent(move),
            move < 0m
                ? "-"
                : move > SplitAdjustment.DefaultMaxUnexplainedMove ? "**YES**" : "no");
    }

    /// <summary>The close the adjuster held for one session, or a dash when it held none.</summary>
    private static string CloseOn(IReadOnlyList<ClosingPrice> prices, string date)
    {
        foreach (var price in prices)
        {
            if (string.Equals(Text(price.SessionCloseUtc), date, StringComparison.Ordinal))
            {
                return Number(price.Close);
            }
        }

        return "-";
    }

    private static string LargestGap(string reason) =>
        reason.Contains("gap of", StringComparison.Ordinal)
            ? reason[(reason.IndexOf("gap of", StringComparison.Ordinal) + 7)..].Split(' ')[0]
            : reason.Contains("gap is", StringComparison.Ordinal)
                ? reason[(reason.IndexOf("gap is", StringComparison.Ordinal) + 7)..].Split(' ')[0]
                : "0";

    // ---- the replay, unchanged from the re-read ---------------------------------------------------

    private static async Task<Replay> ReplayAsync(
        EodhdDailyPriceNormalizer normalizer,
        Func<byte[], NormalizationInput> inputFor,
        byte[] bytes)
    {
        var raw = ReadRows(bytes);
        var verdict = await normalizer.NormalizeAsync(inputFor(bytes));
        var surviving = Enumerable.Range(0, raw.Count).ToList();
        var removed = 0;
        var peels = 0;

        while (verdict.IsQuarantined && peels < MaxPeels)
        {
            var match = RowIndex.Match(verdict.Reason ?? string.Empty);

            if (!match.Success)
            {
                break;
            }

            var position = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1;

            if (position < 0 || position >= surviving.Count)
            {
                break;
            }

            surviving.RemoveAt(position);
            removed++;

            verdict = await normalizer.NormalizeAsync(inputFor(Serialise(raw, surviving)));
            peels++;
        }

        return new Replay(
            verdict.IsQuarantined ? [] : verdict.Observations,
            removed);
    }

    private static List<string> ReadRows(byte[] bytes)
    {
        var rows = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                rows.Add(element.GetRawText());
            }
        }
        catch (JsonException)
        {
            // Not an array of rows. The normaliser says so too, and its verdict is the one used.
        }

        return rows;
    }

    private static byte[] Serialise(List<string> raw, List<int> surviving)
    {
        var builder = new StringBuilder("[");

        for (var i = 0; i < surviving.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(raw[surviving[i]]);
        }

        return Encoding.UTF8.GetBytes(builder.Append(']').ToString());
    }

    // ---- gate 6, run rather than reasoned about ---------------------------------------------------

    private static Six EvaluateGateSix(
        List<Member> members,
        Dictionary<string, List<DateOnly>> held,
        HashSet<string> requested)
    {
        var faultMembers = 0;
        var pending = 0;
        var noSeries = 0;
        var interiorGaps = 0;
        var gapMembers = 0;
        var discontinuities = 0;
        var withSeries = 0;
        var faultEvents = 0;
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
                faultMembers++;
                faulted.Add(Universe.Inv(
                    $"`{symbol ?? member.Cik}` ({string.Join(", ", verdict.Faults)}): {verdict.Reason}"));
            }

            faultEvents += verdict.Faults.Count;
            noSeries += verdict.Faults.Count(f => f == CoverageEvaluation.NoSeries);
            discontinuities += verdict.Faults.Count(f => f == CoverageEvaluation.UnexplainedDiscontinuity);

            var gaps = verdict.Faults.Count(f => f == CoverageEvaluation.InteriorGap);
            interiorGaps += gaps;

            if (gaps > 0)
            {
                gapMembers++;
            }
        }

        return new Six(
            faultMembers,
            faultEvents,
            noSeries,
            interiorGaps,
            gapMembers,
            discontinuities,
            pending,
            withSeries,
            CoverageEvaluation.Passes(faultMembers, pending, acquisitionComplete: true),
            faulted);
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
                    isReady && ticker is not null ? AcquisitionPlanning.SymbolFor(ticker) : null,
                    spanFrom,
                    spanTo);
            })
            .ToList();
    }

    // ---- formatting -------------------------------------------------------------------------------

    private static string Text(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Text(DateTime instant) =>
        instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Number(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Percent(decimal fraction) =>
        Universe.Inv($"{decimal.Round(fraction * 100m, 2)}%");

    // ---- the shapes -------------------------------------------------------------------------------

    private sealed record Replay(IReadOnlyList<Observation> Accepted, int Removed);

    private sealed record Walked(int Over, string Largest, string Where, string Margin);

    private sealed record Join(
        string Before,
        string BeforeClose,
        string After,
        string AfterClose,
        string Move,
        string Breaches);

    private sealed record Screened(
        string Symbol,
        string Payload,
        List<DateOnly> Dates,
        int Removed,
        int Splits,
        bool Usable,
        SeriesRefusal Refusal,
        string Explanation,
        int Adjusted,
        string MoveFound,
        string MoveDates,
        string PreviousClose,
        string CurrentClose,
        string MovePercent,
        string FailureClass,
        string Cause,
        int GateFaults,
        string LargestGap,
        int OverThreshold,
        string LargestMove,
        string LargestMoveWhere,
        string Margin,
        string BeforeGapDate,
        string BeforeGapClose,
        string AfterGapDate,
        string AfterGapClose,
        string GapMove,
        string GapBreaches,
        string SplitDetail,
        string Basis,
        string Control);

    private sealed record Six(
        int FaultMembers,
        int FaultEvents,
        int NoSeries,
        int InteriorGaps,
        int GapMembers,
        int Discontinuities,
        int Pending,
        int WithSeries,
        bool Passes,
        List<string> Faulted);

    private sealed record Member(
        string Cik,
        bool Ready,
        string? Symbol,
        DateOnly SpanFrom,
        DateOnly SpanTo);
}
