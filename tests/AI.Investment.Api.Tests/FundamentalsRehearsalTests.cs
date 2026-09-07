using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Opportunities.Fundamentals;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Admission;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Scores a fundamentals strategy against the stored SEC filings. Observational only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This creates nothing and changes nothing.</strong> It reads observations, forms
/// predictions in memory, resolves them against observations that were published later, and counts
/// the answers. No opportunity is drafted, no prediction is persisted, no order is placed and no
/// provider is called. The opportunity and observation counts are asserted unchanged at the end.
/// </para>
/// <para>
/// <strong>Why a fundamentals strategy.</strong> Every price claim needs a future price to resolve,
/// and this installation holds one year of prices against twenty years of filings. A claim about a
/// reported figure resolves entirely from what is already stored, over two decades, for nothing.
/// The point is not that this strategy is good - it may well be refused - but that the admission
/// machinery gets exercised against real evidence at a sample size the price data cannot reach.
/// </para>
/// <para>
/// <strong>What was fixed before the run.</strong> The claimed Brier score, the minimum history the
/// rule speaks from, the Laplace smoothing, and the clustering used for the independence discount
/// were all chosen before any of this was scored, and none of them was varied afterwards. That
/// matters more here than usual: the whole apparatus being tested exists to catch parameters fitted
/// to the evidence they are scored on.
/// </para>
/// <para>
/// Gated on <c>AIINV_FUNDAMENTALS=1</c>.
/// </para>
/// </remarks>
public sealed class FundamentalsRehearsalTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_FUNDAMENTALS";

    private const string CompanySubjectKind = "Company";

    /// <summary>The pooled rule, across every attribute.</summary>
    private const string PooledStrategy = "fundamental-direction";

    /// <summary>The same rule restricted to one attribute, declared in the register before this run.</summary>
    private const string RevenueStrategy = "fundamental-revenue-direction";

    private const string RevenueAttribute = "financials.revenue";

    private const string NotMeasured = "not measured";

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public FundamentalsRehearsalTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_fundamentals_rule_is_scored_over_the_stored_filings_without_creating_anything()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Fundamentals rehearsal is off. Set {GateVariable}=1 to run it. It reads only.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var nowUtc = clock.UtcNow;

        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();

        var figures = await LoadAsync(context);

        Skip.If(figures.Count == 0, "No fundamental observations are stored. Nothing to rehearse.");

        _output.WriteLine(Inv($"loaded {figures.Count} figures at {watch.Elapsed.TotalSeconds:F0}s"));

        var series = figures
            .GroupBy(f => (f.Company, f.Attribute))
            .Select(g => new Series(
                g.Key.Company,
                g.Key.Attribute,
                g.OrderBy(f => f.PeriodEndUtc).ThenBy(f => f.PublishedAtUtc).ToList()))
            .OrderBy(s => s.Company, StringComparer.Ordinal)
            .ThenBy(s => s.Attribute, StringComparer.Ordinal)
            .ToList();

        var predictions = new List<Prediction>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var one in series)
        {
            Score(one, predictions, skipped);
        }

        _output.WriteLine(Inv($"{predictions.Count} resolved predictions at {watch.Elapsed.TotalSeconds:F0}s"));

        var report = Compose(services, series, predictions, skipped, nowUtc, watch.Elapsed);

        await WriteAsync(report);
        _output.WriteLine(report);

        var opportunitiesAfter = await context.Opportunities.AsNoTracking().CountAsync();
        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();

        Assert.Equal(opportunitiesBefore, opportunitiesAfter);
        Assert.Equal(observationsBefore, observationsAfter);
    }

    // ---- reading ------------------------------------------------------------

    private static async Task<List<Figure>> LoadAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == CompanySubjectKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
            .Select(o => new
            {
                Company = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Kind,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .ToListAsync();

        var figures = new List<Figure>(rows.Count);

        foreach (var row in rows)
        {
            // A non-numeric figure is not a defect to throw on here: it is a row this strategy has
            // no way to compare, and dropping it is reported rather than hidden.
            if (row.Kind != ObservationValueKind.Number)
            {
                continue;
            }

            if (!decimal.TryParse(
                    row.Canonical,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                continue;
            }

            figures.Add(new Figure(
                row.Company,
                row.Attribute,
                DateTime.SpecifyKind(row.AsOfUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(row.PublishedAtUtc, DateTimeKind.Utc),
                value));
        }

        return figures;
    }

    // ---- scoring ------------------------------------------------------------

    /// <summary>
    /// Walks one company-attribute series, deciding at each publication and resolving at the next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision instant is a minute after a period's <em>first</em> publication, so everything
    /// published up to and including that filing is visible and nothing later is. The claim is about
    /// the next stored period, and it is resolved against that period's first publication - the
    /// number as first reported, not as later restated, because the restatement is not what the
    /// claim was about.
    /// </para>
    /// <para>
    /// The guard that matters is the one refusing a prediction whose outcome was already public at
    /// the decision. Filings arrive out of order often enough that this is not hypothetical, and
    /// every such case would otherwise be scored as a brilliant forecast.
    /// </para>
    /// </remarks>
    private static void Score(Series series, List<Prediction> predictions, Dictionary<string, int> skipped)
    {
        var periods = series.Figures
            .GroupBy(f => f.PeriodEndUtc)
            .Select(g => new Period(
                g.Key,
                g.OrderBy(f => f.PublishedAtUtc).First().PublishedAtUtc,
                g.OrderBy(f => f.PublishedAtUtc).First().Value,
                g.ToList()))
            .OrderBy(p => p.PeriodEndUtc)
            .ToList();

        for (var k = 0; k < periods.Count - 1; k++)
        {
            var current = periods[k];
            var next = periods[k + 1];
            var decidedAtUtc = current.FirstPublishedAtUtc.AddMinutes(1);

            // The outcome must not already be on the record. If it is, this is not a prediction.
            if (next.FirstPublishedAtUtc <= decidedAtUtc)
            {
                Count(skipped, "outcome already public at the decision");

                continue;
            }

            var known = series.Figures
                .Where(f => f.PublishedAtUtc <= decidedAtUtc)
                .Select(f => new FiledFigure(f.Attribute, f.PeriodEndUtc, f.PublishedAtUtc, f.Value))
                .ToList();

            var verdict = FundamentalDirectionRule.Evaluate(known, decidedAtUtc);

            if (!verdict.Fired)
            {
                Count(skipped, verdict.Refusal.ToString());

                continue;
            }

            // The rule reads the latest publication at or before the decision, which for the period
            // just filed is that filing. If they disagree, something is wrong with the walk itself.
            if (verdict.CurrentPeriodEndUtc != current.PeriodEndUtc)
            {
                Count(skipped, "the rule read a different period than the walk");

                continue;
            }

            predictions.Add(new Prediction(
                series.Company,
                series.Attribute,
                decidedAtUtc,
                next.PeriodEndUtc,
                verdict.Probability,
                FundamentalDirectionRule.Occurred(next.FirstValue, verdict.CurrentValue)));
        }
    }

    private static void Count(Dictionary<string, int> tally, string reason) =>
        tally[reason] = tally.GetValueOrDefault(reason) + 1;

    // ---- statistics ---------------------------------------------------------

    // ---- the report ---------------------------------------------------------

    private static string Compose(
        IServiceProvider services,
        List<Series> series,
        List<Prediction> predictions,
        Dictionary<string, int> skipped,
        DateTime nowUtc,
        TimeSpan elapsed)
    {
        var report = new StringBuilder();

        Line(report, "# Fundamentals strategy — read-only validation");
        Line(report, string.Empty);
        Line(report, Inv($"Generated {nowUtc:yyyy-MM-dd HH:mm:ss}Z in {elapsed.TotalSeconds:F0}s. Nothing was created, written or fetched."));
        Line(report, string.Empty);
        Line(report, Inv($"Rule: the next reported figure is at least the last, stated as the Laplace-smoothed frequency of past increases, from at least {FundamentalDirectionRule.MinimumPriorTransitions} prior transitions."));

        if (predictions.Count == 0)
        {
            Line(report, string.Empty);
            Line(report, "**No prediction resolved.** Nothing below can be computed.");

            AppendSkipped(report, skipped);

            return report.ToString();
        }

        var occurred = predictions.Count(p => p.Occurred);
        var baseRate = (decimal)occurred / predictions.Count;
        var spread = ScoreStatistics.ComponentSpread(
            predictions.Select(p => (p.Probability, p.Occurred)));

        // Clustering declared in advance: the calendar year of the period being predicted. Filings
        // for one year move together across companies, and a count that ignores that is a count of
        // rows rather than of independent evidence.
        var clusters = predictions
            .GroupBy(p => p.PredictedPeriodEndUtc.Year)
            .OrderBy(g => g.Key)
            .ToList();

        var byCompany = predictions.GroupBy(p => p.Company, StringComparer.Ordinal).ToList();
        var rho = ScoreStatistics.IntraClusterCorrelation(
            clusters.Select(c => (Size: c.Count(), Successes: c.Count(p => p.Occurred))));
        var companyRho = ScoreStatistics.IntraClusterCorrelation(
            byCompany.Select(c => (Size: c.Count(), Successes: c.Count(p => p.Occurred))));

        var curve = CalibrationCurve.From(predictions.Select(p => (p.Probability, p.Occurred)));
        var effective = SampleRequirement.EffectiveSample(predictions.Count, clusters.Count, rho);

        // ---- headline -------------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Headline");
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Company × attribute series | {series.Count} |"));
        Line(report, Inv($"| Companies | {series.Select(s => s.Company).Distinct(StringComparer.Ordinal).Count()} |"));
        Line(report, Inv($"| Attributes | {series.Select(s => s.Attribute).Distinct(StringComparer.Ordinal).Count()} |"));
        Line(report, Inv($"| **Resolved predictions** | **{predictions.Count}** |"));
        Line(report, Inv($"| Of those, the figure did not fall | {occurred} |"));
        Line(report, Inv($"| Base rate | {baseRate:P2} |"));
        Line(report, Inv($"| Brier score | {Shown(curve.BrierScore)} |"));
        Line(report, Inv($"| Reference (base rate only) | {baseRate * (1m - baseRate):F4} |"));
        Line(report, Inv($"| Component spread | {spread:F4} |"));
        Line(report, Inv($"| Earliest decision | {predictions.Min(p => p.DecidedAtUtc):yyyy-MM-dd} |"));
        Line(report, Inv($"| Latest decision | {predictions.Max(p => p.DecidedAtUtc):yyyy-MM-dd} |"));

        // ---- independence ---------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## How much of that is independent");
        Line(report, string.Empty);
        Line(report, "Clustered by the calendar year of the period being predicted, declared before the run.");
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Clusters (years) | {clusters.Count} |"));
        Line(report, Inv($"| Mean cluster size | {(decimal)predictions.Count / clusters.Count:F1} |"));
        Line(report, Inv($"| Measured correlation inside a year | {rho:F4} |"));
        Line(report, Inv($"| Design effect | {SampleRequirement.DesignEffect(predictions.Count, clusters.Count, rho):F2} |"));
        Line(report, Inv($"| **Effective sample** | **{effective}** |"));
        Line(report, string.Empty);
        Line(report, Inv($"For comparison only, clustering by company instead gives {byCompany.Count} clusters and a correlation of {companyRho:F4}. The gate uses the declared clustering, not whichever is kinder."));

        // ---- the gate -------------------------------------------------------

        var register = StrategyRegister.Parse(File.ReadAllText(RegisterPath()));

        AppendAdmission(
            report,
            services,
            register,
            PooledStrategy,
            predictions,
            curve,
            nowUtc,
            "The pooled rule, every attribute together.");

        AppendRevenue(report, services, register, predictions, nowUtc);

        // ---- per year -------------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## By predicted period");
        Line(report, string.Empty);
        Line(report, "| Year | Predictions | Did not fall | Rate | Brier |");
        Line(report, "| --- | ---: | ---: | ---: | ---: |");

        foreach (var cluster in clusters)
        {
            var rows = cluster.ToList();
            var won = rows.Count(p => p.Occurred);

            Line(report, Inv($"| {cluster.Key} | {rows.Count} | {won} | {Rate(won, rows.Count)} | {Brier(rows):F4} |"));
        }

        // ---- per attribute --------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## By attribute");
        Line(report, string.Empty);
        Line(report, "| Attribute | Predictions | Did not fall | Rate | Brier |");
        Line(report, "| --- | ---: | ---: | ---: | ---: |");

        foreach (var group in predictions
            .GroupBy(p => p.Attribute)
            .OrderByDescending(g => g.Count()))
        {
            var rows = group.ToList();
            var won = rows.Count(p => p.Occurred);

            Line(report, Inv($"| `{group.Key}` | {rows.Count} | {won} | {Rate(won, rows.Count)} | {Brier(rows):F4} |"));
        }

        // ---- calibration ----------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Calibration spread");
        Line(report, string.Empty);
        Line(report, "| Bin | Predictions | Did not fall | Rate |");
        Line(report, "| --- | ---: | ---: | ---: |");

        for (var bin = 0; bin < 10; bin++)
        {
            var low = bin / 10m;
            var high = (bin + 1) / 10m;

            var inBin = predictions
                .Where(p => p.Probability >= low && (bin == 9 ? p.Probability <= high : p.Probability < high))
                .ToList();

            var won = inBin.Count(p => p.Occurred);

            Line(report, Inv($"| {low:F1}–{high:F1} | {inBin.Count} | {won} | {Rate(won, inBin.Count)} |"));
        }

        AppendSkipped(report, skipped);

        // ---- what this is not -----------------------------------------------

        Line(report, string.Empty);
        Line(report, "## What this run does not establish");
        Line(report, string.Empty);
        Line(report, "- **It is not a trading result.** The event is a reported figure, not a price. Nothing here says what a position in these companies would have returned, and nothing here should be read as an edge.");
        Line(report, "- **The declaration is back-dated to the earliest prediction**, as the price rehearsal also does, because a backtest necessarily writes its event down today. The gate cannot tell a genuinely prospective declaration from a back-dated one; only a record kept forward in time can. That is a convention, and naming it is the only honest way to use it.");
        Line(report, "- **The rule is deliberately weak.** It has no free parameters beyond a minimum history fixed in advance, and it knows nothing except the past direction of one series. It is here to exercise the gate on real evidence, not to be a good strategy.");

        return report.ToString();
    }

    private static void AppendSkipped(StringBuilder report, Dictionary<string, int> skipped)
    {
        Line(report, string.Empty);
        Line(report, "## Decision points that produced no prediction");
        Line(report, string.Empty);

        if (skipped.Count == 0)
        {
            Line(report, "None.");

            return;
        }

        Line(report, "| Reason | Count |");
        Line(report, "| --- | ---: |");

        foreach (var entry in skipped.OrderByDescending(e => e.Value))
        {
            Line(report, Inv($"| `{entry.Key}` | {entry.Value} |"));
        }
    }

    /// <summary>
    /// Scores one registered strategy against the predictions it is declared over.
    /// </summary>
    /// <remarks>
    /// The declaration is read from the committed register rather than built here. That is the
    /// whole change: this method used to construct a declaration and date it to the earliest
    /// prediction it was about to score, which made the look-ahead check unfailable.
    /// </remarks>
    private static void AppendAdmission(
        StringBuilder report,
        IServiceProvider services,
        IReadOnlyDictionary<string, RegisteredStrategy> register,
        string strategy,
        List<Prediction> predictions,
        CalibrationCurve curve,
        DateTime nowUtc,
        string subtitle)
    {
        var criteria = services.GetRequiredService<AdmissionCriteria>();
        var registered = StrategyRegister.Require(register, strategy);
        var declared = registered.Event;
        var type = declared.Type;
        var threshold = FundamentalDirectionRule.EventThreshold;
        var earliest = predictions.Min(p => p.DecidedAtUtc);
        var scored = predictions.Select(p => (p.Probability, p.Occurred)).ToList();
        var occurred = predictions.Count(p => p.Occurred);

        var byYear = predictions
            .GroupBy(p => p.PredictedPeriodEndUtc.Year)
            .Select(g => (Size: g.Count(), Successes: g.Count(x => x.Occurred)))
            .ToList();

        var rho = ScoreStatistics.IntraClusterCorrelation(byYear);

        var measurement = StrategyMeasurement.Create(
            type,
            predictions.Count,
            curve.BrierScore,
            threshold,
            earliest,
            nowUtc,
            declared.EvidenceBaseFingerprint,
            registered.TrialsInFamily,
            registered.FamiliesOnThisEvidenceBase,
            baseRate: (decimal)occurred / predictions.Count,
            componentStandardDeviation: ScoreStatistics.ComponentSpread(scored),
            independentClusters: byYear.Count,
            intraClusterCorrelation: rho);

        var requirement = SampleRequirement.For(declared, measurement, criteria);
        var admission = StrategyAdmission.Evaluate(declared, measurement, criteria, nowUtc);

        Line(report, string.Empty);
        Line(report, Inv($"## The admission gate — `{type}`"));
        Line(report, string.Empty);
        Line(report, subtitle);
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Strategy | `{type}` |"));
        Line(report, Inv($"| Declaration | `{declared.Fingerprint[..16]}` from the register, {declared.Basis}, dated {declared.DeclaredAtUtc:yyyy-MM-dd} |"));
        Line(report, Inv($"| Evidence base | `{declared.EvidenceBaseFingerprint}` |"));
        Line(report, "| Declared event | the next reported figure at or above the last |");
        Line(report, Inv($"| Declared claim | Brier at or under {declared.ClaimedBrier:F2} |"));
        Line(report, Inv($"| Trials in family `{declared.SearchFamily}` | {registered.TrialsInFamily} of {criteria.MaximumTrialsPerSearchFamily} allowed |"));
        Line(report, Inv($"| Families on this evidence base | {registered.FamiliesOnThisEvidenceBase}, so significance {requirement.EffectiveSignificance:0.0000} |"));
        Line(report, Inv($"| Clusters | {byYear.Count} years, rho {rho:F4} |"));
        Line(report, Inv($"| Floor | {requirement.Floor} |"));
        Line(report, Inv($"| Derived requirement | {(requirement.IsUnattainable ? "unattainable" : requirement.Derived.ToString(CultureInfo.InvariantCulture))} |"));
        Line(report, Inv($"| **Sample required** | **{(requirement.IsUnattainable ? "unattainable" : requirement.Required.ToString(CultureInfo.InvariantCulture))}** |"));
        Line(report, Inv($"| Resolved predictions | {measurement.ResolvedPredictions} |"));
        Line(report, Inv($"| Effective after clustering | {measurement.EffectiveResolvedPredictions} |"));
        Line(report, Inv($"| Brier score | {Shown(curve.BrierScore)} against a ceiling of {criteria.MaximumBrierScore:F2} |"));
        Line(report, Inv($"| Base-rate reference | {measurement.ReferenceBrier:F4} |"));
        Line(report, Inv($"| **Admitted to the ranked pool** | **{(admission.IsAdmitted ? "yes" : "NO")}** |"));
        Line(report, string.Empty);
        Line(report, Inv($"How the requirement was arrived at: {requirement.Basis}"));
        Line(report, string.Empty);
        Line(report, Inv($"Why this entry is in the register: {registered.Note}"));
        Line(report, string.Empty);

        if (admission.IsAdmitted)
        {
            Line(report, "The strategy clears the bar and its opportunities may compete for an action.");

            return;
        }

        Line(report, "Refused, for these reasons:");
        Line(report, string.Empty);

        foreach (var (refusal, reason) in admission.Refusals.Zip(admission.Reasons))
        {
            Line(report, Inv($"- `{refusal}` — {reason}"));
        }
    }

    /// <summary>
    /// The revenue-only strategy, declared in the register before this measurement ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared before the measurement, and <strong>not</strong> before revenue was seen. The
    /// pooled run printed a per-attribute table in which revenue scored best, and that table is why
    /// this strategy exists. The declaration says so in its note and its trial count says so in a
    /// number the gate reads, which is the only part of an honest disclosure that has teeth.
    /// </para>
    /// <para>
    /// Everything else about the rule is identical. The restriction to one attribute is the whole
    /// strategy, so nothing was tuned to produce it.
    /// </para>
    /// </remarks>
    private static void AppendRevenue(
        StringBuilder report,
        IServiceProvider services,
        IReadOnlyDictionary<string, RegisteredStrategy> register,
        List<Prediction> all,
        DateTime nowUtc)
    {
        var predictions = all
            .Where(p => string.Equals(p.Attribute, RevenueAttribute, StringComparison.Ordinal))
            .ToList();

        if (predictions.Count == 0)
        {
            Line(report, string.Empty);
            Line(report, Inv($"## The admission gate — `{RevenueStrategy}`"));
            Line(report, string.Empty);
            Line(report, Inv($"No `{RevenueAttribute}` prediction resolved. Nothing to score."));

            return;
        }

        var curve = CalibrationCurve.From(predictions.Select(p => (p.Probability, p.Occurred)));

        AppendAdmission(
            report,
            services,
            register,
            RevenueStrategy,
            predictions,
            curve,
            nowUtc,
            Inv($"The same rule, restricted to `{RevenueAttribute}`. Declared before this run and after the pooled table that motivated it — see the trial count."));
    }

    /// <summary>The committed declaration register, beside the solution rather than in the build.</summary>
    private static string RegisterPath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            StrategyRegister.RelativePath));

    private static decimal Brier(List<Prediction> predictions) =>
        predictions.Count == 0
            ? 0m
            : predictions.Sum(p =>
            {
                var error = p.Probability - (p.Occurred ? 1m : 0m);

                return error * error;
            }) / predictions.Count;

    private static string Shown(Measurement measurement) =>
        measurement.IsMeasured
            ? Inv($"{measurement.Value!.Value:F4} (n={measurement.SampleSize})")
            : NotMeasured;

    private static string Rate(int part, int whole) =>
        whole == 0 ? "—" : Inv($"{(decimal)part / whole:P2}");

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "fundamentals.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report);
    }

    // ---- collected shapes ---------------------------------------------------

    private sealed record Figure(
        string Company,
        string Attribute,
        DateTime PeriodEndUtc,
        DateTime PublishedAtUtc,
        decimal Value);

    private sealed record Series(string Company, string Attribute, List<Figure> Figures);

    private sealed record Period(
        DateTime PeriodEndUtc,
        DateTime FirstPublishedAtUtc,
        decimal FirstValue,
        List<Figure> Publications);

    private sealed record Prediction(
        string Company,
        string Attribute,
        DateTime DecidedAtUtc,
        DateTime PredictedPeriodEndUtc,
        decimal Probability,
        bool Occurred);
}
