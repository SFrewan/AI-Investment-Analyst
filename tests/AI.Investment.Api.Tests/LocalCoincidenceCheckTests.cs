using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Analytics.Financial;
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
/// Every refused session pair, held against every dated thing this repository already knows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, local-only, and nothing is kept.</strong> No connector is enabled and no
/// request leaves the process — not to EODHD, not to EDGAR, not anywhere. Nothing is persisted,
/// reclassified, released or removed: run, observation and quarantine counts are asserted unchanged
/// either side. No price is repaired, interpolated or reclassified. The normaliser,
/// <see cref="SplitAdjustment"/>, <see cref="CoverageEvaluation"/> and the sealed Gate 6
/// declaration are read and not touched.
/// </para>
/// <para>
/// <strong>What this asks.</strong> Fifteen of the twenty-three refused pairs are persistent
/// repricings that no local source explains, and the price vendor's own adjustment factor records
/// no corporate action at any of the twenty-three dates. This stage asks the only question left
/// that costs nothing: on the days those prices moved, does this repository already hold a record
/// of anything happening? The answer is a coincidence, and a coincidence is all it is reported as.
/// </para>
/// <para>
/// <strong>What counts as a dated local event, and what does not.</strong> Financial observations
/// carry three dates each and they mean different things: the period the figure describes
/// (<c>AsOfUtc</c>), the day it was filed (<c>PublishedAtUtc</c>, which is EDGAR's <c>filed</c>),
/// and the day this platform fetched it (<c>RetrievedAtUtc</c>). Only the first two are events in
/// the world. Company-profile observations are stamped at retrieval on all three, because the
/// submissions normaliser has no event date to use — so their dates are <em>this platform's</em>
/// history, not the company's, and they are reported under their own heading and never allowed to
/// count as a coincidence. The same is true of acquisition runs.
/// </para>
/// <para>
/// <strong>The previous classification is read, not recomputed.</strong> Each breach is rediscovered
/// from the data by the same production path as before, and its class is then looked up by symbol
/// and dates in the characterisation report this repository already holds. A breach the report does
/// not carry is reported as unmatched rather than given a fresh opinion, and the count of matches is
/// asserted — so the two stages cannot quietly disagree.
/// </para>
/// </remarks>
public sealed class LocalCoincidenceCheckTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_COINCIDENCE_CHECK";

    private const string PriceSource = "eodhd-eod";

    private const int MaxPeels = 200;

    /// <summary>Breach pairs the characterisation stage found. Asserted, not assumed.</summary>
    private const int ExpectedBreaches = 23;

    private static readonly Regex RowIndex = new(
        @"^Row (\d+):", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A row of the previous report's breach table: symbol, dates, and the class.</summary>
    private static readonly Regex ReportedClass = new(
        @"^([ABCDE]) - ", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public LocalCoincidenceCheckTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_refused_pair_is_held_against_every_dated_thing_the_repository_holds()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The local coincidence check is off. Set {GateVariable}=1 to run it. It reads only.");

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
        // 1. Rediscover the refused members and their breaches, from the data
        // ================================================================================

        var quarantined = (await context.QuarantinedPayloads
                .AsNoTracking()
                .OrderBy(q => q.QuarantinedAtUtc)
                .ToListAsync())
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

        var refused = new List<Refused>();

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

                var verdict = SplitAdjustment.Apply(
                    closes,
                    splits,
                    SplitAdjustment.DefaultMaxUnexplainedMove);

                if (verdict.IsUsable)
                {
                    continue;
                }

                var restated = SplitAdjustment.Apply(closes, splits, decimal.MaxValue);

                refused.Add(new Refused(symbol, splits, Pairs(restated.Prices)));
            }
        }

        refused = refused.OrderBy(r => r.Symbol, StringComparer.Ordinal).ToList();

        // ================================================================================
        // 2. Every dated thing the repository holds for these members
        // ================================================================================

        var identity = await IdentityAsync();
        var cohorts = await CohortsAsync();

        var lookup = new Dictionary<string, Identity>(StringComparer.Ordinal);

        foreach (var member in identity)
        {
            if (member.Symbol is not null && !lookup.ContainsKey(member.Symbol))
            {
                lookup[member.Symbol] = member;
            }
        }

        // Every identifier shape the store might have used for these members, so the search does not
        // depend on guessing which one the SEC connector wrote.
        var identifiers = new List<string>();

        foreach (var r in refused)
        {
            identifiers.Add(r.Symbol);

            if (lookup.TryGetValue(r.Symbol, out var who))
            {
                identifiers.Add(who.Cik);
                identifiers.Add(who.Cik.TrimStart('0'));

                if (who.Ticker is not null)
                {
                    identifiers.Add(who.Ticker);
                }
            }
        }

        identifiers = identifiers.Distinct(StringComparer.Ordinal).ToList();

        // Materialised rather than projected, so the caveats - which carry EDGAR's form type - come
        // back with the row. The set is six companies' worth, not the whole store.
        var held = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Identifier != null && identifiers.Contains(o.Subject.Identifier!))
            .ToListAsync();

        var events = new Dictionary<string, List<Local>>(StringComparer.Ordinal);
        var acquisitionOnly = new Dictionary<string, List<Local>>(StringComparer.Ordinal);
        var searched = new Dictionary<string, Searched>(StringComparer.Ordinal);

        foreach (var r in refused)
        {
            var mine = new List<string> { r.Symbol };

            if (lookup.TryGetValue(r.Symbol, out var who))
            {
                mine.Add(who.Cik);
                mine.Add(who.Cik.TrimStart('0'));

                if (who.Ticker is not null)
                {
                    mine.Add(who.Ticker);
                }
            }

            var rows = held
                .Where(o => o.Subject.Identifier is not null
                    && mine.Contains(o.Subject.Identifier!, StringComparer.Ordinal))
                .ToList();

            var dated = new List<Local>();
            var undated = new List<Local>();

            // ---- SEC filings, one per accession, with the form type from the caveat ------------
            foreach (var group in rows
                .Where(o => o.Attribute.StartsWith(FinancialFigures.Prefix, StringComparison.Ordinal))
                .GroupBy(o => Universe.Inv($"{o.Provenance.SourceRecordId}|{o.Provenance.PublishedAtUtc:yyyy-MM-dd}|{o.Provenance.AsOfUtc:yyyy-MM-dd}"))
                .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                var form = Form(group);

                dated.Add(new Local(
                    "SEC filing",
                    DateOnly.FromDateTime(first.Provenance.PublishedAtUtc),
                    Universe.Inv($"accession `{first.Provenance.SourceRecordId ?? "(none)"}`, {form}, {group.Count()} figure(s), period end {first.Provenance.AsOfUtc:yyyy-MM-dd}"),
                    "observation store, `financials.*`, Provenance.PublishedAtUtc (EDGAR `filed`)"));

                dated.Add(new Local(
                    "financial period end",
                    DateOnly.FromDateTime(first.Provenance.AsOfUtc),
                    Universe.Inv($"period the figures describe, filed {first.Provenance.PublishedAtUtc:yyyy-MM-dd}, accession `{first.Provenance.SourceRecordId ?? "(none)"}`"),
                    "observation store, `financials.*`, Provenance.AsOfUtc"));
            }

            // ---- splits -----------------------------------------------------------------------
            foreach (var split in r.Splits)
            {
                dated.Add(new Local(
                    "split",
                    DateOnly.FromDateTime(split.EffectiveAtUtc),
                    Universe.Inv($"ratio {Number(split.Ratio)} new shares per old"),
                    "observation store, `security.split-ratio`"));
            }

            // ---- universe lifecycle -------------------------------------------------------------
            if (who is not null && cohorts.TryGetValue(who.Cik, out var cuts))
            {
                foreach (var cut in cuts)
                {
                    dated.Add(new Local(
                        "universe cohort cut",
                        cut,
                        "a membership boundary this member was present at",
                        "`declarations/universe-sample400-2021-2026.json`"));
                }
            }

            // ---- what is dated by this platform rather than by the world -------------------------
            foreach (var group in rows
                .Where(o => o.Attribute.StartsWith("company.", StringComparison.Ordinal))
                .GroupBy(o => o.Provenance.RetrievedAtUtc)
                .OrderBy(g => g.Key))
            {
                undated.Add(new Local(
                    "company profile record",
                    DateOnly.FromDateTime(group.Key),
                    Universe.Inv($"{group.Count()} `company.*` observation(s); the submissions normaliser stamps all three instants at retrieval, so this is when the platform read the profile, not when anything happened"),
                    "observation store, `company.*`"));
            }

            foreach (var run in runs
                .Where(x => x.Request.Subject.Identifier is not null
                    && mine.Contains(x.Request.Subject.Identifier!, StringComparer.Ordinal))
                .OrderBy(x => x.StartedAtUtc))
            {
                undated.Add(new Local(
                    "acquisition run",
                    DateOnly.FromDateTime(run.StartedAtUtc),
                    Universe.Inv($"{run.Request.SourceId.Value}, {run.Request.Category}, {run.Outcome}"),
                    "ingestion run store"));
            }

            events[r.Symbol] = dated.OrderBy(e => e.Date).ToList();
            acquisitionOnly[r.Symbol] = undated.OrderBy(e => e.Date).ToList();

            searched[r.Symbol] = new Searched(
                string.Join(", ", mine.Distinct(StringComparer.Ordinal).Select(x => Universe.Inv($"`{x}`"))),
                rows.Count,
                rows.Count(o => o.Attribute.StartsWith(FinancialFigures.Prefix, StringComparison.Ordinal)),
                rows.Count(o => o.Attribute.StartsWith("company.", StringComparison.Ordinal)),
                rows.Count(o => string.Equals(o.Attribute, ObservationDeduplication.SplitAttribute, StringComparison.Ordinal)),
                string.Join(", ", rows.Select(o => o.Attribute).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(12)));
        }

        // ================================================================================
        // 2b. Whether a null result is a real absence or a failed search
        // ================================================================================
        //
        // A coincidence check that finds nothing is only worth reading if the search is known to
        // have run. This asks the store, repository-wide, where financial observations actually
        // live and under what identifier shape - so "no filing is held for this member" can be
        // separated from "this query looked in the wrong place".

        // EF.Functions.Like rather than StartsWith: the prefix match has to reach the database, and
        // the overload that carries a StringComparison - the one an analyser would ask for - does
        // not translate. This is the form the existing gate-12 reads already use.
        var financialSubjects = (await context.Observations
                .AsNoTracking()
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
                .Select(o => o.Subject.Identifier)
                .Distinct()
                .ToListAsync())
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

        var financialTotal = await context.Observations
            .AsNoTracking()
            .CountAsync(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var sampleShapes = financialSubjects
            .Order(StringComparer.Ordinal)
            .Take(6)
            .ToList();

        var refusedInFinancials = identifiers
            .Where(x => financialSubjects.Contains(x, StringComparer.Ordinal))
            .ToList();

        // ================================================================================
        // 3. The previous classification, read from the report this repository holds
        // ================================================================================

        var classes = await ClassesAsync();

        var checks = new List<Check>();

        foreach (var r in refused)
        {
            foreach (var pair in r.Pairs)
            {
                var key = Universe.Inv($"{r.Symbol}|{Text(pair.From)}|{Text(pair.To)}");

                checks.Add(Assess(
                    r.Symbol,
                    pair,
                    classes.TryGetValue(key, out var klass) ? klass : "(not carried by the previous report)",
                    events[r.Symbol]));
            }
        }

        var matched = checks.Count(c => !c.PreviousClass.StartsWith('('));

        var withEvent = checks.Count(c => c.Label == LabelCoincidence);
        var withSplit = checks.Count(c => c.Label == LabelSplit);
        var without = checks.Count(c => c.Label == LabelNone);
        var insufficient = checks.Count(c => c.Label == LabelInsufficient);

        // ================================================================================
        // 4. The report
        // ================================================================================

        report.AppendLine("# The refused session pairs, against every dated thing this repository holds");
        report.AppendLine();
        report.AppendLine("**Read-only and local-only.** No provider was called and no request left the");
        report.AppendLine("process — not to EODHD, not to EDGAR, not anywhere. Nothing was persisted,");
        report.AppendLine("reclassified, released or removed. No price was repaired, interpolated or");
        report.AppendLine("reclassified. `SplitAdjustment`, `CoverageEvaluation`, the normaliser and the");
        report.AppendLine("sealed Gate 6 declaration are unchanged.");
        report.AppendLine();
        report.AppendLine("**Nothing below establishes causation, and nothing below is permitted to.** A");
        report.AppendLine("date that falls near another date is a coincidence. Where one is found it is");
        report.AppendLine("reported as a coincidence and the causal question is left open, which is what the");
        report.AppendLine("local evidence supports and no more.");
        report.AppendLine();

        // ---- Section 1 -----------------------------------------------------------------------

        report.AppendLine("## Section 1 — Executive summary");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Breach pairs checked | **{checks.Count}** |"));
        report.AppendLine(Universe.Inv($"| …matched to a class in the previous report | **{matched}** |"));
        report.AppendLine(Universe.Inv($"| `Local evidence supports coincidence; causation unconfirmed` | **{withEvent}** |"));
        report.AppendLine(Universe.Inv($"| `Known split/corporate-action evidence exists locally` | **{withSplit}** |"));
        report.AppendLine(Universe.Inv($"| `No relevant local event found` | **{without}** |"));
        report.AppendLine(Universe.Inv($"| `Insufficient local evidence` | **{insufficient}** |"));
        report.AppendLine(Universe.Inv($"| **Breaches where causation is established** | **{checks.Count(c => c.CausationEstablished)}** |"));
        report.AppendLine();
        report.AppendLine("| Window | Breaches with a dated local event in it |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| On one of the two breach dates exactly | **{checks.Count(c => c.OnExactDate != "none")}** |"));
        report.AppendLine(Universe.Inv($"| Within ±1 session | **{checks.Count(c => c.WithinSession != "none")}** |"));
        report.AppendLine(Universe.Inv($"| Within ±5 calendar days | **{checks.Count(c => c.WithinFive != "none")}** |"));
        report.AppendLine(Universe.Inv($"| Within ±10 calendar days | **{checks.Count(c => c.WithinTen != "none")}** |"));
        report.AppendLine();

        report.AppendLine("| Symbol | Dated local events held | Earliest | Latest |");
        report.AppendLine("| --- | ---: | --- | --- |");

        foreach (var r in refused)
        {
            var mine = events[r.Symbol];

            report.AppendLine(Universe.Inv(
                $"| `{r.Symbol}` | **{mine.Count}** | {(mine.Count == 0 ? "-" : Text(mine[0].Date))} | {(mine.Count == 0 ? "-" : Text(mine[^1].Date))} |"));
        }

        report.AppendLine();

        report.AppendLine("### Was the search actually run?");
        report.AppendLine();
        report.AppendLine("A check that finds nothing is worth nothing unless the search is known to have");
        report.AppendLine("reached the right rows. Each member was searched under every identifier shape the");
        report.AppendLine("store might have used for it, and what came back is counted below. A member whose");
        report.AppendLine("`company.*` count is non-zero was found: the join works, and a zero elsewhere on");
        report.AppendLine("that row is an absence in the store rather than a miss by this query.");
        report.AppendLine();
        report.AppendLine("| Symbol | Identifiers searched | Observations found | `financials.*` | `company.*` | `security.split-ratio` | Attributes present |");
        report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | --- |");

        foreach (var r in refused)
        {
            var w = searched[r.Symbol];

            report.AppendLine(Universe.Inv(
                $"| `{r.Symbol}` | {w.Identifiers} | **{w.Total}** | **{w.Financials}** | {w.Company} | {w.Splits} | {(w.Attributes.Length == 0 ? "-" : w.Attributes)} |"));
        }

        report.AppendLine();
        report.AppendLine("And repository-wide, so the absence above can be placed:");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        report.AppendLine(Universe.Inv($"| `financials.*` observations in the store | **{financialTotal}** |"));
        report.AppendLine(Universe.Inv($"| Distinct subjects holding them | **{financialSubjects.Count}** |"));
        report.AppendLine(Universe.Inv($"| The identifier shape they use | {(sampleShapes.Count == 0 ? "-" : string.Join(", ", sampleShapes.Select(x => Universe.Inv($"`{x}`"))))} |"));
        report.AppendLine(Universe.Inv($"| Identifier shapes searched for the six | {string.Join(", ", identifiers.Take(8).Select(x => Universe.Inv($"`{x}`")))}{(identifiers.Count > 8 ? ", …" : string.Empty)} |"));
        report.AppendLine(Universe.Inv($"| …of those, present among the financial subjects | **{refusedInFinancials.Count}** |"));
        report.AppendLine();

        // ---- the compact summary the brief asked for -------------------------------------------

        report.AppendLine("### Compact summary");
        report.AppendLine();
        report.AppendLine("| Symbol | Breach | Previous classification | Local event? | Event type | Temporal coincidence | Causation established? |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach (var c in checks)
        {
            report.AppendLine(Universe.Inv(
                $"| `{c.Symbol}` | {Text(c.From)} → {Text(c.To)} | {c.PreviousClass} | {c.HasEvent} | {c.EventType} | {c.Coincidence} | **{(c.CausationEstablished ? "yes" : "no")}** |"));
        }

        report.AppendLine();

        // ---- Section 2 -----------------------------------------------------------------------

        report.AppendLine("## Section 2 — The complete breach table");
        report.AppendLine();
        report.AppendLine("| # | Symbol | Breach pair | Move | Previous class | On the exact date | Within ±1 session | Within ±5 days | Within ±10 days | Event type | Event date | Source | Coincides | Causation | Label |");
        report.AppendLine("| ---: | --- | --- | ---: | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        var n = 0;

        foreach (var c in checks)
        {
            n++;

            report.AppendLine(Universe.Inv(
                $"| {n} | `{c.Symbol}` | {Text(c.From)} → {Text(c.To)} | **{c.Move}** | {c.PreviousClass} | {c.OnExactDate} | {c.WithinSession} | {c.WithinFive} | {c.WithinTen} | {c.EventType} | {c.EventDate} | {c.Source} | {c.Coincidence} | **{(c.CausationEstablished ? "yes" : "no")}** | `{c.Label}` |"));
        }

        report.AppendLine();
        report.AppendLine("Every *causation* cell reads **no**, and that is a finding rather than an");
        report.AppendLine("omission. Nothing this repository holds states that any recorded event caused any");
        report.AppendLine("price move; the store holds figures, filing dates and periods, and none of them");
        report.AppendLine("carries a claim about a price. A coincidence is reported where one exists and the");
        report.AppendLine("causal question is left where the evidence leaves it.");
        report.AppendLine();

        // ---- Section 3 -----------------------------------------------------------------------

        report.AppendLine("## Section 3 — The local evidence, in detail");
        report.AppendLine();

        var any = checks.Where(c => c.Nearby.Count > 0).ToList();

        if (any.Count == 0)
        {
            report.AppendLine("**No breach has a dated local event within ten calendar days of it.** That is the");
            report.AppendLine("result of the search, not a failure to run it: the events each member holds are");
            report.AppendLine("listed below with their dates, and none of them falls near a breach.");
            report.AppendLine();
        }
        else
        {
            foreach (var c in any)
            {
                report.AppendLine(Universe.Inv($"### `{c.Symbol}` {Text(c.From)} → {Text(c.To)} ({c.Move})"));
                report.AppendLine();
                report.AppendLine(Universe.Inv($"Previous classification: {c.PreviousClass}. Label: `{c.Label}`."));
                report.AppendLine();
                report.AppendLine("| Event | Date | Days from the breach | What exactly is held | Where it is held |");
                report.AppendLine("| --- | --- | ---: | --- | --- |");

                foreach (var e in c.Nearby)
                {
                    report.AppendLine(Universe.Inv(
                        $"| {e.Event.Kind} | {Text(e.Event.Date)} | {e.Days} | {e.Event.Detail} | {e.Event.Source} |"));
                }

                report.AppendLine();
                report.AppendLine("**No causal claim is made from the rows above.** A filing dated near a price");
                report.AppendLine("move is a filing dated near a price move. This repository holds no statement");
                report.AppendLine("that connects them, and none is invented here.");
                report.AppendLine();
            }
        }

        report.AppendLine("### Everything each member holds, dated");
        report.AppendLine();

        foreach (var r in refused)
        {
            report.AppendLine(Universe.Inv($"#### `{r.Symbol}`"));
            report.AppendLine();

            var mine = events[r.Symbol];

            if (mine.Count == 0)
            {
                report.AppendLine("No dated local event of any kind is held for this member.");
                report.AppendLine();
            }
            else
            {
                report.AppendLine("| Event | Date | What exactly is held | Where it is held |");
                report.AppendLine("| --- | --- | --- | --- |");

                foreach (var e in mine)
                {
                    report.AppendLine(Universe.Inv(
                        $"| {e.Kind} | {Text(e.Date)} | {e.Detail} | {e.Source} |"));
                }

                report.AppendLine();
            }

            var theirs = acquisitionOnly[r.Symbol];

            if (theirs.Count > 0)
            {
                report.AppendLine("Dated by this platform rather than by the world, and therefore excluded from");
                report.AppendLine("every coincidence test above:");
                report.AppendLine();
                report.AppendLine("| Record | Date | What it is | Where it is held |");
                report.AppendLine("| --- | --- | --- | --- |");

                foreach (var e in theirs)
                {
                    report.AppendLine(Universe.Inv(
                        $"| {e.Kind} | {Text(e.Date)} | {e.Detail} | {e.Source} |"));
                }

                report.AppendLine();
            }
        }

        // ---- Section 4 -----------------------------------------------------------------------

        report.AppendLine("## Section 4 — `SHPW.US`, chronologically");
        report.AppendLine();

        var shpw = checks.Where(c => string.Equals(c.Symbol, "SHPW.US", StringComparison.Ordinal)).ToList();

        if (shpw.Count == 0)
        {
            report.AppendLine("`SHPW.US` produced no breaches in this run, which contradicts the previous");
            report.AppendLine("stage. That is reported rather than explained away.");
            report.AppendLine();
        }
        else
        {
            report.AppendLine(Universe.Inv(
                $"All **{shpw.Count}** breaches in date order, interleaved with every dated local event this repository holds for the member, so the two timelines can be read against each other."));
            report.AppendLine();
            report.AppendLine("| Date | What | Detail | Sub-penny pair? |");
            report.AppendLine("| --- | --- | --- | --- |");

            var timeline = new List<(DateOnly Date, string What, string Detail, string Tick)>();

            foreach (var c in shpw)
            {
                timeline.Add((
                    c.To,
                    Universe.Inv($"**breach** {Text(c.From)} → {Text(c.To)}"),
                    Universe.Inv($"{c.Move}; {c.PreviousClass}; `{c.Label}`"),
                    c.SubPenny ? "**yes**" : "no"));
            }

            foreach (var e in events["SHPW.US"])
            {
                timeline.Add((e.Date, e.Kind, e.Detail, "-"));
            }

            foreach (var entry in timeline.OrderBy(x => x.Date))
            {
                report.AppendLine(Universe.Inv(
                    $"| {Text(entry.Date)} | {entry.What} | {entry.Detail} | {entry.Tick} |"));
            }

            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"**{shpw.Count(c => c.SubPenny)} of the {shpw.Count} are sub-penny pairs**, flagged above and separated here as the brief requires. In those, both closes sit at or below $0.001, where a single minimum increment is itself a move larger than the tolerance. They are **neither proven anomalies nor proven real moves**: the arithmetic cannot distinguish the two at that price, and nothing about tick size or `SplitAdjustment` is changed, proposed or implied by saying so."));
            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"Local events held for `SHPW.US`: **{events["SHPW.US"].Count}**. Breaches with one within ten days: **{shpw.Count(c => c.WithinTen != "none")}**."));
            report.AppendLine();
        }

        // ---- Section 5 -----------------------------------------------------------------------

        report.AppendLine("## Section 5 — Evidence gaps");
        report.AppendLine();
        report.AppendLine("What this check could not do, stated so that its silence is not mistaken for a");
        report.AppendLine("negative result.");
        report.AppendLine();
        report.AppendLine("- **No filing index is held.** The platform stores EDGAR *company facts* and");
        report.AppendLine("  *submissions*, and the filing dates it holds are those attached to financial");
        report.AppendLine("  figures — one date per accession that reported a figure. A filing that reported");
        report.AppendLine("  no figure this platform maps is not in the store at all. 8-Ks, which are where a");
        report.AppendLine("  merger, a delisting notice or a going-concern warning would be announced, carry");
        report.AppendLine("  no us-gaap facts of the kind read here and are therefore invisible to it. **The");
        report.AppendLine("  absence of an 8-K in this check is not evidence that no 8-K exists.**");
        report.AppendLine("- **The submissions data carries no event dates.** `company.*` observations are");
        report.AppendLine("  stamped at retrieval on all three instants, because the normaliser has no event");
        report.AppendLine("  date to use. They say what the company was called and where it listed, not when");
        report.AppendLine("  anything happened, and they are excluded from every window above.");
        report.AppendLine("- **No dividend, spin-off, symbol-change, halt or delisting feed is held**, for");
        report.AppendLine("  any member. The platform acquired prices and splits.");
        report.AppendLine("- **No news, announcement or press-release source is held.**");
        report.AppendLine("- **The price vendor's own adjustment factor is unchanged at all 23 dates**, as");
        report.AppendLine("  the previous stage measured, so the one corporate-action signal the price");
        report.AppendLine("  payload does carry says nothing happened at any of them.");
        report.AppendLine("- **A coincidence found here could not establish causation even in principle.**");
        report.AppendLine("  Nothing in this repository relates an event to a price. Establishing that a");
        report.AppendLine("  filing moved a price needs a claim no local record makes.");
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "gate6-local-coincidence-check.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -------------------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());

        Assert.Equal(
            "LGIQ.US NGM.US ONEM.US QUMU.US SDC.US SHPW.US",
            string.Join(" ", refused.Select(r => r.Symbol)));

        Assert.Equal(ExpectedBreaches, checks.Count);

        // Every breach must carry the class the previous stage gave it. An unmatched breach means
        // the two stages disagree about what they are looking at, and that fails here rather than
        // producing a table with a blank in it.
        Assert.Equal(ExpectedBreaches, matched);

        // Every breach carries exactly one of the four permitted labels. None is left unlabelled.
        Assert.All(checks, c => Assert.Contains(
            c.Label,
            new[] { LabelCoincidence, LabelNone, LabelSplit, LabelInsufficient }));

        Assert.Equal(0.5m, SplitAdjustment.DefaultMaxUnexplainedMove);
    }

    // ---- the four permitted labels -----------------------------------------------------------

    private const string LabelCoincidence = "Local evidence supports coincidence; causation unconfirmed";
    private const string LabelNone = "No relevant local event found";
    private const string LabelSplit = "Known split/corporate-action evidence exists locally";
    private const string LabelInsufficient = "Insufficient local evidence";

    // ---- the assessment ------------------------------------------------------------------------

    private static Check Assess(string symbol, Pair pair, string previousClass, List<Local> events)
    {
        var nearby = new List<Near>();

        foreach (var e in events)
        {
            var days = Math.Min(
                Math.Abs(e.Date.DayNumber - pair.From.DayNumber),
                Math.Abs(e.Date.DayNumber - pair.To.DayNumber));

            if (days <= 10)
            {
                nearby.Add(new Near(e, days));
            }
        }

        nearby = nearby.OrderBy(x => x.Days).ThenBy(x => x.Event.Date).ToList();

        var exact = nearby.Where(x => x.Days == 0).ToList();
        var session = nearby.Where(x => Sessions(x.Event.Date, pair) <= 1).ToList();
        var five = nearby.Where(x => x.Days <= 5).ToList();

        var split = nearby.FirstOrDefault(x =>
            string.Equals(x.Event.Kind, "split", StringComparison.Ordinal));

        var nearest = nearby.Count > 0 ? nearby[0] : null;

        var label = events.Count == 0
            ? LabelInsufficient
            : split is not null
                ? LabelSplit
                : nearby.Count > 0
                    ? LabelCoincidence
                    : LabelNone;

        return new Check(
            symbol,
            pair.From,
            pair.To,
            pair.Move,
            previousClass,
            Describe(exact),
            Describe(session),
            Describe(five),
            Describe(nearby),
            nearest is null ? "none" : nearest.Event.Kind,
            nearest is null ? "-" : Text(nearest.Event.Date),
            nearest is null ? "-" : nearest.Event.Source,
            nearest is null
                ? "no"
                : Universe.Inv($"**yes** - {nearest.Days} day(s) away"),
            nearby.Count > 0 ? "yes" : "no",

            // Nothing in this repository relates an event to a price. Causation is therefore never
            // established here, and the column is a constant for a stated reason rather than an
            // opinion formed case by case.
            false,
            label,
            nearby,
            pair.SubPenny);
    }

    private static int Sessions(DateOnly date, Pair pair) =>
        Math.Min(
            CoverageEvaluation.ExpectedSessions(Min(date, pair.From).AddDays(1), Max(date, pair.From).AddDays(-1)),
            CoverageEvaluation.ExpectedSessions(Min(date, pair.To).AddDays(1), Max(date, pair.To).AddDays(-1)));

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string Describe(List<Near> found) =>
        found.Count == 0
            ? "none"
            : Universe.Inv($"**{found.Count}** ({string.Join(", ", found.Select(x => x.Event.Kind).Distinct(StringComparer.Ordinal))})");

    private static string Form(IEnumerable<Observation> group)
    {
        foreach (var observation in group)
        {
            foreach (var caveat in observation.Caveats)
            {
                if (caveat.StartsWith("From a ", StringComparison.Ordinal)
                    && caveat.EndsWith(" filing.", StringComparison.Ordinal))
                {
                    return caveat["From a ".Length..^" filing.".Length];
                }
            }
        }

        return "form not stated";
    }

    // ---- breaches, by the same path as the previous stage ---------------------------------------

    private static List<Pair> Pairs(IReadOnlyList<ClosingPrice> prices)
    {
        var pairs = new List<Pair>();

        for (var i = 1; i < prices.Count; i++)
        {
            var previous = prices[i - 1];
            var current = prices[i];

            if (previous.Close <= 0m || current.Close <= 0m)
            {
                continue;
            }

            var move = Math.Abs(current.Close - previous.Close) / previous.Close;

            if (move <= SplitAdjustment.DefaultMaxUnexplainedMove)
            {
                continue;
            }

            pairs.Add(new Pair(
                DateOnly.FromDateTime(previous.SessionCloseUtc),
                DateOnly.FromDateTime(current.SessionCloseUtc),
                Percent(move),
                previous.Close <= 0.001m && current.Close <= 0.001m));
        }

        return pairs;
    }

    // ---- reading what the repository already holds ------------------------------------------------

    /// <summary>The previous stage's class for each breach, read from its own report.</summary>
    private static async Task<Dictionary<string, string>> ClassesAsync()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        var path = Universe.RepositoryPath(
            "artifacts", "verify", "gate6-unexplained-move-characterisation.md");

        if (!File.Exists(path))
        {
            return found;
        }

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|').Select(c => c.Trim()).ToList();

            string? symbol = null;
            string? from = null;
            string? to = null;
            string? klass = null;

            foreach (var cell in cells)
            {
                var bare = cell.Replace("*", string.Empty, StringComparison.Ordinal).Trim();

                if (cell.StartsWith('`') && cell.EndsWith('`') && cell.Contains(".US", StringComparison.Ordinal))
                {
                    symbol ??= cell.Trim('`');
                }
                else if (from is null && bare.Length == 23 && bare.Contains('→', StringComparison.Ordinal))
                {
                    var halves = bare.Split('→', StringSplitOptions.TrimEntries);

                    if (halves.Length == 2 && halves[0].Length == 10 && halves[1].Length == 10)
                    {
                        from = halves[0];
                        to = halves[1];
                    }
                }
                else if (ReportedClass.IsMatch(bare))
                {
                    klass ??= bare;
                }
            }

            if (symbol is not null && from is not null && to is not null && klass is not null)
            {
                found[Universe.Inv($"{symbol}|{from}|{to}")] = klass;
            }
        }

        return found;
    }

    private static async Task<List<Identity>> IdentityAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m =>
            {
                var ticker = m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                return new Identity(
                    m.GetProperty("Cik").GetString() ?? string.Empty,
                    ticker,
                    ticker is null ? null : AcquisitionPlanning.SymbolFor(ticker));
            })
            .ToList();
    }

    private static async Task<Dictionary<string, List<DateOnly>>> CohortsAsync()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        return manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => m.GetProperty("Cohorts").EnumerateArray()
                    .Select(c => DateOnly.Parse(c.GetString() ?? "0001-01-01", CultureInfo.InvariantCulture))
                    .OrderBy(d => d)
                    .ToList(),
                StringComparer.Ordinal);
    }

    // ---- the replay, unchanged ---------------------------------------------------------------------

    private static async Task<IReadOnlyList<Observation>> ReplayAsync(
        EodhdDailyPriceNormalizer normalizer,
        Func<byte[], NormalizationInput> inputFor,
        byte[] bytes,
        List<string> raw)
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

    // ---- formatting -------------------------------------------------------------------------------

    private static string Text(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Number(decimal value) =>
        value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Percent(decimal fraction) =>
        Universe.Inv($"{decimal.Round(fraction * 100m, 2)}%");

    // ---- the shapes ---------------------------------------------------------------------------------

    private sealed record Identity(string Cik, string? Ticker, string? Symbol);

    private sealed record Pair(DateOnly From, DateOnly To, string Move, bool SubPenny);

    private sealed record Refused(string Symbol, List<ShareSplit> Splits, List<Pair> Pairs);

    private sealed record Local(string Kind, DateOnly Date, string Detail, string Source);

    private sealed record Searched(
        string Identifiers,
        int Total,
        int Financials,
        int Company,
        int Splits,
        string Attributes);

    private sealed record Near(Local Event, int Days);

    private sealed record Check(
        string Symbol,
        DateOnly From,
        DateOnly To,
        string Move,
        string PreviousClass,
        string OnExactDate,
        string WithinSession,
        string WithinFive,
        string WithinTen,
        string EventType,
        string EventDate,
        string Source,
        string Coincidence,
        string HasEvent,
        bool CausationEstablished,
        string Label,
        List<Near> Nearby,
        bool SubPenny);
}
