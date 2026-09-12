using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Every session pair the split adjuster refuses, read back against the archived rows around it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, in memory, and nothing is kept.</strong> No connector is enabled and no
/// request leaves the process. Nothing is persisted, reclassified, released or removed: run,
/// observation and quarantine counts are asserted unchanged either side, and every quarantined
/// payload is asserted still quarantined under the same rule. The normaliser,
/// <see cref="SplitAdjustment"/>, <see cref="CoverageEvaluation"/> and the sealed Gate 6
/// declaration are read and not touched. No price is repaired, interpolated or synthesised.
/// </para>
/// <para>
/// <strong>The question, stated narrowly.</strong> Six recovered series are refused as unexplained
/// discontinuities, and the control run established that the refusals come from the data rather
/// than from restating it by a stored split. This stage asks one thing of each breach: does the
/// new price level <em>hold</em>, or does it come back? That distinction is available locally,
/// because the archive holds the rows either side. Whether a persistent shift is a corporate action
/// is <em>not</em> available locally, and is not decided here.
/// </para>
/// <para>
/// <strong>How the adjusted series is obtained without re-implementing the adjuster.</strong>
/// <see cref="SplitAdjustment.Apply"/> returns an empty list when it refuses, so the series it
/// walked cannot be read back from a refusal. It is therefore asked a second time with a tolerance
/// no move can exceed, which makes its discontinuity check pass and hands back the restated series
/// it built. The restatement arithmetic is production's; only the tolerance passed to that one call
/// differs, and every breach below is still measured against the real
/// <see cref="SplitAdjustment.DefaultMaxUnexplainedMove"/>. Nothing in the domain is modified and
/// no threshold is changed.
/// </para>
/// <para>
/// <strong>Context comes from the raw archive, not from the recovered series.</strong> The window
/// around each breach is read from the archived payload itself, so it includes the rows the
/// normaliser refused. That is deliberate: a breach that sits beside a <c>close: 0</c> row is a
/// different finding from one that does not, and a window drawn from the accepted rows alone would
/// hide exactly that.
/// </para>
/// </remarks>
public sealed class UnexplainedMoveCharacterisationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_MOVE_CHARACTERISATION";

    private const string PriceSource = "eodhd-eod";

    /// <summary>A guard on the peel loop, so a reason this cannot parse cannot spin.</summary>
    private const int MaxPeels = 200;

    /// <summary>Sessions of context read either side of a breach.</summary>
    private const int Context = 5;

    /// <summary>How near a later close must be to a level to count as sitting at it.</summary>
    /// <remarks>
    /// A quarter. Wide enough that ordinary trading after the event does not read as a return to
    /// the old level, narrow enough that a return actually registers. It is a reporting band for
    /// this diagnostic only: it is not a threshold, it changes nothing in the platform, and every
    /// close it was applied to is printed beside the verdict so the band can be checked.
    /// </remarks>
    private const decimal Band = 0.25m;

    /// <summary>Weekday gap beyond which a pair is reported as spanning missing sessions.</summary>
    /// <remarks>The sealed Gate 6 tolerance, reused as a description rather than as a judgement.</remarks>
    private const int GapTolerance = CoverageEvaluation.InteriorGapTolerance;

    private static readonly Regex RowIndex = new(
        @"^Row (\d+):", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UnexplainedMoveCharacterisationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_refused_session_pair_is_read_back_against_the_archived_rows_around_it()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The unexplained-move characterisation is off. Set {GateVariable}=1 to run it. It reads only.");

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

        var members = new List<Member>();

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

                var raw = ReadRows(bytes);

                var accepted = await ReplayAsync(
                    normalizer,
                    payload => new NormalizationInput(
                        request.SourceId,
                        request.Category,
                        request.Subject,
                        hash,
                        payload,
                        retrievedAtUtc),
                    bytes,
                    raw);

                if (accepted.Count == 0)
                {
                    continue;
                }

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

                var closes = accepted
                    .GroupBy(o => o.Provenance.AsOfUtc)
                    .Select(g => g
                        .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                        .ThenByDescending(o => o.Provenance.RetrievedAtUtc)
                        .First())
                    .OrderBy(o => o.Provenance.AsOfUtc)
                    .Select(o => new ClosingPrice(o.Provenance.AsOfUtc, o.Value.AsNumber()))
                    .ToList();

                // The verdict as production gives it, at the real tolerance. Recorded so this
                // stage's member set is the adjuster's own and not a list carried in by hand.
                var verdict = SplitAdjustment.Apply(
                    closes,
                    splits,
                    SplitAdjustment.DefaultMaxUnexplainedMove);

                if (verdict.IsUsable)
                {
                    continue;
                }

                // The restated series, from the same function, asked with a tolerance no move can
                // exceed so that it returns what it built instead of refusing. Nothing else differs.
                var restated = SplitAdjustment.Apply(closes, splits, decimal.MaxValue);

                members.Add(new Member(
                    symbol,
                    row.Id.Abbreviated,
                    raw,
                    closes,
                    restated.Prices,
                    splits,
                    verdict.Explanation,
                    Breaches(symbol, raw, restated.Prices, splits),
                    Divergence(closes, restated.Prices)));
            }
        }

        members = members.OrderBy(m => m.Symbol, StringComparer.Ordinal).ToList();

        var all = members.SelectMany(m => m.Breaches).ToList();

        var persistent = all.Count(b => b.Class == ClassPersistent);
        var reversion = all.Count(b => b.Class == ClassReversion);
        var transient = all.Count(b => b.Class == ClassTransient);
        var anomaly = all.Count(b => b.Class == ClassAnomaly);
        var insufficient = all.Count(b => b.Class == ClassInsufficient);

        // ================================================================================
        // Section 1
        // ================================================================================

        report.AppendLine("# The refused session pairs, read back against the archived rows around them");
        report.AppendLine();
        report.AppendLine("**In-memory and read-only.** No provider was called, no connector enabled,");
        report.AppendLine("nothing persisted, reclassified, released or removed. No price was repaired,");
        report.AppendLine("interpolated or synthesised. The normaliser, `SplitAdjustment`,");
        report.AppendLine("`CoverageEvaluation` and the sealed Gate 6 declaration are unchanged.");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Threshold in force: `SplitAdjustment.DefaultMaxUnexplainedMove` = **{Percent(SplitAdjustment.DefaultMaxUnexplainedMove)}**, read from the domain. Context window: up to **{Context}** archived sessions either side."));
        report.AppendLine();

        report.AppendLine("## Section 1 — Executive summary");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Members refused by the adjuster | **{members.Count}** |"));
        report.AppendLine(Universe.Inv($"| Breach pairs characterised | **{all.Count}** |"));
        report.AppendLine(Universe.Inv($"| **A** — persistent level shift, corporate-action cause unconfirmed | **{persistent}** |"));
        report.AppendLine(Universe.Inv($"| **B** — immediate or repeated reversal | **{reversion}** |"));
        report.AppendLine(Universe.Inv($"| **C** — transient isolated print | **{transient}** |"));
        report.AppendLine(Universe.Inv($"| **D** — gap or data-quality anomaly | **{anomaly}** |"));
        report.AppendLine(Universe.Inv($"| **E** — insufficient local evidence | **{insufficient}** |"));
        report.AppendLine();
        report.AppendLine("| Confidence | Breaches |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| high | {all.Count(b => b.Confidence == "high")} |"));
        report.AppendLine(Universe.Inv($"| medium | {all.Count(b => b.Confidence == "medium")} |"));
        report.AppendLine(Universe.Inv($"| low | {all.Count(b => b.Confidence == "low")} |"));
        report.AppendLine();
        report.AppendLine("| Symbol | Breaches | A | B | C | D | E |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var m in members)
        {
            report.AppendLine(Universe.Inv(
                $"| `{m.Symbol}` | **{m.Breaches.Count}** | {m.Breaches.Count(b => b.Class == ClassPersistent)} | {m.Breaches.Count(b => b.Class == ClassReversion)} | {m.Breaches.Count(b => b.Class == ClassTransient)} | {m.Breaches.Count(b => b.Class == ClassAnomaly)} | {m.Breaches.Count(b => b.Class == ClassInsufficient)} |"));
        }

        report.AppendLine();
        var subTick = all.Count(b => b.IsSubTick);

        report.AppendLine(Universe.Inv(
            $"Breaches where **both** closes are at or below $0.001, the price level at which one minimum tick is itself a move of 100 per cent: **{subTick}**. Section 6 returns to what that means."));
        report.AppendLine();
        report.AppendLine("**How a class is decided, so the counts can be checked rather than taken.** For");
        report.AppendLine(Universe.Inv($"each breach the next up-to-{Context} archived closes are read, and two questions are"));
        report.AppendLine("asked of each. First, does it come **back to the old level** — within a stated band");
        report.AppendLine(Universe.Inv($"of ±{Percent(Band)} of the close before the breach? Second, which of the two levels does"));
        report.AppendLine("it sit **nearer**, measured as a ratio rather than a percentage difference, because");
        report.AppendLine("these series span four orders of magnitude and a percentage is not symmetric across");
        report.AppendLine("them. The rules, applied in order:");
        report.AppendLine();
        report.AppendLine("1. Fewer than two archived closes after the breach → **E**.");
        report.AppendLine(Universe.Inv($"2. The pair spans more than {GapTolerance} weekdays of missing sessions → **D**."));
        report.AppendLine("3. The very next close is back at the old level → **C**.");
        report.AppendLine("4. Some later close is back at the old level → **B**.");
        report.AppendLine("5. None returns, and every later close sits nearer the new level → **A**.");
        report.AppendLine("6. None returns, but not every close sits nearer the new level → **E**.");
        report.AppendLine();
        report.AppendLine("Every close each rule was applied to is printed in Section 3 beside the verdict, so");
        report.AppendLine("a reader can disagree with a classification against the same numbers that produced");
        report.AppendLine("it. Neither the band nor the ratio measure is a threshold: nothing in the platform");
        report.AppendLine("reads them, and they change nothing.");
        report.AppendLine();
        report.AppendLine("**A is deliberately the weakest claim in the list.** It says the level held for the");
        report.AppendLine("sessions the archive holds, and nothing more. No corporate action is asserted,");
        report.AppendLine("inferred or looked for, because no local evidence could establish one; where a");
        report.AppendLine("stored split sits near an event Section 5 says so without drawing a line between");
        report.AppendLine("them.");
        report.AppendLine();

        // ================================================================================
        // Section 2
        // ================================================================================

        report.AppendLine("## Section 2 — All breaches");
        report.AppendLine();
        report.AppendLine("| # | Symbol | Pair | Prev close | Cur close | Move | Threshold | Prev OHLCV | Cur OHLCV | New level | Old level | Volume | Split nearby | Vendor adjustment factor | close ≤ 0 nearby | Class | Confidence |");
        report.AppendLine("| ---: | --- | --- | ---: | ---: | ---: | ---: | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        var n = 0;

        foreach (var b in all)
        {
            n++;

            report.AppendLine(Universe.Inv(
                $"| {n} | `{b.Symbol}` | {b.From} → {b.To} | {b.PreviousClose} | {b.CurrentClose} | **{b.Move}** | {Percent(SplitAdjustment.DefaultMaxUnexplainedMove)} | {b.PreviousOhlcv} | {b.CurrentOhlcv} | {b.NewLevel} | {b.OldLevel} | {b.VolumeVerdict} | {b.SplitNearby} | {b.VendorFactor} | {b.ZeroNearby} | **{b.Class}** | {b.Confidence} |"));
        }

        report.AppendLine();
        report.AppendLine("*Move* is the figure `SplitAdjustment` itself computes — absolute change over the");
        report.AppendLine("previous close — at the precision it reports. OHLCV is quoted from the archived");
        report.AppendLine("row verbatim; where a member's series is restated by a stored split, the close");
        report.AppendLine("column of the breach is the restated one and the OHLCV column is the raw row, and");
        report.AppendLine("both are shown rather than one standing in for the other.");
        report.AppendLine();

        // ================================================================================
        // Section 3
        // ================================================================================

        report.AppendLine("## Section 3 — Detailed evidence");
        report.AppendLine();
        report.AppendLine("Every table below is archived rows, quoted. `!` marks a row the normaliser refused");
        report.AppendLine("— these are in the archive but not in the recovered series, and they are shown");
        report.AppendLine("here because a breach beside one is a different finding from a breach that is not.");
        report.AppendLine("`<` and `>` mark the two sessions of the breach itself.");
        report.AppendLine();

        foreach (var m in members)
        {
            report.AppendLine(Universe.Inv($"### `{m.Symbol}` — payload `{m.Payload}`"));
            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"{m.Rows.Count} archived rows, {m.Closes.Count} accepted by the normaliser, {m.Splits.Count} split observation(s) held, **{m.Breaches.Count}** breach pair(s)."));
            report.AppendLine();
            report.AppendLine(Universe.Inv($"Raw closes against restated closes: {m.Divergence}."));
            report.AppendLine();
            report.AppendLine("The adjuster's refusal, verbatim:");
            report.AppendLine();
            report.AppendLine(Universe.Inv($"> {m.Explanation}"));
            report.AppendLine();

            foreach (var b in m.Breaches)
            {
                report.AppendLine(Universe.Inv($"#### `{m.Symbol}` {b.From} → {b.To} ({b.Move})"));
                report.AppendLine();
                report.AppendLine("| | date | open | high | low | close | adj close | volume |");
                report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");

                foreach (var line in b.Window)
                {
                    report.AppendLine(line);
                }

                report.AppendLine();
                report.AppendLine("| | |");
                report.AppendLine("| --- | --- |");
                report.AppendLine(Universe.Inv($"| Move as `SplitAdjustment` computes it | **{b.Move}** against a {Percent(SplitAdjustment.DefaultMaxUnexplainedMove)} tolerance |"));
                report.AppendLine(Universe.Inv($"| Weekdays of missing sessions between the pair | {b.MissingWeekdays} |"));
                report.AppendLine(Universe.Inv($"| Closes after, and where each sits | {b.AfterTrace} |"));
                report.AppendLine(Universe.Inv($"| New level | **{b.NewLevel}** |"));
                report.AppendLine(Universe.Inv($"| Old level | **{b.OldLevel}** |"));
                report.AppendLine(Universe.Inv($"| Volume, mean of {Context} before → {Context} after | {b.VolumeDetail} |"));
                report.AppendLine(Universe.Inv($"| Locally archived split within 30 days | {b.SplitNearby} |"));
                report.AppendLine(Universe.Inv($"| Vendor's own adjustment factor across the pair | {b.VendorFactor} |"));
                report.AppendLine(Universe.Inv($"| Rows with close ≤ 0 in this window | {b.ZeroNearby} |"));
                report.AppendLine(Universe.Inv($"| **Classification** | **{b.Class}** ({b.Confidence} confidence) |"));
                report.AppendLine(Universe.Inv($"| Why | {b.Why} |"));
                report.AppendLine();
            }
        }

        // ================================================================================
        // Section 4
        // ================================================================================

        report.AppendLine("## Section 4 — `SHPW.US` deep dive");
        report.AppendLine();

        var shpw = members.FirstOrDefault(m =>
            string.Equals(m.Symbol, "SHPW.US", StringComparison.Ordinal));

        if (shpw is null)
        {
            report.AppendLine("`SHPW.US` was not among the refused members in this run, which contradicts the");
            report.AppendLine("previous screen. That is reported rather than explained away.");
            report.AppendLine();
        }
        else
        {
            report.AppendLine(Universe.Inv(
                $"`SHPW.US` carries **{shpw.Breaches.Count}** of the {all.Count} breaches. They are listed in date order with the level either side of each, so a reader can see whether they form a ladder of real transitions or the same level being left and returned to."));
            report.AppendLine();
            report.AppendLine("| # | Pair | Close before | Close after | Move | Median close, 5 before | Median close, 5 after | New level | Class |");
            report.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |");

            var k = 0;

            foreach (var b in shpw.Breaches)
            {
                k++;

                report.AppendLine(Universe.Inv(
                    $"| {k} | {b.From} → {b.To} | {b.PreviousClose} | {b.CurrentClose} | {b.Move} | {b.MedianBefore} | {b.MedianAfter} | {b.NewLevel} | **{b.Class}** |"));
            }

            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"**The distinct closing levels this series visits**, in date order, collapsing runs of the same level: {shpw.LevelLadder}"));
            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"Of the {shpw.Breaches.Count} breaches, **{shpw.Breaches.Count(b => b.Class == ClassPersistent)}** are persistent shifts, **{shpw.Breaches.Count(b => b.Class == ClassReversion)}** are reversals, **{shpw.Breaches.Count(b => b.Class == ClassTransient)}** are isolated prints, **{shpw.Breaches.Count(b => b.Class == ClassAnomaly)}** are gap or data-quality anomalies and **{shpw.Breaches.Count(b => b.Class == ClassInsufficient)}** cannot be determined from the archive. They are not collapsed into a single verdict here; the per-breach rows above and the windows in Section 3 are the evidence for each."));
            report.AppendLine();
        }

        // ================================================================================
        // Section 5
        // ================================================================================

        report.AppendLine("## Section 5 — Split and corporate-action evidence");
        report.AppendLine();
        report.AppendLine("Every split observation the store holds for these members, with its distance to");
        report.AppendLine("the nearest breach. **Proximity is reported; causation is not inferred.** A split");
        report.AppendLine("near an event is not evidence that it caused the event, and the adjuster has");
        report.AppendLine("already applied every split listed here — a move that survived that application");
        report.AppendLine("is by construction one the split does not explain.");
        report.AppendLine();

        var anySplit = false;

        report.AppendLine("| Symbol | Effective | Ratio | Nearest breach | Days away |");
        report.AppendLine("| --- | --- | ---: | --- | ---: |");

        foreach (var m in members)
        {
            foreach (var s in m.Splits)
            {
                anySplit = true;

                Breach? nearest = null;
                var days = 0;

                foreach (var candidate in m.Breaches)
                {
                    var distance = Math.Abs(
                        (candidate.ToDate.ToDateTime(TimeOnly.MinValue) - s.EffectiveAtUtc.Date).Days);

                    if (nearest is null || distance < days)
                    {
                        nearest = candidate;
                        days = distance;
                    }
                }

                var nearestPair = nearest is null
                    ? "-"
                    : Universe.Inv($"{nearest.From} → {nearest.To}");

                var nearestDays = nearest is null
                    ? "-"
                    : days.ToString(CultureInfo.InvariantCulture);

                report.AppendLine(Universe.Inv(
                    $"| `{m.Symbol}` | {Text(s.EffectiveAtUtc)} | {Number(s.Ratio)} | {nearestPair} | {nearestDays} |"));
            }
        }

        if (!anySplit)
        {
            report.AppendLine("| *(none)* | | | | |");
        }

        report.AppendLine();
        report.AppendLine("**The payload carries a second, independent piece of corporate-action evidence, and");
        report.AppendLine("it is negative.** Every archived row states an `adjusted_close` beside its `close`.");
        report.AppendLine("The ratio between them is the vendor's own adjustment factor for that session — what");
        report.AppendLine("it would divide the raw close by to state it in today's shares. The platform");
        report.AppendLine("deliberately stores the raw close and never that figure, and nothing here applies");
        report.AppendLine("it; but a factor that *moves* between two consecutive sessions is the vendor");
        report.AppendLine("recording a corporate action between them, and a factor that does not move is the");
        report.AppendLine("vendor recording none.");
        report.AppendLine();
        report.AppendLine("| | Breaches |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| The vendor's adjustment factor **moves** across the pair | **{all.Count(b => b.VendorFactorMoves)}** |"));
        report.AppendLine(Universe.Inv($"| The vendor's adjustment factor is unchanged across the pair | **{all.Count(b => !b.VendorFactorMoves)}** |"));
        report.AppendLine();
        report.AppendLine("Read carefully, that is a statement about the vendor's records and not about the");
        report.AppendLine("world: a vendor that does not know about a corporate action reports no factor");
        report.AppendLine("change for it either. It is nevertheless the strongest corporate-action evidence");
        report.AppendLine("available locally, and where it does not move it is evidence *against* the");
        report.AppendLine("adjustment-bearing explanation rather than merely an absence of evidence for it.");
        report.AppendLine();
        report.AppendLine("No other corporate-action category is held locally for these members. The platform");
        report.AppendLine("acquired splits and prices; it holds no dividend, spin-off, symbol-change or");
        report.AppendLine("delisting feed, so the absence of an explanation here is the absence of a source,");
        report.AppendLine("not evidence that no event occurred.");
        report.AppendLine();

        // ================================================================================
        // Section 6
        // ================================================================================

        report.AppendLine("## Section 6 — Data-quality assessment");
        report.AppendLine();
        report.AppendLine("Observations first, each one a property of the archived rows that can be checked");
        report.AppendLine("against the tables in Section 3.");
        report.AppendLine();
        report.AppendLine("| Symbol | Pair | Both closes ≤ $0.001 | Breach row has zero volume | Breach row is O=H=L=C | A close ≤ 0 row in the window | Spans missing sessions | Repeats an adjacent close exactly |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var b in all)
        {
            var tick = b.IsSubTick ? "**yes**" : "no";

            var spans = b.MissingWeekdays > GapTolerance
                ? Universe.Inv($"**yes - {b.MissingWeekdays}**")
                : Universe.Inv($"no - {b.MissingWeekdays}");

            report.AppendLine(Universe.Inv(
                $"| `{b.Symbol}` | {b.From} → {b.To} | {tick} | {b.ZeroVolume} | {b.Degenerate} | {b.ZeroNearby} | {spans} | {b.StaleRepeat} |"));
        }

        report.AppendLine();
        report.AppendLine("**Reading these columns.** A breach row printing zero volume, or an open, high,");
        report.AppendLine("low and close that are all the same number, is a row that does not describe a");
        report.AppendLine("session in which anything was traded at a range of prices. That is an observation");
        report.AppendLine("about the row, not a verdict on the price: a genuinely illiquid security prints");
        report.AppendLine("rows like that too. It is recorded so that the reader can weigh it, and it is not");
        report.AppendLine("fed into the classification.");
        report.AppendLine();
        report.AppendLine("**The sub-tick column is the one that changes how the rest should be read.** The US");
        report.AppendLine("sub-penny minimum increment is $0.0001. A close printed at $0.0001 that moves to");
        report.AppendLine("$0.0002 has moved one increment — the smallest change the price is capable of");
        report.AppendLine(Universe.Inv($"expressing — and that single increment is a {Percent(1m)} move, which is twice the"));
        report.AppendLine(Universe.Inv($"{Percent(SplitAdjustment.DefaultMaxUnexplainedMove)} tolerance. In that regime the tolerance cannot be met by any price change"));
        report.AppendLine("at all, so a breach carries no information about whether anything happened. This is");
        report.AppendLine("an observation about the arithmetic and the tick, not an inference about the");
        report.AppendLine("security or a judgement about the tolerance, which is unchanged and is not");
        report.AppendLine("proposed for change here.");
        report.AppendLine();
        report.AppendLine("| Symbol | Breaches | …at or below $0.001 | …above it |");
        report.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var m in members)
        {
            var sub = m.Breaches.Count(x => x.IsSubTick);

            report.AppendLine(Universe.Inv(
                $"| `{m.Symbol}` | {m.Breaches.Count} | **{sub}** | {m.Breaches.Count - sub} |"));
        }

        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "gate6-unexplained-move-characterisation.md"),
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

        // The six the adjuster refuses, found by asking it rather than by carrying a list in.
        Assert.Equal(
            "LGIQ.US NGM.US ONEM.US QUMU.US SDC.US SHPW.US",
            string.Join(" ", members.Select(m => m.Symbol)));

        // The five members holding no split are restated by nothing, so their breach counts cannot
        // move. Stated exactly, so a change in the recovery would fail here rather than be absorbed.
        Assert.Equal(
            "LGIQ.US:3 NGM.US:1 ONEM.US:1 QUMU.US:1 SDC.US:2",
            string.Join(
                " ",
                members
                    .Where(m => m.Splits.Count == 0)
                    .Select(m => Universe.Inv($"{m.Symbol}:{m.Breaches.Count}"))));

        // Every breach is characterised or explicitly recorded as undeterminable. None is silent.
        Assert.All(all, b => Assert.False(string.IsNullOrWhiteSpace(b.Class)));

        Assert.Equal(0.5m, SplitAdjustment.DefaultMaxUnexplainedMove);
    }

    // ---- the classes -----------------------------------------------------------------------------

    private const string ClassPersistent = "A - persistent level shift, corporate-action cause unconfirmed";
    private const string ClassReversion = "B - immediate or repeated reversal";
    private const string ClassTransient = "C - transient isolated print";
    private const string ClassAnomaly = "D - gap or data-quality anomaly";
    private const string ClassInsufficient = "E - insufficient local evidence";

    // ---- finding and characterising the breaches --------------------------------------------------

    private static List<Breach> Breaches(
        string symbol,
        List<Raw> raw,
        IReadOnlyList<ClosingPrice> restated,
        List<ShareSplit> splits)
    {
        var found = new List<Breach>();

        // Indexed by trading date so a breach in the restated series can be placed back in the
        // archived payload, invalid rows and all.
        var byDate = new Dictionary<DateOnly, int>();

        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i].Date is { } d && !byDate.ContainsKey(d))
            {
                byDate[d] = i;
            }
        }

        for (var i = 1; i < restated.Count; i++)
        {
            var previous = restated[i - 1];
            var current = restated[i];

            if (previous.Close <= 0m || current.Close <= 0m)
            {
                continue;
            }

            var move = Math.Abs(current.Close - previous.Close) / previous.Close;

            if (move <= SplitAdjustment.DefaultMaxUnexplainedMove)
            {
                continue;
            }

            found.Add(Characterise(symbol, raw, byDate, restated, splits, i, previous, current, move));
        }

        return found;
    }

    private static Breach Characterise(
        string symbol,
        List<Raw> raw,
        Dictionary<DateOnly, int> byDate,
        IReadOnlyList<ClosingPrice> restated,
        List<ShareSplit> splits,
        int at,
        ClosingPrice previous,
        ClosingPrice current,
        decimal move)
    {
        var fromDate = DateOnly.FromDateTime(previous.SessionCloseUtc);
        var toDate = DateOnly.FromDateTime(current.SessionCloseUtc);

        var ia = byDate.TryGetValue(fromDate, out var a) ? a : -1;
        var ib = byDate.TryGetValue(toDate, out var b) ? b : -1;

        var first = ia < 0 ? Math.Max(ib - Context, 0) : Math.Max(ia - Context, 0);
        var last = ib < 0 ? Math.Min(ia + Context, raw.Count - 1) : Math.Min(ib + Context, raw.Count - 1);

        var window = new List<string>();

        for (var i = first; i <= last; i++)
        {
            var mark = i == ia ? "`<`" : i == ib ? "`>`" : raw[i].IsInvalid ? "`!`" : "";

            window.Add(Universe.Inv(
                $"| {mark} | {raw[i].DateText} | {raw[i].Open} | {raw[i].High} | {raw[i].Low} | {raw[i].Close} | {raw[i].AdjustedClose} | {raw[i].Volume} |"));
        }

        // The closes after the breach, in the restated series - the same numbers the adjuster
        // compares. Where each sits is decided by the reporting band and printed with it.
        var afterCloses = new List<decimal>();

        for (var i = at + 1; i < restated.Count && afterCloses.Count < Context; i++)
        {
            afterCloses.Add(restated[i].Close);
        }

        var beforeCloses = new List<decimal>();

        for (var i = at - 1; i >= 0 && beforeCloses.Count < Context; i--)
        {
            beforeCloses.Add(restated[i].Close);
        }

        beforeCloses.Reverse();

        var atOld = 0;
        var nearerNew = 0;
        var trace = new List<string>();
        var returnedAt = -1;

        for (var i = 0; i < afterCloses.Count; i++)
        {
            var c = afterCloses[i];

            // A return to the old level is the band test, and it is the only test that decides a
            // reversal: coming back means coming back to where the price was, within a stated
            // tolerance.
            var returned = previous.Close > 0m
                && Math.Abs(c - previous.Close) / previous.Close <= Band;

            // Which level a close sits nearer is measured as a ratio, not a percentage difference.
            // A percentage is not symmetric - a fall to a hundredth is 99% away while the rise back
            // is 9,900% - and these series span four orders of magnitude, so a percentage band
            // would call a close that is plainly at the new level 'neither'. The ratio measure
            // asks only which level the close is a smaller multiple away from.
            var toNew = Distance(c, current.Close);
            var toOld = Distance(c, previous.Close);
            var nearer = toNew < toOld;

            if (returned)
            {
                atOld++;

                if (returnedAt < 0)
                {
                    returnedAt = i;
                }

                trace.Add(Universe.Inv($"{Number(c)} **back at the old level**"));
            }
            else if (nearer)
            {
                nearerNew++;
                trace.Add(Universe.Inv($"{Number(c)} nearer the new level"));
            }
            else
            {
                trace.Add(Universe.Inv($"{Number(c)} nearer the old level, but outside the band"));
            }
        }

        var missing = CoverageEvaluation.ExpectedSessions(fromDate.AddDays(1), toDate.AddDays(-1));

        var newLevel = afterCloses.Count == 0
            ? "cannot be determined"
            : returnedAt == 0
                ? "reverts immediately"
                : atOld > 0
                    ? "partially reverts"
                    : nearerNew == afterCloses.Count
                        ? "persists"
                        : "cannot be determined";

        var oldLevel = afterCloses.Count == 0
            ? "cannot be determined"
            : atOld > 0 ? "persists" : "disappears";

        var zeroInWindow = 0;

        for (var i = first; i <= last; i++)
        {
            if (raw[i].IsInvalid)
            {
                zeroInWindow++;
            }
        }

        var (klass, confidence, why) = Classify(
            afterCloses.Count, missing, returnedAt, atOld, nearerNew);

        // Proximity only. A split within a month of the event is recorded so a reader can see it;
        // the adjuster has already applied every one of them, so a move that survived is by
        // construction one the split does not explain.
        var near = splits
            .Where(s => Math.Abs((s.EffectiveAtUtc.Date - toDate.ToDateTime(TimeOnly.MinValue)).Days) <= 30)
            .Select(s => Universe.Inv($"{Text(s.EffectiveAtUtc)} x{Number(s.Ratio)}"))
            .ToList();

        var splitNearby = near.Count == 0 ? "none" : string.Join("; ", near);

        // Does the vendor's own adjustment factor move across the pair? Read from the two archived
        // rows, compared, and reported. No factor is applied and no cause is inferred from it.
        var factorBefore = ia >= 0 ? raw[ia].Factor : null;
        var factorAfter = ib >= 0 ? raw[ib].Factor : null;

        var factorMoves = factorBefore is { } fb && factorAfter is { } fa
            && fb > 0m
            && Math.Abs(fa - fb) / fb > 0.001m;

        var factorText = factorBefore is null || factorAfter is null
            ? "not stated in the archive"
            : factorMoves
                ? Universe.Inv($"**moves** {Number(factorBefore.Value)} → {Number(factorAfter.Value)}")
                : Universe.Inv($"unchanged at {Number(factorAfter.Value)}");

        var volumeBefore = Mean(raw, first, ia < 0 ? first : ia - 1);
        var volumeAfter = Mean(raw, ib < 0 ? last : ib + 1, last);

        var volumeVerdict = volumeBefore <= 0m || volumeAfter <= 0m
            ? "not comparable"
            : volumeAfter >= volumeBefore * 3m || volumeAfter * 3m <= volumeBefore
                ? "**material**"
                : "not material";

        return new Breach(
            symbol,
            Text(fromDate),
            Text(toDate),
            toDate,
            Number(previous.Close),
            Number(current.Close),
            Percent(move),
            ia < 0 ? "not in the archive" : raw[ia].Ohlcv,
            ib < 0 ? "not in the archive" : raw[ib].Ohlcv,
            newLevel,
            oldLevel,
            volumeVerdict,
            Universe.Inv($"{Number(volumeBefore)} → {Number(volumeAfter)}"),
            splitNearby,
            zeroInWindow == 0 ? "none" : Universe.Inv($"**{zeroInWindow}**"),
            klass,
            confidence,
            why,
            missing,
            trace.Count == 0 ? "none - the breach is the last pair in the series" : string.Join(", ", trace),
            Median(beforeCloses),
            Median(afterCloses),
            ib >= 0 && raw[ib].IsZeroVolume ? "**yes**" : "no",
            ib >= 0 && raw[ib].IsDegenerate ? "**yes**" : "no",
            ib >= 0 && ib + 1 < raw.Count && string.Equals(raw[ib].Close, raw[ib + 1].Close, StringComparison.Ordinal)
                ? "**yes**"
                : "no",
            previous.Close <= 0.001m && current.Close <= 0.001m,
            factorText,
            factorMoves,
            window);
    }

    private static (string Class, string Confidence, string Why) Classify(
        int afterCount,
        int missing,
        int returnedAt,
        int atOld,
        int nearerNew)
    {
        var confidence = afterCount >= Context ? "high" : afterCount >= 3 ? "medium" : "low";

        if (afterCount < 2)
        {
            return (ClassInsufficient, "low",
                Universe.Inv($"only {afterCount} archived close(s) follow the breach, which cannot show whether a level held."));
        }

        if (missing > GapTolerance)
        {
            return (ClassAnomaly, confidence,
                Universe.Inv($"the pair spans {missing} weekdays of missing sessions, more than the {GapTolerance} a holiday run explains, so the two closes are not consecutive trading days and the move is measured across a hole."));
        }

        if (returnedAt == 0)
        {
            return (ClassTransient, confidence,
                Universe.Inv($"the very next archived close is back at the level the breach moved away from, so exactly one session sits at the new level; {atOld} of the following {afterCount} closes are back at the old level."));
        }

        if (atOld > 0)
        {
            return (ClassReversion, confidence,
                Universe.Inv($"{atOld} of the following {afterCount} closes come back to the level the breach moved away from, so the new level does not hold."));
        }

        if (nearerNew == afterCount)
        {
            return (ClassPersistent, confidence,
                Universe.Inv($"none of the following {afterCount} closes returns to the old level and every one of them sits nearer the new level than the old. The level held for as long as the archive runs; no corporate-action cause is asserted, and none is available locally."));
        }

        return (ClassInsufficient, "low",
            Universe.Inv($"of the {afterCount} closes that follow, none returns to the old level but only {nearerNew} sit nearer the new one, so the archive does not settle whether the move held."));
    }

    /// <summary>
    /// How far one close sits from a level, as a ratio rather than a percentage.
    /// </summary>
    /// <remarks>
    /// The absolute natural logarithm of the ratio, which is symmetric: a halving and a doubling
    /// are the same distance, where -50 per cent and +100 per cent are not. These series run from
    /// eighty dollars to a hundredth of a cent, and a percentage measure applied across that range
    /// reports a close plainly sitting at its new level as belonging to neither. Nothing is judged
    /// by this figure except which of two levels a close is nearer; no threshold uses it, and every
    /// close it was applied to is printed beside the verdict.
    /// </remarks>
    private static double Distance(decimal close, decimal level)
    {
        if (close <= 0m || level <= 0m)
        {
            return double.MaxValue;
        }

        return Math.Abs(Math.Log((double)close / (double)level));
    }

    /// <summary>
    /// Which breaches the split restatement adds or removes, against the same rows unrestated.
    /// </summary>
    /// <remarks>
    /// The earlier screen enumerated a refused member's breaches on the raw closes, because a
    /// refusal hands back no series to walk. This walks both and names the difference, so a change
    /// in the count between the two stages is explained by evidence rather than left to be noticed.
    /// A member holding no split has no difference to report, by construction.
    /// </remarks>
    private static string Divergence(
        IReadOnlyList<ClosingPrice> raw,
        IReadOnlyList<ClosingPrice> restated)
    {
        var before = Pairs(raw);
        var after = Pairs(restated);

        var removed = before.Where(p => !after.Contains(p)).ToList();
        var added = after.Where(p => !before.Contains(p)).ToList();

        if (removed.Count == 0 && added.Count == 0)
        {
            return Universe.Inv(
                $"none - the same {after.Count} pair(s) breach whether or not the stored splits are applied");
        }

        var parts = new List<string>();

        if (removed.Count > 0)
        {
            parts.Add(Universe.Inv(
                $"applying the stored split(s) **resolves** {removed.Count} pair(s) that breach on the raw closes: {string.Join(", ", removed)}"));
        }

        if (added.Count > 0)
        {
            parts.Add(Universe.Inv(
                $"applying the stored split(s) **introduces** {added.Count} pair(s) that do not breach on the raw closes: {string.Join(", ", added)}"));
        }

        return Universe.Inv(
            $"{before.Count} breach(es) on the raw closes, {after.Count} once restated - {string.Join("; ", parts)}");
    }

    private static List<string> Pairs(IReadOnlyList<ClosingPrice> prices)
    {
        var pairs = new List<string>();

        for (var i = 1; i < prices.Count; i++)
        {
            var previous = prices[i - 1];
            var current = prices[i];

            if (previous.Close <= 0m || current.Close <= 0m)
            {
                continue;
            }

            if (Math.Abs(current.Close - previous.Close) / previous.Close
                > SplitAdjustment.DefaultMaxUnexplainedMove)
            {
                pairs.Add(Universe.Inv(
                    $"{Text(previous.SessionCloseUtc)} → {Text(current.SessionCloseUtc)}"));
            }
        }

        return pairs;
    }

    private static decimal Mean(List<Raw> raw, int from, int to)
    {
        var total = 0m;
        var count = 0;

        for (var i = Math.Max(from, 0); i <= Math.Min(to, raw.Count - 1); i++)
        {
            if (raw[i].VolumeValue is { } v)
            {
                total += v;
                count++;
            }
        }

        return count == 0 ? -1m : decimal.Round(total / count, 0);
    }

    private static string Median(List<decimal> values)
    {
        if (values.Count == 0)
        {
            return "-";
        }

        var sorted = values.OrderBy(v => v).ToList();

        return Number(sorted[sorted.Count / 2]);
    }

    // ---- the replay, unchanged from the earlier stages ---------------------------------------------

    private static async Task<IReadOnlyList<Observation>> ReplayAsync(
        EodhdDailyPriceNormalizer normalizer,
        Func<byte[], NormalizationInput> inputFor,
        byte[] bytes,
        List<Raw> raw)
    {
        var verdict = await normalizer.NormalizeAsync(inputFor(bytes));
        var surviving = Enumerable.Range(0, raw.Count).ToList();
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

            verdict = await normalizer.NormalizeAsync(inputFor(Serialise(raw, surviving)));
            peels++;
        }

        return verdict.IsQuarantined ? [] : verdict.Observations;
    }

    private static byte[] Serialise(List<Raw> raw, List<int> surviving)
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

    // ---- reading the archive, verbatim -------------------------------------------------------------

    private static List<Raw> ReadRows(byte[] bytes)
    {
        var rows = new List<Raw>();

        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                rows.Add(Read(element));
            }
        }
        catch (JsonException)
        {
            // Not an array of rows. The normaliser says so too, and its verdict is the one used.
        }

        return rows;
    }

    private static Raw Read(JsonElement element)
    {
        var text = element.GetRawText();

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Raw(null, "-", "-", "-", "-", "-", "-", "-", text, false, false, false, null, null);
        }

        var dateText = Field(element, "date");

        DateOnly? date = DateOnly.TryParseExact(
            dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

        var open = Field(element, "open");
        var high = Field(element, "high");
        var low = Field(element, "low");
        var close = Field(element, "close");
        var adjusted = Field(element, "adjusted_close");
        var volume = Field(element, "volume");

        var closeValue = Value(element, "close");
        var volumeValue = Value(element, "volume");
        var adjustedValue = Value(element, "adjusted_close");

        // The vendor's own adjustment factor for this row: what it divides the raw close by to
        // state it in today's shares. It is read, never applied - the platform deliberately stores
        // the raw close, and nothing here changes that. A factor that moves between two sessions is
        // the vendor recording a corporate action between them; a factor that does not is the
        // vendor recording none. That is the only corporate-action evidence the archive carries.
        var factor = closeValue is { } c && c > 0m && adjustedValue is { } adj && adj > 0m
            ? adj / c
            : (decimal?)null;

        return new Raw(
            date,
            dateText,
            open,
            high,
            low,
            close,
            adjusted,
            volume,
            text,
            closeValue is not null && closeValue <= 0m,
            volumeValue == 0m,
            string.Equals(open, high, StringComparison.Ordinal)
                && string.Equals(high, low, StringComparison.Ordinal)
                && string.Equals(low, close, StringComparison.Ordinal),
            volumeValue,
            factor);
    }

    private static string Field(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "-" : value.GetRawText()
            : "-";

    private static decimal? Value(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number)
            ? number
            : null;

    // ---- formatting ---------------------------------------------------------------------------------

    private static string Text(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Text(DateTime instant) =>
        instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Number(decimal value) =>
        value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Percent(decimal fraction) =>
        Universe.Inv($"{decimal.Round(fraction * 100m, 2)}%");

    // ---- the shapes ---------------------------------------------------------------------------------

    private sealed record Raw(
        DateOnly? Date,
        string DateText,
        string Open,
        string High,
        string Low,
        string Close,
        string AdjustedClose,
        string Volume,
        string Text,
        bool IsInvalid,
        bool IsZeroVolume,
        bool IsDegenerate,
        decimal? VolumeValue,
        decimal? Factor)
    {
        public string Ohlcv => Universe.Inv($"{Open}/{High}/{Low}/{Close} v{Volume}");
    }

    private sealed record Breach(
        string Symbol,
        string From,
        string To,
        DateOnly ToDate,
        string PreviousClose,
        string CurrentClose,
        string Move,
        string PreviousOhlcv,
        string CurrentOhlcv,
        string NewLevel,
        string OldLevel,
        string VolumeVerdict,
        string VolumeDetail,
        string SplitNearby,
        string ZeroNearby,
        string Class,
        string Confidence,
        string Why,
        int MissingWeekdays,
        string AfterTrace,
        string MedianBefore,
        string MedianAfter,
        string ZeroVolume,
        string Degenerate,
        string StaleRepeat,
        bool IsSubTick,
        string VendorFactor,
        bool VendorFactorMoves,
        List<string> Window);

    private sealed record Member(
        string Symbol,
        string Payload,
        List<Raw> Rows,
        List<ClosingPrice> Closes,
        IReadOnlyList<ClosingPrice> Restated,
        List<ShareSplit> Splits,
        string Explanation,
        List<Breach> Breaches,
        string Divergence)
    {
        /// <summary>The distinct closing levels the series visits, runs of the same level collapsed.</summary>
        public string LevelLadder
        {
            get
            {
                var steps = new List<string>();
                decimal? last = null;

                foreach (var price in Restated)
                {
                    if (last is { } value && value > 0m
                        && Math.Abs(price.Close - value) / value <= Band)
                    {
                        continue;
                    }

                    steps.Add(Number(price.Close));
                    last = price.Close;

                    if (steps.Count >= 40)
                    {
                        steps.Add("…");

                        break;
                    }
                }

                return string.Join(" → ", steps);
            }
        }
    }
}
