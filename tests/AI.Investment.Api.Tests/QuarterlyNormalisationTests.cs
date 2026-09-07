using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.Observations;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Re-reads the archived EDGAR payloads with the quarterly filter open, and writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called and no row is written.</strong> The payloads are already on disk;
/// this reads them through the archive, runs the normaliser in memory, and compares the result
/// against what is stored. Observation, opportunity and ingestion-run counts are asserted unchanged.
/// </para>
/// <para>
/// <strong>Why it does not persist, which is a deliberate departure worth stating.</strong>
/// <c>IObservationStore.RecordAsync</c> is a plain insert with no de-duplication - by design, since
/// a refetched session is a second publication rather than a correction. Re-normalising every
/// archived EDGAR run through the pipeline would therefore write a second, identical copy of all
/// 21,349 annual observations alongside the new quarterly ones. Nothing would be wrong with the
/// values, but the store would carry two of everything, and the gate that says the frozen twenty's
/// stored observations are unchanged in count would be the thing that noticed.
/// </para>
/// <para>
/// So this stage answers the question the de-risking was for - do quarterly facts exist, do they
/// arrive often enough to be worth having, and does opening the filter disturb the annual figures -
/// and leaves the writing to the universe acquisition, which normalises everything once against a
/// store that does not already hold it.
/// </para>
/// <para>
/// <strong>The strongest check here is not a count.</strong> Every annual observation the new
/// normaliser produces is matched against the stored one by subject, attribute, period, publication
/// and value. Identical output, not merely an identical number of rows.
/// </para>
/// <para>
/// Gated on <c>AIINV_QUARTERLY=1</c>.
/// </para>
/// </remarks>
public sealed class QuarterlyNormalisationTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_QUARTERLY";

    /// <summary>
    /// Gate 4, corrected: distinct quarterly periods per company per <em>complete</em> calendar year.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The original 3.5 was unachievable, and not by a little.</strong> A US issuer files a
    /// 10-Q for the first three fiscal quarters and no 10-Q at all for the fourth: Q4 is reported
    /// inside the 10-K, as part of the annual figure, and is never separately tagged as a
    /// quarter-length duration. So three quarterly duration facts a year is the structural ceiling,
    /// and a threshold of 3.5 could not have been met by any filer complying with the rules.
    /// </para>
    /// <para>
    /// That distinction matters and is the reason this constant was allowed to move at all. It is a
    /// correction to a factual error about how companies file - the same measurement would have
    /// failed on perfect data - and not a bar lowered because a result missed it. The reasoning is
    /// pinned by <see cref="QuarterlyCadenceTests"/> so that raising it back above the ceiling
    /// fails a test rather than a run.
    /// </para>
    /// <para>
    /// 2.9 rather than 3.0 because a fiscal year offset from the calendar puts a company's four
    /// quarter-ends into calendar years unevenly, and a company that changes its fiscal year end
    /// loses one. The margin absorbs the calendar, not a shortfall in the data.
    /// </para>
    /// </remarks>
    internal const decimal MinimumQuarterlyPeriodsPerCompletedYear = 2.9m;

    /// <summary>
    /// The ceiling the corrected threshold has to sit under, and the reason it does.
    /// </summary>
    /// <remarks>
    /// Three 10-Qs a year. Any gate above this is unsatisfiable by construction rather than
    /// demanding, which is the failure being corrected.
    /// </remarks>
    internal const decimal QuarterlyFilingsPerYearCeiling = 3.0m;

    /// <summary>The threshold this replaced. Kept named so the regression test can say what it was.</summary>
    internal const decimal RejectedImpossibleThreshold = 3.5m;

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public QuarterlyNormalisationTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Archived_payloads_yield_quarterly_figures_without_disturbing_the_annual_ones()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Quarterly normalisation is off. Set {GateVariable}=1 to run it. It calls no provider " +
            "and writes nothing.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();

        var runs = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.FinancialStatements)
            .ToListAsync();

        Skip.If(runs.Count == 0, "No financial-statement runs are stored. Nothing to re-read.");

        var normalizer = new SecEdgarCompanyFactsNormalizer();
        var produced = new List<Observation>();
        var payloads = 0;
        var missing = 0;
        var duplicates = 0;

        // One payload, one normalisation - however many runs reach it.
        //
        // The archive stores a document once and records a retrieval per run, so the same
        // companyfacts bytes are reachable from every run that fetched them. Normalising run by run
        // therefore produced each fact once per run: 184 runs resolved to 55 reads and emitted
        // exactly twice the stored figures. Nothing was wrong with the values, and that is what
        // makes it dangerous - the acquisition would have written 400 companies' fundamentals twice
        // and the duplication would only have shown up as a row count nobody could explain.
        //
        // Keyed on subject as well as hash: two subjects sharing bytes is impossible for
        // companyfacts, and relying on that would be relying on it.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var run in runs)
        {
            if (run.Request.SourceId != SecEdgarProvider.Id)
            {
                continue;
            }

            foreach (var hash in run.Artifacts)
            {
                if (!seen.Add(run.Request.Subject.Identifier + "|" + hash.Value))
                {
                    duplicates++;

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
                produced.AddRange(result.Observations);
            }
        }

        var annual = produced
            .Where(o => !o.Attribute.StartsWith(FinancialFigures.QuarterlyPrefix, StringComparison.Ordinal))
            .ToList();

        var quarterly = produced
            .Where(o => o.Attribute.StartsWith(FinancialFigures.QuarterlyPrefix, StringComparison.Ordinal))
            .ToList();

        var stored = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == "Company")
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
            .Select(o => new
            {
                Company = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .ToListAsync();

        var storedKeys = stored
            .Select(o => Key(o.Company, o.Attribute, o.AsOfUtc, o.PublishedAtUtc, o.Canonical))
            .ToHashSet(StringComparer.Ordinal);

        var unmatched = annual
            .Select(Key)
            .Where(key => !storedKeys.Contains(key))
            .ToList();

        var report = Compose(
            runs.Count, payloads, duplicates, missing, annual, quarterly, stored.Count, unmatched,
            watch.Elapsed);

        await WriteAsync(report);
        _output.WriteLine(report);

        // ---- nothing was written ------------------------------------------------------------

        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(opportunitiesBefore, await context.Opportunities.AsNoTracking().CountAsync());

        // ---- the annual figures are untouched, row for row ----------------------------------

        Assert.True(
            unmatched.Count == 0,
            Inv($"{unmatched.Count} annual observations produced by the new normaliser do not ") +
            "match anything stored. Opening the quarterly filter was supposed to leave the annual " +
            "attributes byte-identical. First few: " +
            string.Join(" | ", unmatched.Take(5)));

        // ---- and the quarterly figures are actually there -----------------------------------

        Assert.True(
            quarterly.Count > 0,
            "No quarterly observations were produced. Either the payloads carry none, or the " +
            "filter is still closed.");

        var rate = PerCompanyPerYear(quarterly);

        Assert.True(
            rate >= MinimumQuarterlyPeriodsPerCompletedYear,
            Inv($"Quarterly figures arrive {rate:F2} times per company per completed calendar year, ") +
            Inv($"against a gate of {MinimumQuarterlyPeriodsPerCompletedYear:F1}. A rate near one ") +
            "means the annual filter is still in force and these are not quarters.");

        Assert.True(
            duplicates > 0,
            "No duplicate payload references were seen, which contradicts the ledger: 184 runs " +
            "resolve to far fewer distinct payloads. If this ever stops being true the de-duplication " +
            "has become dead code and the acquisition's protection against double-writing is gone.");
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// How often a quarterly figure lands, per company per year of covered periods.
    /// </summary>
    /// <remarks>
    /// Measured on distinct period ends rather than on rows, because a restatement republishes a
    /// period it does not add one - and counting republications would make a company that revised
    /// its accounts look like a company that reported more often.
    /// </remarks>
    private static decimal PerCompanyPerYear(List<Observation> quarterly)
    {
        var years = CompletedYears(quarterly);

        if (years.Count == 0)
        {
            return 0m;
        }

        // Aggregate, not an average of per-year averages: total periods over total company-years.
        // Averaging the yearly rates would weight a year in which one company reported as heavily
        // as a year in which seventeen did.
        var periods = years.Sum(y => y.Periods);
        var companyYears = years.Sum(y => y.Companies);

        return companyYears == 0 ? 0m : (decimal)periods / companyYears;
    }

    /// <summary>
    /// Quarterly net-income periods per calendar year, for complete years only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current year is excluded because it is partial by definition: at the time of writing two
    /// of its three quarters had been filed, which drags a rate measured over it toward two-thirds
    /// of the truth. Excluding it is not a choice about this data - it is the only way the measure
    /// means the same thing in January as in December.
    /// </para>
    /// <para>
    /// Distinct <c>(company, period)</c> pairs rather than rows: a restatement republishes a period
    /// rather than adding one, and counting republications would make a company that revised its
    /// accounts look like a company that reported more often.
    /// </para>
    /// </remarks>
    private static List<(int Year, int Companies, int Periods)> CompletedYears(
        List<Observation> quarterly)
    {
        var currentYear = DateTime.UtcNow.Year;

        return quarterly
            .Where(o => string.Equals(
                o.Attribute,
                FinancialFigures.QuarterlyNetIncome,
                StringComparison.Ordinal))
            .Select(o => (Company: o.Subject.Identifier ?? string.Empty, o.Provenance.AsOfUtc))
            .Distinct()
            .Where(p => p.AsOfUtc.Year < currentYear)
            .GroupBy(p => p.AsOfUtc.Year)
            .Select(g => (
                Year: g.Key,
                Companies: g.Select(p => p.Company).Distinct().Count(),
                Periods: g.Count()))
            .OrderBy(y => y.Year)
            .ToList();
    }

    private static string Key(Observation observation) => Key(
        observation.Subject.Identifier ?? string.Empty,
        observation.Attribute,
        observation.Provenance.AsOfUtc,
        observation.Provenance.PublishedAtUtc,
        observation.Value.Canonical);

    private static string Key(
        string company,
        string attribute,
        DateTime asOf,
        DateTime published,
        string? canonical) =>
        Inv($"{company}|{attribute}|{asOf:yyyy-MM-dd}|{published:yyyy-MM-dd}|{canonical}");

    private static string Compose(
        int runs,
        int payloads,
        int duplicates,
        int missing,
        List<Observation> annual,
        List<Observation> quarterly,
        int stored,
        List<string> unmatched,
        TimeSpan elapsed)
    {
        var report = new StringBuilder();

        report.AppendLine("# Quarterly normalisation — read from the archive, written nowhere");
        report.AppendLine();
        report.AppendLine(Inv($"Completed in {elapsed.TotalSeconds:F1}s. **No provider call. No row written.**"));
        report.AppendLine("The payloads were already on disk; this re-read them with the quarterly filter open.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Inv($"| Financial-statement runs found | {runs} |"));
        report.AppendLine(Inv($"| Archived payloads re-read | {payloads} |"));
        report.AppendLine(Inv($"| **Duplicate references skipped** | **{duplicates}** |"));
        report.AppendLine(Inv($"| Payloads no longer in the archive | {missing} |"));
        report.AppendLine(Inv($"| Annual observations produced | {annual.Count:N0} |"));
        report.AppendLine(Inv($"| **Quarterly observations produced** | **{quarterly.Count:N0}** |"));
        report.AppendLine(Inv($"| Fundamental observations stored today | {stored:N0} |"));
        report.AppendLine(Inv($"| Annual rows not matching a stored row | {unmatched.Count} |"));
        report.AppendLine(Inv(
            $"| **Quarterly periods per company per completed year** | **{PerCompanyPerYear(quarterly):F2}** (gate {MinimumQuarterlyPeriodsPerCompletedYear:F1}) |"));
        report.AppendLine();

        report.AppendLine("## Quarterly figures by attribute");
        report.AppendLine();
        report.AppendLine("| Attribute | Observations | Companies | Distinct periods |");
        report.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var group in quarterly
            .GroupBy(o => o.Attribute, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            var companies = group.Select(o => o.Subject.Identifier).Distinct().Count();
            var periods = group.Select(o => o.Provenance.AsOfUtc).Distinct().Count();

            report.AppendLine(Inv($"| `{group.Key}` | {group.Count():N0} | {companies} | {periods} |"));
        }

        report.AppendLine();
        report.AppendLine("## Quarterly net-income periods per calendar year");
        report.AppendLine();
        report.AppendLine("Distinct periods, not rows: a restatement republishes a period rather than");
        report.AppendLine("adding one. The per-company rate is what gate 4 was written against.");
        report.AppendLine();
        report.AppendLine(Inv(
            $"Complete calendar years only; {DateTime.UtcNow.Year} is excluded as partial. Three is the"));
        report.AppendLine("structural ceiling: a US issuer files a 10-Q for Q1, Q2 and Q3 and none for Q4,");
        report.AppendLine("which is reported inside the 10-K. Years above three are fiscal calendars offset");
        report.AppendLine("from the calendar year, which puts four quarter-ends into one of them.");
        report.AppendLine();
        report.AppendLine("| Year | Companies reporting | Distinct periods | Per company |");
        report.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var year in CompletedYears(quarterly))
        {
            report.AppendLine(Inv(
                $"| {year.Year} | {year.Companies} | {year.Periods} | {(decimal)year.Periods / year.Companies:F2} |"));
        }

        report.AppendLine();
        report.AppendLine("## What this does and does not establish");
        report.AppendLine();
        report.AppendLine("Establishes: the quarterly facts are present in payloads already held, they");
        report.AppendLine("arrive at a genuinely quarterly cadence, and opening the filter leaves every");
        report.AppendLine("annual observation identical in subject, attribute, period, publication and value.");
        report.AppendLine();
        report.AppendLine("Does not establish: anything about a wider universe, and nothing is persisted.");
        report.AppendLine("The store is unchanged; the acquisition normalises once, against a store that");
        report.AppendLine("does not already hold these rows.");

        return report.ToString();
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "quarterly.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report);
    }
}
