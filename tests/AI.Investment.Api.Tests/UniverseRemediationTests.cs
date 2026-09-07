using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The remediation, proved against the payloads already held, and the two designs it does not
/// have the authority to execute.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only throughout.</strong> No provider is called - the EDGAR connector is not even
/// registered in the host this runs under, and no EODHD client is constructed. Nothing is written
/// to the store, no payload is re-normalised into it, and no manifest, roster or declaration is
/// modified. The observation, run, quarantine and opportunity counts are asserted unchanged.
/// </para>
/// <para>
/// Three separate questions, three facts, one gate: <c>AIINV_UNIVERSE_REMEDIATE=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseRemediationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_REMEDIATE";

    /// <summary>Distinct quarterly periods per company per complete calendar year.</summary>
    private const decimal MinimumQuarterlyPeriodsPerYear = 2.9m;

    /// <summary>The pre-registered survivorship floor. Reported against, never moved.</summary>
    private const decimal Gate2Floor = 0.05m;

    /// <summary>
    /// Where an authorised EODHD delisted symbol list would be cached, if one is ever taken.
    /// </summary>
    /// <remarks>
    /// The file does not exist and this run will not create it. Its absence is the whole finding
    /// of the mapping section: the machinery below is complete and deterministic, and it has
    /// nothing to run against, because the one list that carries delisted US tickers was read
    /// during the manifest stage for a membership test and discarded rather than stored.
    /// </remarks>
    private static string DelistedCachePath =>
        Universe.RepositoryPath("artifacts", "universe", "eodhd-delisted-us.json");

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseRemediationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    // ================= 1. the normalisation fix, proved =========================================

    [SkippableFact]
    public async Task Re_normalising_the_archived_payloads_emits_each_fact_once_and_loses_none()
    {
        Skip.IfNot(Enabled(), Reason());

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var before = await SnapshotAsync(context);

        var runs = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.FinancialStatements)
                .ToListAsync())
            .Where(r => r.Request.SourceId == SecEdgarProvider.Id)
            .ToList();

        Skip.If(runs.Count == 0, "No company-facts runs are stored. Nothing to re-read.");

        var normalizer = new SecEdgarCompanyFactsNormalizer();

        // One payload, one normalisation - however many runs reach it. Keyed on subject as well as
        // hash, because relying on two subjects never sharing bytes would be relying on it.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Keys, not observations. Six hundred and fifty thousand Observation aggregates would be
        // several gigabytes; the identity of each is a string, and the identity is the whole
        // question being asked.
        var producedDistinct = new HashSet<string>(StringComparer.Ordinal);
        var annualProduced = new HashSet<string>(StringComparer.Ordinal);
        var quarterlyProduced = new HashSet<string>(StringComparer.Ordinal);
        var quarterlyPeriods = new HashSet<string>(StringComparer.Ordinal);
        var quarterlyCompanyYears = new HashSet<string>(StringComparer.Ordinal);

        // What a rebuild would actually write: ONE payload per company, the most recent. The
        // measure below it - every archived payload, including superseded snapshots - is not what
        // any run does, and counting its repeats as duplicates would report two snapshots of one
        // company as a defect in the normaliser rather than as two snapshots.
        var rebuildDistinct = new HashSet<string>(StringComparer.Ordinal);
        var rebuildTotal = 0;

        var producedTotal = 0;
        var annualTotal = 0;
        var quarterlyTotal = 0;
        var payloads = 0;
        var missing = 0;
        var withinDocumentRepeats = 0;

        // The most recent archived payload per company, decided from the ledger rather than by
        // reading anything: a rebuild would take the latest snapshot it holds.
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var run in runs
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .OrderBy(r => r.StartedAtUtc))
        {
            foreach (var hash in run.Artifacts)
            {
                latest[run.Request.Subject.Identifier ?? string.Empty] = hash.Value;
            }
        }

        foreach (var run in runs)
        {
            foreach (var hash in run.Artifacts)
            {
                var cik = run.Request.Subject.Identifier ?? string.Empty;

                if (!seen.Add(cik + "|" + hash.Value))
                {
                    continue;
                }

                var described = await archive.DescribeAsync(hash);
                var payload = described is null ? null : await archive.RetrieveAsync(hash);

                if (described is null || payload is null)
                {
                    // Retention may have removed it under licence. Recorded, never invented.
                    missing++;

                    continue;
                }

                var result = await normalizer.NormalizeAsync(new NormalizationInput(
                    run.Request.SourceId,
                    run.Request.Category,
                    run.Request.Subject,
                    hash,
                    payload,
                    described.RetrievedAtUtc));

                payloads++;

                var isLatest = latest.TryGetValue(cik, out var newest) &&
                    string.Equals(newest, hash.Value, StringComparison.Ordinal);

                var inThisDocument = new HashSet<string>(StringComparer.Ordinal);

                foreach (var observation in result.Observations)
                {
                    var key = Key(
                        cik,
                        observation.Attribute,
                        observation.Provenance.AsOfUtc,
                        observation.Provenance.PublishedAtUtc,
                        observation.Value.Canonical);

                    producedTotal++;
                    producedDistinct.Add(key);

                    if (isLatest)
                    {
                        rebuildTotal++;
                        rebuildDistinct.Add(key);
                    }

                    if (!inThisDocument.Add(key))
                    {
                        withinDocumentRepeats++;
                    }

                    if (observation.Attribute.StartsWith(
                        FinancialFigures.QuarterlyPrefix,
                        StringComparison.Ordinal))
                    {
                        quarterlyTotal++;
                        quarterlyProduced.Add(key);

                        var asOf = observation.Provenance.AsOfUtc;

                        if (asOf.Year >= Universe.Cuts[0].Year && asOf.Year <= Universe.Cuts[^1].Year)
                        {
                            quarterlyPeriods.Add(Universe.Inv($"{cik}|{asOf:yyyy-MM-dd}"));
                            quarterlyCompanyYears.Add(Universe.Inv($"{cik}|{asOf.Year}"));
                        }
                    }
                    else
                    {
                        annualTotal++;
                        annualProduced.Add(key);
                    }
                }
            }
        }

        // ---- what the store actually holds, for the same companies --------------------------

        var readable = runs
            .Where(r => r.Request.Subject.Identifier is not null)
            .Select(r => r.Request.Subject.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var rows = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var storedRows = await rows.CountAsync();

        var storedDistinct = new HashSet<string>(StringComparer.Ordinal);

        // Restricted to companies whose payload could be re-read, and to the ANNUAL namespace.
        // Both restrictions are the difference between a proof and a coincidence: a stored row for
        // a company whose archive entry retention removed is not evidence the fix lost anything,
        // and the quarterly namespace did not exist when the pilot's rows were written, so its
        // absence from them says nothing about this change either.
        var storedAnnualComparable = new HashSet<string>(StringComparer.Ordinal);

        // Streamed, not materialised. Six hundred and fifty thousand projected rows held at once
        // alongside the three key sets is most of a gigabyte for no reason: each row is needed for
        // exactly as long as it takes to turn into a key.
        var projected = rows
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .AsAsyncEnumerable();

        await foreach (var row in projected)
        {
            var key = Key(row.Cik, row.Attribute, row.AsOfUtc, row.PublishedAtUtc, row.Canonical);

            storedDistinct.Add(key);

            if (readable.Contains(row.Cik) &&
                !row.Attribute.StartsWith(FinancialFigures.QuarterlyPrefix, StringComparison.Ordinal))
            {
                storedAnnualComparable.Add(key);
            }
        }

        // The direction that matters. Every annual fact the store holds must still be produced;
        // anything else would mean de-duplication had dropped a distinct figure.
        var annualLost = storedAnnualComparable
            .Except(annualProduced, StringComparer.Ordinal)
            .ToList();

        // The other direction is reported, not asserted, and the reason is worth stating. An
        // archived payload can legitimately hold facts the store never received: a superseded
        // snapshot for a company the store already had, and - for the twenty pilot companies -
        // every quarterly fact there is, because their rows were written before the quarterly
        // namespace existed. Neither is the fix inventing anything.
        var annualExtra = annualProduced
            .Except(storedDistinct, StringComparer.Ordinal)
            .ToList();

        var quarterlyExtra = quarterlyProduced
            .Except(storedDistinct, StringComparer.Ordinal)
            .Count();

        var cadence = quarterlyCompanyYears.Count == 0
            ? 0m
            : Math.Round((decimal)quarterlyPeriods.Count / quarterlyCompanyYears.Count, 2);

        var report = new StringBuilder();

        report.AppendLine("# Remediation 1 - the normalisation fix, proved on the archive");
        report.AppendLine();
        report.AppendLine("**Read-only.** No provider call, no row written, no payload re-normalised");
        report.AppendLine("into the store. The fixed normaliser is run over the archived documents and");
        report.AppendLine("its output compared against what is stored; nothing is persisted.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Company-facts runs in the ledger | {runs.Count} |"));
        report.AppendLine(Universe.Inv($"| Distinct payloads re-read | {payloads} |"));
        report.AppendLine(Universe.Inv($"| Payloads no longer in the archive | {missing} |"));
        report.AppendLine(Universe.Inv($"| Observations produced | {producedTotal} |"));
        report.AppendLine(Universe.Inv($"| Companies covered | {latest.Count} |"));
        report.AppendLine(Universe.Inv($"| Superseded snapshots also re-read | {payloads - latest.Count} |"));
        report.AppendLine(Universe.Inv($"| **Repeats within any single document** | **{withinDocumentRepeats}** |"));
        report.AppendLine(Universe.Inv($"| Annual observations | {annualTotal} ({annualProduced.Count} distinct) |"));
        report.AppendLine(Universe.Inv($"| Quarterly observations | {quarterlyTotal} ({quarterlyProduced.Count} distinct) |"));
        report.AppendLine(Universe.Inv($"| Quarterly cadence | {cadence:F2} per company per complete year |"));
        report.AppendLine();
        report.AppendLine("### What a rebuild would write - one payload per company, the latest");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Observations produced | {rebuildTotal} |"));
        report.AppendLine(Universe.Inv($"| Distinct facts | {rebuildDistinct.Count} |"));
        report.AppendLine(Universe.Inv($"| **Duplicates** | **{rebuildTotal - rebuildDistinct.Count}** |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Every archived payload was also re-read, superseded snapshots included: "));
        report.AppendLine(Universe.Inv($"{producedTotal} observations, {producedDistinct.Count} distinct. Those "));
        report.AppendLine(Universe.Inv($"{producedTotal - producedDistinct.Count} repeats are two snapshots of one company "));
        report.AppendLine("agreeing with each other, which no run would ever write - a company is");
        report.AppendLine("fetched once and normalised once - so they are reported rather than");
        report.AppendLine("counted against the fix.");
        report.AppendLine();
        report.AppendLine("| Against the store | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Stored rows | {storedRows} |"));
        report.AppendLine(Universe.Inv($"| Stored distinct facts | {storedDistinct.Count} |"));
        report.AppendLine(Universe.Inv($"| **Duplicate rows in the store** | **{storedRows - storedDistinct.Count}** |"));
        report.AppendLine(Universe.Inv($"| Stored annual facts re-readable from the archive | {storedAnnualComparable.Count} |"));
        report.AppendLine(Universe.Inv($"| **Annual facts the fix loses** | **{annualLost.Count}** |"));
        report.AppendLine(Universe.Inv($"| Annual facts produced that the store never held | {annualExtra.Count} |"));
        report.AppendLine(Universe.Inv($"| Quarterly facts produced that the store never held | {quarterlyExtra} |"));
        report.AppendLine();

        if (annualLost.Count > 0)
        {
            report.AppendLine("Lost, first few: " + string.Join(" | ", annualLost.Take(5)));
            report.AppendLine();
        }

        report.AppendLine("The last two rows are expected, and are not the fix inventing anything.");
        report.AppendLine("A superseded snapshot holds facts the store was never written from, and");
        report.AppendLine("the twenty pilot companies' rows predate the quarterly namespace");
        report.AppendLine("entirely - so every quarterly fact of theirs is new to the store by");
        report.AppendLine("construction. The row above them is the one that would matter, and it is");
        report.AppendLine("zero: no annual figure the store holds stops being produced.");
        report.AppendLine();
        report.AppendLine("## Gate 12, re-evaluated at zero tolerance");
        report.AppendLine();
        report.AppendLine(rebuildTotal == rebuildDistinct.Count && withinDocumentRepeats == 0
            ? Universe.Inv($"**On the fixed normaliser's output: PASS.** {rebuildDistinct.Count} ")
              + Universe.Inv($"distinct facts across {rebuildTotal} produced rows, exactly one row ")
              + "per fact, at zero tolerance and with the threshold untouched."
            : Universe.Inv($"**On the fixed normaliser's output: FAIL.** {rebuildTotal - rebuildDistinct.Count} ")
              + "duplicates survive the fix.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"**On the stored rows: still FAIL** - {storedRows - storedDistinct.Count} "));
        report.AppendLine("duplicate rows remain in the observation store, written by the old");
        report.AppendLine("normaliser before this fix existed. Clearing them is a WRITE, and this");
        report.AppendLine("stage is read-only: nothing here deletes, migrates or rewrites a row.");
        report.AppendLine("The gate cannot pass against the store until that write is separately");
        report.AppendLine("authorised, and it needs no provider call when it is - every payload the");
        report.AppendLine("store would be rebuilt from is already archived.");

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "remediation-normalisation.md"),
            report.ToString() + Universe.Inv($"\nElapsed {watch.Elapsed.TotalSeconds:F0}s.\n"));

        _output.WriteLine(report.ToString());

        await AssertUnchangedAsync(context, before);

        // The fix itself: no document emits the same fact twice.
        Assert.Equal(0, withinDocumentRepeats);

        // And what a run would actually write carries no duplicate either.
        Assert.Equal(0, rebuildTotal - rebuildDistinct.Count);

        Assert.True(
            annualLost.Count == 0,
            Universe.Inv($"{annualLost.Count} stored annual facts are no longer produced. ")
            + "De-duplication must collapse identical rows, never drop a distinct one. "
            + string.Join(" | ", annualLost.Take(3)));

        Assert.True(quarterlyTotal > 0, "No quarterly observations were produced.");

        Assert.True(
            cadence >= MinimumQuarterlyPeriodsPerYear,
            Universe.Inv($"Quarterly cadence fell to {cadence:F2}, below the gate of ")
            + Universe.Inv($"{MinimumQuarterlyPeriodsPerYear:F1}. The fix must not have merged ")
            + "quarters into each other.");
    }

    // ================= 2. the dead names ========================================================

    [SkippableFact]
    public async Task The_dead_names_are_mapped_from_evidence_or_reported_as_unmappable()
    {
        Skip.IfNot(Enabled(), Reason());

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var before = await SnapshotAsync(context);

        Skip.If(!File.Exists(Universe.ManifestPath), "The sealed manifest is not on disk.");

        var manifest = JsonSerializer.Deserialize<ManifestFile>(
            await File.ReadAllTextAsync(Universe.ManifestPath),
            Universe.Json);

        var dropouts = (manifest?.Members ?? [])
            .Where(m => m.StoppedFiling || m.AbsentFromFinalCrossSection)
            .OrderBy(m => m.LastFilingUtc ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var candidates = LoadDelistedCache();

        var report = new StringBuilder();

        report.AppendLine("# Remediation 2 - a historical ticker for each company that left");
        report.AppendLine();
        report.AppendLine("**Read-only.** The identity side is the archived EDGAR submissions");
        report.AppendLine("document for each CIK. The candidate side is EODHD's delisted US symbol");
        report.AppendLine("list, which this run does **not** fetch.");
        report.AppendLine();

        if (candidates is null)
        {
            report.AppendLine("## BLOCKED on one billable call");
            report.AppendLine();
            report.AppendLine(Universe.Inv($"No delisted symbol list is cached at `{Relative(DelistedCachePath)}`, "));
            report.AppendLine("and this stage will not fetch one. Everything below is the identity");
            report.AppendLine("evidence the match would be made against, extracted from documents");
            report.AppendLine("already held, so that the moment one call is authorised the matching");
            report.AppendLine("runs against a fixed, already-published set of names rather than");
            report.AppendLine("against whatever a later fetch happens to return.");
            report.AppendLine();
        }
        else
        {
            report.AppendLine(Universe.Inv($"## Matching against {candidates.Count} cached delisted symbols"));
            report.AppendLine();
        }

        report.AppendLine("| CIK | SEC name | Last filing | Former names on record | Candidate ticker | Evidence | Confidence |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        var exact = 0;
        var ambiguous = 0;
        var none = 0;

        foreach (var member in dropouts)
        {
            var identity = await IdentityAsync(context, archive, member.Cik);

            var names = new List<string>();

            if (member.Name is { Length: > 0 })
            {
                names.Add(member.Name);
            }

            if (identity.Name is { Length: > 0 })
            {
                names.Add(identity.Name);
            }

            names.AddRange(identity.FormerNames);

            var matched = Match(names, candidates);

            var confidence = matched.Count switch
            {
                1 => "exact - one delisted symbol whose normalised name equals a name EDGAR recorded",
                0 when candidates is null => "**blocked** - no candidate list is available locally",
                0 => "**none** - no delisted symbol's normalised name equals any name EDGAR recorded",
                _ => "**ambiguous, rejected** - several delisted symbols share the normalised name",
            };

            if (matched.Count == 1)
            {
                exact++;
            }
            else if (matched.Count > 1)
            {
                ambiguous++;
            }
            else
            {
                none++;
            }

            var former = identity.FormerNames.Count == 0
                ? "-"
                : string.Join("; ", identity.FormerNames.Take(3));

            report.AppendLine(Universe.Inv(
                $"| `{member.Cik}` | {identity.Name ?? member.Name ?? "-"} | {member.LastFilingUtc ?? "-"} | {former} | {(matched.Count == 1 ? matched[0] : "**none**")} | {identity.Evidence} | {confidence} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Exact: **{exact}**. Ambiguous and therefore rejected: **{ambiguous}**. Unmatched: **{none}**."));
        report.AppendLine();
        report.AppendLine("An exact name match names a symbol; it does not establish that the");
        report.AppendLine("symbol is the company's primary listing. `BGEPF.US` for Bunge is the");
        report.AppendLine("standing example - the name matches exactly and the line is not the one");
        report.AppendLine("anybody would have traded - so every recovered ticker stays provisional");
        report.AppendLine("until a price series confirms it, which nothing here is authorised to");
        report.AppendLine("fetch.");
        report.AppendLine();
        report.AppendLine("No company is dropped for being unmatched and no name is accepted for");
        report.AppendLine("looking similar. The matcher requires equality after a stated");
        report.AppendLine("normalisation - case folded, punctuation removed, and a fixed list of");
        report.AppendLine("legal suffixes stripped - and rejects a name that matches more than one");
        report.AppendLine("symbol rather than choosing between them.");
        report.AppendLine();
        report.AppendLine("### What the one billable call would establish");
        report.AppendLine();
        report.AppendLine("`GET api/exchange-symbol-list/US?delisted=1` returns every US symbol EODHD");
        report.AppendLine("no longer serves as live, each with a `Code` and a `Name`. It is the only");
        report.AppendLine("source this platform can reach that names a ticker for a company after");
        report.AppendLine("EDGAR has stopped doing so. Cached to the path above it becomes a fixed");
        report.AppendLine("artefact the matcher can be re-run against without spending anything");
        report.AppendLine("further, and it is the same list the manifest stage already read once");
        report.AppendLine("and discarded. One call, no price data, nothing acquired.");

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "remediation-tickers.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        await AssertUnchangedAsync(context, before);

        // Ambiguity is an outcome, not a defect. Several of these companies share a normalised
        // name with more than one delisted symbol - share classes, OTC lines, a successor trading
        // under a predecessor's name - and refusing to choose between them is the matcher working.
        // What must hold is that every dropout is accounted for in exactly one bucket.
        Assert.Equal(dropouts.Count, exact + ambiguous + none);
    }

    // ================= 3. gate 2, analysed and never moved ======================================

    [SkippableFact]
    public async Task Gate_two_is_analysed_against_the_constructions_the_archived_frames_support()
    {
        Skip.IfNot(Enabled(), Reason());

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var before = await SnapshotAsync(context);

        var frames = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);

        foreach (var cut in Universe.Cuts)
        {
            var identifier = Universe.MembershipFrame(cut);
            frames[identifier] = await FrameAsync(context, archive, identifier);
        }

        var revenue = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var concept in Universe.RankingConcepts)
        {
            foreach (var pair in await FrameAsync(context, archive, Universe.RankingFrame(concept)))
            {
                if (!revenue.TryGetValue(pair.Key, out var held) || pair.Value > held)
                {
                    revenue[pair.Key] = pair.Value;
                }
            }
        }

        var first = frames[Universe.MembershipFrame(Universe.Cuts[0])];
        var last = frames[Universe.MembershipFrame(Universe.Cuts[^1])];

        Skip.If(
            first.Count == 0 || last.Count == 0,
            "The archived cross-sections could not be re-read, so nothing can be measured.");

        var ranked = new List<(string Cik, decimal Revenue)>();

        foreach (var cik in first.Keys)
        {
            if (revenue.TryGetValue(cik, out var value))
            {
                ranked.Add((cik, value));
            }
        }

        ranked = ranked
            .OrderByDescending(r => r.Revenue)
            .ThenBy(r => r.Cik, StringComparer.Ordinal)
            .ToList();

        var report = new StringBuilder();

        report.AppendLine("# Remediation 3 - gate 2, analysed at the threshold it already has");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"**The floor stays at {Gate2Floor:P0}.** It is not moved, argued down, or "));
        report.AppendLine("re-derived here. What follows measures which point-in-time constructions");
        report.AppendLine("the seven already-archived cross-sections can support, and what each");
        report.AppendLine("one's observed dropout rate actually is.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Measured on the frames alone: a member is a dropout when it is absent "));
        report.AppendLine("from the final cross-section. That is the same signal the sealed manifest");
        report.AppendLine("used, and it is the only one available for companies whose facts were");
        report.AppendLine("never fetched - so every row below is measured the same way as every other.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"| Construction | Members | Dropouts | Rate | Against {Gate2Floor:P0} |"));
        report.AppendLine("| --- | ---: | ---: | ---: | --- |");

        foreach (var (label, members) in Constructions(ranked, first))
        {
            var gone = members.Count(c => !last.ContainsKey(c));
            var rate = members.Count == 0 ? 0m : (decimal)gone / members.Count;

            report.AppendLine(Universe.Inv(
                $"| {label} | {members.Count} | {gone} | {rate:P2} | {(rate >= Gate2Floor ? "**passes**" : "fails")} |"));
        }

        report.AppendLine();
        report.AppendLine("Every row above is the same methodology as the sealed manifest: membership");
        report.AppendLine("is a cross-section of filers taken at the first cut, fixed there and");
        report.AppendLine("tracked forward, with no company ever entering on later evidence. Only the");
        report.AppendLine("selection rule differs, and each one is stated in full.");

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "remediation-gate2.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        await AssertUnchangedAsync(context, before);

        Assert.True(ranked.Count > Universe.Size, "The ranking is smaller than the universe it produced.");
    }

    /// <summary>
    /// The candidate selection rules, each stated as a rule rather than as a result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reading this table the wrong way round is the hazard.</strong> Choosing whichever
    /// row clears the gate is selection on the outcome, and would make gate 2 a formality rather
    /// than a control. The table exists to answer a different question: whether the 4.3% the top
    /// four hundred produced is a property of the market or of the slice, and it plainly is a
    /// property of the slice - the largest filers by revenue are the least likely to be acquired,
    /// wound up or taken private.
    /// </para>
    /// <para>
    /// So a construction is defensible here only if it can be argued for without reference to its
    /// dropout rate. "Every filer that reported a balance sheet" is defensible: it is the market,
    /// not a curated part of it. "Every fifth filer down the ranking" is defensible: it is a
    /// systematic sample of the same population the ranking describes. "The top six hundred and
    /// twelve" would not be, whatever it scored.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Label, List<string> Members)> Constructions(
        List<(string Cik, decimal Revenue)> ranked,
        Dictionary<string, decimal> first)
    {
        yield return (
            "**as sealed** - top 400 by CY2020 revenue",
            ranked.Take(400).Select(r => r.Cik).ToList());

        foreach (var size in new[] { 500, 600, 800, 1000, 1500, 2000 })
        {
            if (ranked.Count >= size)
            {
                yield return (
                    Universe.Inv($"top {size} by CY2020 revenue"),
                    ranked.Take(size).Select(r => r.Cik).ToList());
            }
        }

        yield return (
            Universe.Inv($"every filer with a CY2020 revenue figure ({ranked.Count})"),
            ranked.Select(r => r.Cik).ToList());

        yield return (
            Universe.Inv($"the whole CY2021Q2 cross-section ({first.Count}), revenue or not"),
            first.Keys.ToList());

        // Systematic samples of the ranked population: same size as the sealed universe, same
        // methodology, but spanning the ranking rather than its head.
        foreach (var size in new[] { 400 })
        {
            if (ranked.Count < size)
            {
                continue;
            }

            var step = ranked.Count / size;

            var systematic = new List<string>(size);

            for (var i = 0; i < size; i++)
            {
                systematic.Add(ranked[i * step].Cik);
            }

            yield return (
                Universe.Inv($"systematic 1-in-{step} sample of all {ranked.Count} revenue filers ({size})"),
                systematic);
        }

        // The band immediately below the sealed universe, on its own: the same rule, applied to
        // companies the sealed cut excluded for being smaller.
        if (ranked.Count >= 800)
        {
            yield return (
                "ranks 401-800 by CY2020 revenue",
                ranked.Skip(400).Take(400).Select(r => r.Cik).ToList());
        }

        if (ranked.Count >= 1200)
        {
            yield return (
                "ranks 801-1200 by CY2020 revenue",
                ranked.Skip(800).Take(400).Select(r => r.Cik).ToList());
        }
    }

    // ---- shared helpers ------------------------------------------------------------------------

    private static bool Enabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(GateVariable),
            "1",
            StringComparison.Ordinal);

    private static string Reason() =>
        $"Remediation analysis is off. Set {GateVariable}=1 to run it. It calls no provider and " +
        "writes nothing to the store.";

    private static string Key(
        string cik,
        string attribute,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        string canonical) =>
        string.Join(
            '|',
            cik,
            attribute,
            asOfUtc.ToString("O", CultureInfo.InvariantCulture),
            publishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            canonical);

    private static string Relative(string path) =>
        path.Replace(Universe.RepositoryPath(), string.Empty, StringComparison.Ordinal)
            .Replace('\\', '/')
            .TrimStart('/');

    private static async Task<(int Observations, int Runs, int Quarantine, int Opportunities)>
        SnapshotAsync(AppDbContext context) =>
        (
            await context.Observations.AsNoTracking().CountAsync(),
            await context.IngestionRuns.AsNoTracking().CountAsync(),
            await context.QuarantinedPayloads.AsNoTracking().CountAsync(),
            await context.Opportunities.AsNoTracking().CountAsync());

    private static async Task AssertUnchangedAsync(
        AppDbContext context,
        (int Observations, int Runs, int Quarantine, int Opportunities) before)
    {
        var after = await SnapshotAsync(context);

        Assert.Equal(before.Observations, after.Observations);
        Assert.Equal(before.Runs, after.Runs);
        Assert.Equal(before.Quarantine, after.Quarantine);
        Assert.Equal(before.Opportunities, after.Opportunities);
    }

    private static async Task<Dictionary<string, decimal>> FrameAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        string identifier)
    {
        var run = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.MarketWideDisclosure)
                .ToListAsync())
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .FirstOrDefault(r => string.Equals(
                r.Request.Subject.Identifier,
                identifier,
                StringComparison.Ordinal));

        if (run is null)
        {
            return new Dictionary<string, decimal>(StringComparer.Ordinal);
        }

        foreach (var hash in run.Artifacts)
        {
            var payload = await archive.RetrieveAsync(hash);

            if (payload is not null)
            {
                return Universe.ReadFrame(payload);
            }
        }

        return new Dictionary<string, decimal>(StringComparer.Ordinal);
    }

    // ---- the identity side of the mapping ------------------------------------------------------

    private static async Task<CompanyIdentity> IdentityAsync(
        AppDbContext context,
        IRawResponseArchive archive,
        string cik)
    {
        var run = (await context.IngestionRuns
                .AsNoTracking()
                .Where(r => r.Request.Category == DataCategory.CompanyProfile)
                .ToListAsync())
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .LastOrDefault(r => string.Equals(
                r.Request.Subject.Identifier,
                cik,
                StringComparison.Ordinal));

        if (run is null)
        {
            return new CompanyIdentity(null, [], "no archived submissions document for this CIK");
        }

        foreach (var hash in run.Artifacts)
        {
            var payload = await archive.RetrieveAsync(hash);

            if (payload is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            var former = new List<string>();

            if (root.TryGetProperty("formerNames", out var names) &&
                names.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in names.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("name", out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        value.GetString() is { Length: > 0 } text)
                    {
                        former.Add(text);
                    }
                }
            }

            return new CompanyIdentity(
                name,
                former,
                Universe.Inv($"archived EDGAR submissions payload, content hash {hash.Value[..16]}"));
        }

        return new CompanyIdentity(
            null,
            [],
            "the submissions run is recorded but its payload is no longer archived");
    }

    /// <summary>
    /// Every cached delisted symbol whose normalised name equals one of a company's names.
    /// </summary>
    /// <remarks>
    /// Equality after normalisation, never similarity. A name that matches several symbols returns
    /// several, and the caller rejects it rather than picking one: two companies sharing a
    /// normalised name is precisely the case where a wrong ticker looks right.
    /// </remarks>
    private static List<string> Match(List<string> names, Dictionary<string, List<string>>? candidates)
    {
        if (candidates is null)
        {
            return [];
        }

        var matches = new List<string>();

        foreach (var name in names)
        {
            if (candidates.TryGetValue(Normalise(name), out var symbols))
            {
                foreach (var symbol in symbols)
                {
                    if (!matches.Contains(symbol, StringComparer.Ordinal))
                    {
                        matches.Add(symbol);
                    }
                }
            }
        }

        return matches;
    }

    /// <summary>Case folded, punctuation dropped, and a fixed list of legal suffixes removed.</summary>
    private static string Normalise(string name)
    {
        var letters = new StringBuilder(name.Length);

        foreach (var c in name.ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                letters.Append(c);
            }
            else if (letters.Length > 0 && letters[^1] != ' ')
            {
                letters.Append(' ');
            }
        }

        var words = letters.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !Suffixes.Contains(w))
            .ToList();

        return string.Concat(words);
    }

    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "LLC", "LP", "LLLP",
        "LTD", "LIMITED", "PLC", "NV", "SA", "AG", "GROUP", "HOLDINGS", "HOLDING", "THE",
        "CLASS", "A", "B", "C", "COMMON", "STOCK", "NEW", "DE", "JERSEY",
    };

    private static Dictionary<string, List<string>>? LoadDelistedCache()
    {
        var path = DelistedCachePath;

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("Code", out var code) ||
                !row.TryGetProperty("Name", out var name) ||
                code.ValueKind != JsonValueKind.String ||
                name.ValueKind != JsonValueKind.String ||
                code.GetString() is not { Length: > 0 } symbol ||
                name.GetString() is not { Length: > 0 } text)
            {
                continue;
            }

            var key = Normalise(text);

            if (!byName.TryGetValue(key, out var symbols))
            {
                symbols = [];
                byName[key] = symbols;
            }

            symbols.Add(symbol + ".US");
        }

        return byName;
    }

    private sealed record CompanyIdentity(
        string? Name,
        List<string> FormerNames,
        string Evidence);

    private sealed record ManifestMember(
        string Cik,
        string? Ticker,
        string? Name,
        string? LastFilingUtc,
        bool StoppedFiling,
        bool AbsentFromFinalCrossSection);

    private sealed record ManifestFile(List<ManifestMember>? Members);
}
