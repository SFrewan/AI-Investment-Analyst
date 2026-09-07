using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Opportunities.Research;
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
/// Nine pre-registered strategies, scored against the stored evidence. Observational only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This creates nothing and changes nothing.</strong> It reads observations, forms
/// predictions in memory and counts the answers. No opportunity is drafted, no prediction is
/// persisted, no order is placed and no provider is called. Opportunity and observation counts are
/// asserted unchanged at the end.
/// </para>
/// <para>
/// <strong>Why these.</strong> They are hypotheses from the finance literature, not shapes found
/// by looking at this data - which is the difference between a hypothesis and a search, and the
/// difference the trial budget exists to police. Four condition on different information channels
/// in the filings themselves: accrual quality, asset-growth persistence, margin persistence, and
/// how late a company files. The last two test a price move, and are the reason the SEC backfill
/// and then the five-year price acquisition were worth doing: filings give dated events, and a
/// dated event is what a drift study needs. Those two share their cases exactly and differ only in
/// which declaration judges them.
/// </para>
/// <para>
/// <strong>What was fixed before any of it ran.</strong> Every declaration is in
/// <c>declarations/strategies.json</c>, sealed with its fingerprint, before this file was executed
/// once. The minimum history, the smoothing and the clustering are fixed in
/// <see cref="ConditionalFrequency"/> and here, identically for all of them, so no strategy can be
/// helped by a choice made for it alone.
/// </para>
/// <para>
/// Gated on <c>AIINV_RESEARCH=1</c>.
/// </para>
/// </remarks>
public sealed class ResearchRehearsalTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_RESEARCH";

    private const string CompanySubjectKind = "Company";
    private const string SecuritySubjectKind = "Security";
    private const string CloseAttribute = "security.close";

    private const string NetIncome = "financials.net-income";
    private const string OperatingCashFlow = "financials.operating-cash-flow";
    private const string TotalAssets = "financials.total-assets";
    private const string OperatingIncome = "financials.operating-income";

    /// <summary>The forward window for the only strategy that tests a price move.</summary>
    private const int DriftSessions = 21;

    /// <summary>How many prior filings the late-filing rule needs before it has a median to compare to.</summary>
    private const int MinimumPriorLags = 3;

    /// <summary>
    /// The drawdown screen's own numbers, copied from the discovery configuration verbatim.
    /// </summary>
    /// <remarks>
    /// Copied rather than chosen, and named here so the copy is auditable against
    /// <c>appsettings.Development.json</c>'s <c>Discovery</c> section: drawdown 0.1, window 120,
    /// warm-up 60. The candidate that uses them adds one conditioning variable to an existing
    /// screen; inventing new values would make it a differently-tuned screen instead, which is the
    /// thing this pass is forbidden to produce.
    /// </remarks>
    private const decimal DrawdownRatio = 0.10m;

    private const int DrawdownWindow = 120;

    private const int DrawdownWarmup = 60;

    /// <summary>Fewest companies with a filed figure before a cross-sectional rank means anything.</summary>
    private const int MinimumCrossSection = 8;

    /// <summary>The frozen universe, as the backfill fixed it. Two CIKs hold no facts.</summary>
    private static readonly (string Ticker, string Cik)[] Universe =
    {
        ("MSFT.US", "0000789019"), ("GOOGL.US", "0001652044"), ("AMZN.US", "0001018724"),
        ("NVDA.US", "0001045810"), ("META.US", "0001326801"), ("TSLA.US", "0001318605"),
        ("JPM.US", "0000019617"), ("V.US", "0001403161"), ("JNJ.US", "0000200406"),
        ("WMT.US", "0000104169"), ("PG.US", "0000080424"), ("XOM.US", "0002115436"),
        ("UNH.US", "0000731766"), ("MA.US", "0001141391"), ("KO.US", "0000021344"),
        ("PEP.US", "0000077476"), ("CVX.US", "0000093410"), ("MRK.US", "0000310158"),
        ("AAPL.US", "0000320193"), ("HD.US", "0000060695"),
    };

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public ResearchRehearsalTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Nine_pre_registered_strategies_are_scored_without_creating_anything()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Research rehearsal is off. Set {GateVariable}=1 to run it. It reads only.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var criteria = services.GetRequiredService<AdmissionCriteria>();
        var nowUtc = services.GetRequiredService<IClock>().UtcNow;

        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();

        var register = StrategyRegister.Parse(File.ReadAllText(RegisterPath()));
        var timelines = await LoadFundamentalsAsync(context);
        var prices = await LoadPricesAsync(context);

        Skip.If(timelines.Count == 0, "No fundamental observations are stored. Nothing to research.");

        _output.WriteLine(Inv($"{timelines.Count} companies, {prices.Count} price series, {watch.Elapsed.TotalSeconds:F0}s"));

        var studies = new List<Study>
        {
            Run("accrual-quality-persistence", AccrualCases(timelines), Cluster.Year,
                "Operating cash flow at or above net income, against the next reported net income rising.",
                "Sloan's accrual question, restated so it resolves from filings alone: earnings backed by cash should persist better than earnings that are not."),
            Run("asset-growth-persistence", AssetGrowthCases(timelines), Cluster.Year,
                "Total assets rose last period, against total assets rising again next period.",
                "Whether balance-sheet expansion carries on or reverses. The asset-growth anomaly's mechanism, tested on the accounts rather than on returns."),
            Run("operating-return-persistence", ReturnOnAssetsCases(timelines), Cluster.Year,
                "Operating income over total assets rose last period, against it rising again next period.",
                "Whether an improving operating return persists, or competition erodes it. A different channel from asset growth: profitability rather than size."),
            Run("late-filing-warning", LateFilingCases(timelines), Cluster.Year,
                "This filing was later after period end than the company's own prior median, against the next reported net income rising.",
                "Uses the publication date rather than the figure. Only possible because the backfill preserved point-in-time filing dates."),
            Run("post-filing-drift", DriftCases(timelines, prices), Cluster.Month,
                Inv($"A filing reporting higher net income, against the next {DriftSessions} sessions returning at or above zero."),
                "The one strategy here that tests a price move, and the payoff from the SEC backfill: a filing is a dated event, and a dated event is what a drift study needs."),

            // The same cases, judged against a different declaration. Deliberately identical
            // evidence: the only thing that varies between this study and the one above it is what
            // was written down beforehand - the evidence base named, the claim made, and the
            // position in the search family. Anything the gate says differently about the two is
            // therefore attributable to the declaration and to nothing else, which is the cleanest
            // demonstration of what a declaration is actually for that this repository can make.
            Run("post-filing-drift-deep", DriftCases(timelines, prices), Cluster.Month,
                Inv($"The same event, pre-registered against the five-year base: a filing reporting higher net income, against the next {DriftSessions} sessions returning at or above zero."),
                "Sealed on 2026-09-02, before a single session of the five-year history had been fetched, with a relative claim because the base rate of a 21-session return over five years was not known then. The register's only Prospective entry, and the reason the history was acquired."),

            // ---- price and fundamentals together, sealed 2026-09-02T10:00Z ----
            //
            // All three are declared Backtest, will each be refused EventDeclaredAfterTheEvidence,
            // and are here to eliminate rather than to admit. The decision rule was fixed before
            // any of them ran: positive skill, a hindsight ceiling of at least 0.01, and an
            // effective sample of at least 100, or the candidate is retired.
            Run("profitable-drawdown-recovery", ProfitableDrawdownCases(timelines, prices), Cluster.Month,
                Inv($"A drawdown of at least {DrawdownRatio:P0} from the trailing peak, split by whether the most recently filed operating return on assets is above the cross-sectional median, against the next {DriftSessions} sessions returning at or above zero."),
                "Novy-Marx's quality question put to the platform's own failed price screen: a drawdown in a profitable firm and a drawdown in an unprofitable one may not be the same event. The threshold, window and horizon are copied verbatim from that screen rather than chosen, so this adds one conditioning variable and no free parameters."),
            Run("accrual-quality-return", AccrualReturnCases(timelines, prices), Cluster.Month,
                Inv($"Most recently filed operating cash flow at or above net income, against the next non-overlapping {DriftSessions} sessions returning at or above zero."),
                "Sloan (1996) in its original form. The platform's first attempt tested the signal against the next earnings figure and found -0.0074 of skill against a hindsight ceiling of 0.0002; the claim was always about returns."),
            Run("asset-growth-return", AssetGrowthReturnCases(timelines, prices), Cluster.Month,
                Inv($"Most recently filed total assets higher than the prior filing's, against the next non-overlapping {DriftSessions} sessions returning at or above zero — declared in the negative direction."),
                "Cooper, Gulen and Schill (2008): expansion predicts lower subsequent returns. The persistence version scored exactly the base rate and its requirement came back unattainable. The direction was fixed in the declaration before measurement, so reporting whichever direction worked is not available."),
        };

        var report = Compose(studies, register, criteria, nowUtc, watch.Elapsed);

        await WriteAsync(report);
        _output.WriteLine(report);

        var opportunitiesAfter = await context.Opportunities.AsNoTracking().CountAsync();
        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();

        Assert.Equal(opportunitiesBefore, opportunitiesAfter);
        Assert.Equal(observationsBefore, observationsAfter);
    }

    // ---- reading ------------------------------------------------------------

    private static async Task<Dictionary<string, Timeline>> LoadFundamentalsAsync(AppDbContext context)
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

        var timelines = new Dictionary<string, Timeline>(StringComparer.Ordinal);

        foreach (var group in rows
            .Where(r => r.Kind == ObservationValueKind.Number)
            .GroupBy(r => r.Company, StringComparer.Ordinal))
        {
            var byAttribute = new Dictionary<string, List<Point>>(StringComparer.Ordinal);

            foreach (var attribute in group.GroupBy(r => r.Attribute, StringComparer.Ordinal))
            {
                var points = new List<Point>();

                foreach (var period in attribute.GroupBy(r => r.AsOfUtc))
                {
                    // What was on the record first for this period. A restatement is a later
                    // publication of the same period and is not a new observation of the business.
                    var first = period.OrderBy(r => r.PublishedAtUtc).First();

                    if (!decimal.TryParse(
                            first.Canonical,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var value))
                    {
                        continue;
                    }

                    points.Add(new Point(
                        DateTime.SpecifyKind(period.Key, DateTimeKind.Utc),
                        DateTime.SpecifyKind(first.PublishedAtUtc, DateTimeKind.Utc),
                        value));
                }

                if (points.Count > 0)
                {
                    byAttribute[attribute.Key] = points.OrderBy(p => p.PeriodEndUtc).ToList();
                }
            }

            timelines[group.Key] = new Timeline(group.Key, byAttribute);
        }

        return timelines;
    }

    private static async Task<Dictionary<string, List<Session>>> LoadPricesAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == SecuritySubjectKind && o.Attribute == CloseAttribute)
            .Select(o => new
            {
                Instrument = o.Subject.Identifier!,
                o.Value.Kind,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .ToListAsync();

        var series = new Dictionary<string, List<Session>>(StringComparer.Ordinal);

        foreach (var group in rows
            .Where(r => r.Kind == ObservationValueKind.Number)
            .GroupBy(r => r.Instrument, StringComparer.Ordinal))
        {
            var sessions = new List<Session>();

            foreach (var day in group.GroupBy(r => r.AsOfUtc))
            {
                var latest = day.OrderByDescending(r => r.PublishedAtUtc).First();

                if (!decimal.TryParse(
                        latest.Canonical,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var close))
                {
                    continue;
                }

                sessions.Add(new Session(
                    DateTime.SpecifyKind(day.Key, DateTimeKind.Utc),
                    DateTime.SpecifyKind(latest.PublishedAtUtc, DateTimeKind.Utc),
                    close));
            }

            series[group.Key] = sessions.OrderBy(s => s.SessionUtc).ToList();
        }

        return series;
    }

    // ---- the strategies ------------------------------------------------------

    /// <summary>Earnings backed by cash, against the next reported net income rising.</summary>
    private static List<ResearchCase> AccrualCases(Dictionary<string, Timeline> timelines)
    {
        var cases = new List<ResearchCase>();

        foreach (var timeline in timelines.Values)
        {
            var income = timeline.Series(NetIncome);
            var cash = timeline.Series(OperatingCashFlow);

            for (var k = 0; k + 1 < income.Count; k++)
            {
                var matched = cash.FirstOrDefault(p => p.PeriodEndUtc == income[k].PeriodEndUtc);

                if (matched is null)
                {
                    continue;
                }

                var decidedAt = Later(income[k].PublishedAtUtc, matched.PublishedAtUtc).AddMinutes(1);
                var resolvedAt = income[k + 1].PublishedAtUtc;

                if (resolvedAt <= decidedAt)
                {
                    continue;
                }

                cases.Add(new ResearchCase(
                    timeline.Company,
                    decidedAt,
                    resolvedAt,
                    matched.Value >= income[k].Value ? 1 : 0,
                    income[k + 1].Value >= income[k].Value));
            }
        }

        return cases;
    }

    /// <summary>Whether balance-sheet expansion carries on.</summary>
    private static List<ResearchCase> AssetGrowthCases(Dictionary<string, Timeline> timelines) =>
        Persistence(timelines, TotalAssets);

    /// <summary>Whether an improving operating return persists.</summary>
    private static List<ResearchCase> ReturnOnAssetsCases(Dictionary<string, Timeline> timelines)
    {
        var cases = new List<ResearchCase>();

        foreach (var timeline in timelines.Values)
        {
            var ratios = new List<Point>();

            foreach (var income in timeline.Series(OperatingIncome))
            {
                var assets = timeline
                    .Series(TotalAssets)
                    .FirstOrDefault(p => p.PeriodEndUtc == income.PeriodEndUtc);

                if (assets is null || assets.Value == 0m)
                {
                    continue;
                }

                ratios.Add(new Point(
                    income.PeriodEndUtc,
                    Later(income.PublishedAtUtc, assets.PublishedAtUtc),
                    income.Value / assets.Value));
            }

            cases.AddRange(Persist(timeline.Company, ratios));
        }

        return cases;
    }

    /// <summary>A filing later than the company's own prior median, against the next figure rising.</summary>
    private static List<ResearchCase> LateFilingCases(Dictionary<string, Timeline> timelines)
    {
        var cases = new List<ResearchCase>();

        foreach (var timeline in timelines.Values)
        {
            var income = timeline.Series(NetIncome);

            for (var k = MinimumPriorLags; k + 1 < income.Count; k++)
            {
                // Only lags from filings already on the record at this decision.
                var priorLags = new List<double>();

                for (var j = 0; j < k; j++)
                {
                    priorLags.Add((income[j].PublishedAtUtc - income[j].PeriodEndUtc).TotalDays);
                }

                priorLags.Sort();

                var median = priorLags.Count % 2 == 1
                    ? priorLags[priorLags.Count / 2]
                    : (priorLags[(priorLags.Count / 2) - 1] + priorLags[priorLags.Count / 2]) / 2d;

                var lag = (income[k].PublishedAtUtc - income[k].PeriodEndUtc).TotalDays;
                var decidedAt = income[k].PublishedAtUtc.AddMinutes(1);
                var resolvedAt = income[k + 1].PublishedAtUtc;

                if (resolvedAt <= decidedAt)
                {
                    continue;
                }

                cases.Add(new ResearchCase(
                    timeline.Company,
                    decidedAt,
                    resolvedAt,
                    lag > median ? 1 : 0,
                    income[k + 1].Value >= income[k].Value));
            }
        }

        return cases;
    }

    /// <summary>
    /// A filing reporting higher net income, against the forward return over the drift window.
    /// </summary>
    /// <remarks>
    /// The entry is the first session whose close was published after the filing, so the position is
    /// taken at a price that was actually available once the filing was public. The exit is the
    /// close twenty-one sessions later. This installation holds no split observations, so a split
    /// inside a window would show up as a discontinuity rather than as a wrong number - stated here
    /// because it is a real limitation of this test rather than a hypothetical one.
    /// </remarks>
    private static List<ResearchCase> DriftCases(
        Dictionary<string, Timeline> timelines,
        Dictionary<string, List<Session>> prices)
    {
        var cases = new List<ResearchCase>();

        foreach (var (ticker, cik) in Universe)
        {
            if (!timelines.TryGetValue(cik, out var timeline) ||
                !prices.TryGetValue(ticker, out var sessions) ||
                sessions.Count <= DriftSessions)
            {
                continue;
            }

            var income = timeline.Series(NetIncome);

            for (var k = 1; k < income.Count; k++)
            {
                var filedAt = income[k].PublishedAtUtc;

                // A filing that predates the stored price window is not an event inside it. Without
                // this, every filing from 2011 finds its "next session" at the start of the 2025
                // window and is scored as though it had just been published - which produces a
                // large, entirely spurious case count clustered on one week.
                if (filedAt <= sessions[0].PublishedAtUtc)
                {
                    continue;
                }

                var entry = sessions.FindIndex(s => s.PublishedAtUtc > filedAt);

                if (entry < 0 || entry + DriftSessions >= sessions.Count)
                {
                    continue;
                }

                var open = sessions[entry];
                var close = sessions[entry + DriftSessions];

                if (open.Close <= 0m)
                {
                    continue;
                }

                cases.Add(new ResearchCase(
                    ticker,
                    open.PublishedAtUtc,
                    close.PublishedAtUtc,
                    income[k].Value >= income[k - 1].Value ? 1 : 0,
                    close.Close >= open.Close));
            }
        }

        return cases;
    }

    // ---- price and fundamentals together ------------------------------------

    /// <summary>
    /// What was on the record for an attribute at an instant, or null if nothing was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point-in-time primitive these three candidates rest on. It scans rather than binary
    /// searches, and that is not laziness: <see cref="LoadFundamentalsAsync"/> orders each series by
    /// <em>period end</em>, not by publication, because a company can file two periods out of order
    /// or restate one. Assuming publication order here would silently let a figure be used before
    /// anyone could have read it, which is the exact bias every point-in-time control in this
    /// platform exists to prevent, and it would be invisible in the output.
    /// </para>
    /// </remarks>
    private static Point? OnTheRecordAt(List<Point> points, DateTime instant)
    {
        Point? latest = null;

        foreach (var point in points)
        {
            if (point.PublishedAtUtc <= instant &&
                (latest is null || point.PublishedAtUtc > latest.PublishedAtUtc))
            {
                latest = point;
            }
        }

        return latest;
    }

    /// <summary>The two most recently published figures at an instant, oldest first.</summary>
    private static (Point Previous, Point Latest)? TwoOnTheRecordAt(
        List<Point> points,
        DateTime instant)
    {
        var visible = points
            .Where(p => p.PublishedAtUtc <= instant)
            .OrderBy(p => p.PublishedAtUtc)
            .ToList();

        return visible.Count < 2 ? null : (visible[^2], visible[^1]);
    }

    /// <summary>Operating income over total assets as of an instant, from what was filed by then.</summary>
    private static decimal? ReturnOnAssetsAt(Timeline timeline, DateTime instant)
    {
        var income = OnTheRecordAt(timeline.Series(OperatingIncome), instant);
        var assets = OnTheRecordAt(timeline.Series(TotalAssets), instant);

        return income is null || assets is null || assets.Value == 0m
            ? null
            : income.Value / assets.Value;
    }

    /// <summary>
    /// One case per instrument per non-overlapping forward window, bucketed by a filed condition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Non-overlapping is the whole point.</strong> Consecutive daily observations of a
    /// twenty-one session forward return share twenty of their twenty-one days, so counting them
    /// all would turn about a thousand real observations into twenty-five thousand apparent ones -
    /// a larger fabrication than the independence adjustment has ever had to catch here. Stepping
    /// by the horizon means every case is disjoint from its neighbour before any clustering is
    /// applied on top.
    /// </para>
    /// <para>
    /// The condition is read as of the session's <em>publication</em> instant, so a figure filed
    /// later the same day cannot inform a position taken earlier.
    /// </para>
    /// </remarks>
    private static List<ResearchCase> ConditionedWindows(
        Dictionary<string, Timeline> timelines,
        Dictionary<string, List<Session>> prices,
        Func<Timeline, DateTime, int?> condition)
    {
        var cases = new List<ResearchCase>();

        foreach (var (ticker, cik) in Universe)
        {
            if (!timelines.TryGetValue(cik, out var timeline) ||
                !prices.TryGetValue(ticker, out var sessions) ||
                sessions.Count <= DriftSessions)
            {
                continue;
            }

            for (var i = 0; i + DriftSessions < sessions.Count; i += DriftSessions)
            {
                var open = sessions[i];
                var close = sessions[i + DriftSessions];

                if (open.Close <= 0m)
                {
                    continue;
                }

                var bucket = condition(timeline, open.PublishedAtUtc);

                if (bucket is null)
                {
                    continue;
                }

                cases.Add(new ResearchCase(
                    ticker,
                    open.PublishedAtUtc,
                    close.PublishedAtUtc,
                    bucket.Value,
                    close.Close >= open.Close));
            }
        }

        return cases;
    }

    /// <summary>Sloan's signal against the forward return rather than the next earnings figure.</summary>
    private static List<ResearchCase> AccrualReturnCases(
        Dictionary<string, Timeline> timelines,
        Dictionary<string, List<Session>> prices) =>
        ConditionedWindows(timelines, prices, (timeline, at) =>
        {
            var cash = OnTheRecordAt(timeline.Series(OperatingCashFlow), at);
            var income = OnTheRecordAt(timeline.Series(NetIncome), at);

            return cash is null || income is null ? null : cash.Value >= income.Value ? 1 : 0;
        });

    /// <summary>The asset-growth anomaly against the forward return.</summary>
    private static List<ResearchCase> AssetGrowthReturnCases(
        Dictionary<string, Timeline> timelines,
        Dictionary<string, List<Session>> prices) =>
        ConditionedWindows(timelines, prices, (timeline, at) =>
        {
            var filed = TwoOnTheRecordAt(timeline.Series(TotalAssets), at);

            return filed is null ? null : filed.Value.Latest.Value > filed.Value.Previous.Value ? 1 : 0;
        });

    /// <summary>
    /// The platform's own drawdown screen, split by whether the firm is profitable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drawdown ratio, the trailing window, the warm-up and the horizon are the discovery
    /// configuration's own values, copied rather than chosen. Picking new ones would add three free
    /// parameters to a candidate whose entire content is one conditioning variable, and would be
    /// indistinguishable from tuning the strategy that already failed.
    /// </para>
    /// <para>
    /// <strong>Episode starts only.</strong> A drawdown persists for many sessions and the screen
    /// speaks on every one of them; the discoverer raises the first. The last rehearsal counted
    /// 6,591 firings against 313 episodes, so scoring firings would inflate the sample twenty-fold
    /// with the same handful of events.
    /// </para>
    /// <para>
    /// The median is cross-sectional and computed at each decision instant from what every company
    /// had filed by then - not from a full-sample median, which would let a firm's rank depend on
    /// figures published years after the position was taken.
    /// </para>
    /// </remarks>
    private static List<ResearchCase> ProfitableDrawdownCases(
        Dictionary<string, Timeline> timelines,
        Dictionary<string, List<Session>> prices)
    {
        var cases = new List<ResearchCase>();

        foreach (var (ticker, cik) in Universe)
        {
            if (!timelines.TryGetValue(cik, out var timeline) ||
                !prices.TryGetValue(ticker, out var sessions) ||
                sessions.Count <= DrawdownWarmup + DriftSessions)
            {
                continue;
            }

            var inDrawdown = false;

            for (var i = DrawdownWarmup; i + DriftSessions < sessions.Count; i++)
            {
                var peak = 0m;

                for (var j = Math.Max(0, i - DrawdownWindow + 1); j <= i; j++)
                {
                    if (sessions[j].Close > peak)
                    {
                        peak = sessions[j].Close;
                    }
                }

                var below = peak > 0m && sessions[i].Close <= peak * (1m - DrawdownRatio);

                if (!below)
                {
                    inDrawdown = false;

                    continue;
                }

                if (inDrawdown)
                {
                    // Still the same episode. The screen would speak; the discoverer would not.
                    continue;
                }

                inDrawdown = true;

                var open = sessions[i];

                if (open.Close <= 0m)
                {
                    continue;
                }

                var own = ReturnOnAssetsAt(timeline, open.PublishedAtUtc);

                if (own is null)
                {
                    continue;
                }

                var median = CrossSectionalMedianReturnOnAssets(timelines, open.PublishedAtUtc);

                if (median is null)
                {
                    continue;
                }

                cases.Add(new ResearchCase(
                    ticker,
                    open.PublishedAtUtc,
                    sessions[i + DriftSessions].PublishedAtUtc,
                    own.Value >= median.Value ? 1 : 0,
                    sessions[i + DriftSessions].Close >= open.Close));
            }
        }

        return cases;
    }

    /// <summary>The median filed return on assets across the universe at an instant.</summary>
    /// <remarks>
    /// Null when fewer than <see cref="MinimumCrossSection"/> companies had anything on the record,
    /// because a median of three is not a cross-section and a rank against one means nothing.
    /// </remarks>
    private static decimal? CrossSectionalMedianReturnOnAssets(
        Dictionary<string, Timeline> timelines,
        DateTime instant)
    {
        var values = new List<decimal>();

        foreach (var (_, cik) in Universe)
        {
            if (timelines.TryGetValue(cik, out var timeline) &&
                ReturnOnAssetsAt(timeline, instant) is { } value)
            {
                values.Add(value);
            }
        }

        if (values.Count < MinimumCrossSection)
        {
            return null;
        }

        values.Sort();

        return values[values.Count / 2];
    }

    private static List<ResearchCase> Persistence(Dictionary<string, Timeline> timelines, string attribute)
    {
        var cases = new List<ResearchCase>();

        foreach (var timeline in timelines.Values)
        {
            cases.AddRange(Persist(timeline.Company, timeline.Series(attribute)));
        }

        return cases;
    }

    /// <summary>Did it rise last period, against it rising again next period.</summary>
    private static IEnumerable<ResearchCase> Persist(string subject, List<Point> points)
    {
        for (var k = 1; k + 1 < points.Count; k++)
        {
            var decidedAt = points[k].PublishedAtUtc.AddMinutes(1);
            var resolvedAt = points[k + 1].PublishedAtUtc;

            if (resolvedAt <= decidedAt)
            {
                continue;
            }

            yield return new ResearchCase(
                subject,
                decidedAt,
                resolvedAt,
                points[k].Value >= points[k - 1].Value ? 1 : 0,
                points[k + 1].Value >= points[k].Value);
        }
    }

    // ---- scoring ------------------------------------------------------------

    private static Study Run(
        string strategy,
        List<ResearchCase> cases,
        Cluster cluster,
        string claim,
        string why)
    {
        var predictions = ConditionalFrequency.Score(cases);

        return new Study(strategy, claim, why, cluster, cases.Count, predictions);
    }

    // ---- the report ---------------------------------------------------------

    private static string Compose(
        List<Study> studies,
        IReadOnlyDictionary<string, RegisteredStrategy> register,
        AdmissionCriteria criteria,
        DateTime nowUtc,
        TimeSpan elapsed)
    {
        var report = new StringBuilder();

        Line(report, "# Nine pre-registered strategies — read-only research");
        Line(report, string.Empty);
        Line(report, Inv($"Generated {nowUtc:yyyy-MM-dd HH:mm:ss}Z in {elapsed.TotalSeconds:F0}s. Nothing was created, written or fetched."));
        Line(report, string.Empty);
        Line(report, Inv($"Every probability is the Laplace-smoothed frequency of past cases that shared the strategy's signal, from at least {ConditionalFrequency.MinimumPriorCases} cases that had **resolved** before the decision. No strategy has a parameter of its own."));

        // ---- the scorecard --------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Scorecard");
        Line(report, string.Empty);
        Line(report, "| Strategy | Scored | Base rate | Reference | Brier | Skill | Separation | Hindsight ceiling |");
        Line(report, "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var study in studies)
        {
            if (study.Predictions.Count == 0)
            {
                Line(report, Inv($"| `{study.Strategy}` | 0 of {study.Cases} cases | — | — | — | — | — | — |"));

                continue;
            }

            Line(
                report,
                Inv($"| `{study.Strategy}` | {study.Predictions.Count} | {study.BaseRate:P2} | ") +
                Inv($"{study.Reference:F4} | {study.Brier:F4} | {study.Reference - study.Brier:+0.0000;-0.0000;0.0000} | ") +
                Inv($"{study.Separation:P2} | {study.Reference - study.HindsightBrier:F4} |"));
        }

        Line(report, string.Empty);
        Line(report, "**Skill** is how far the measured Brier beats the base-rate reference; negative means the strategy did worse than knowing only how often the event happens. **Separation** is the gap between how often the two buckets actually resolved true. **Hindsight ceiling** is the skill this conditioning would reach if each bucket's own realised rate had been known in advance — an impossible forecaster, and therefore an upper bound no walk-forward estimate can pass.");

        // ---- per strategy ---------------------------------------------------

        foreach (var study in studies)
        {
            AppendStudy(report, study, register, criteria, nowUtc);
        }

        return report.ToString();
    }

    private static void AppendStudy(
        StringBuilder report,
        Study study,
        IReadOnlyDictionary<string, RegisteredStrategy> register,
        AdmissionCriteria criteria,
        DateTime nowUtc)
    {
        var registered = StrategyRegister.Require(register, study.Strategy);
        var declared = registered.Event;

        Line(report, string.Empty);
        Line(report, Inv($"## `{study.Strategy}`"));
        Line(report, string.Empty);
        Line(report, Inv($"**Claim.** {study.Claim}"));
        Line(report, string.Empty);
        Line(report, Inv($"**Why it is here.** {study.Why}"));
        Line(report, string.Empty);
        Line(report, Inv($"**Declaration.** `{declared.Fingerprint[..16]}`, {declared.Basis}, dated {declared.DeclaredAtUtc:yyyy-MM-dd}, sealed in the register before this ran."));

        if (study.Predictions.Count == 0)
        {
            Line(report, string.Empty);
            Line(report, Inv($"**No prediction resolved** from {study.Cases} cases. Nothing can be said."));

            return;
        }

        var scored = study.Predictions.Select(p => (p.Probability, p.Occurred)).ToList();
        var curve = CalibrationCurve.From(scored);
        var clusters = study.Clusters();
        var rho = ScoreStatistics.IntraClusterCorrelation(clusters);

        var measurement = StrategyMeasurement.Create(
            declared.Type,
            study.Predictions.Count,
            curve.BrierScore,
            declared.Threshold,
            study.Predictions.Min(p => p.DecidedAtUtc),
            nowUtc,
            declared.EvidenceBaseFingerprint,
            registered.TrialsInFamily,
            registered.FamiliesOnThisEvidenceBase,
            baseRate: study.BaseRate,
            componentStandardDeviation: ScoreStatistics.ComponentSpread(scored),
            independentClusters: clusters.Count,
            intraClusterCorrelation: rho);

        var requirement = SampleRequirement.For(declared, measurement, criteria);
        var admission = StrategyAdmission.Evaluate(declared, measurement, criteria, nowUtc);

        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Evidence base | `{declared.EvidenceBaseFingerprint}` |"));
        Line(report, Inv($"| Cases built / scored | {study.Cases} / {study.Predictions.Count} |"));
        Line(report, Inv($"| Decision window | {study.Predictions.Min(p => p.DecidedAtUtc):yyyy-MM-dd} → {study.Predictions.Max(p => p.DecidedAtUtc):yyyy-MM-dd} |"));
        Line(report, Inv($"| Base rate | {study.BaseRate:P2} |"));
        Line(report, Inv($"| Base-rate reference Brier | {study.Reference:F4} |"));
        Line(report, Inv($"| **Measured Brier** | **{study.Brier:F4}** |"));
        Line(report, Inv($"| Skill over the reference | {study.Reference - study.Brier:F4} ({Share(study)}) |"));
        Line(report, Inv($"| Bucket separation | {study.Separation:P2} |"));
        Line(report, Inv($"| Hindsight ceiling on skill | {study.Reference - study.HindsightBrier:F4} |"));
        Line(report, Inv($"| Clusters ({study.Grouping}) | {clusters.Count}, rho {rho:F4} |"));
        Line(report, Inv($"| Effective sample | {measurement.EffectiveResolvedPredictions} of {study.Predictions.Count} |"));
        Line(report, Inv($"| Sample required | {Bar(requirement)} |"));
        Line(report, Inv($"| Trials in family `{declared.SearchFamily}` | {registered.TrialsInFamily} of {criteria.MaximumTrialsPerSearchFamily} allowed |"));
        Line(report, Inv($"| Families on this evidence base | {registered.FamiliesOnThisEvidenceBase}, so significance {requirement.EffectiveSignificance:0.0000} |"));
        Line(report, Inv($"| **Admitted** | **{(admission.IsAdmitted ? "yes" : "NO")}** |"));

        Line(report, string.Empty);
        Line(report, "**By bucket.**");
        Line(report, string.Empty);
        Line(report, "| Signal | Predictions | Resolved true | Rate | Mean stated |");
        Line(report, "| --- | ---: | ---: | ---: | ---: |");

        foreach (var bucket in study.Predictions.GroupBy(p => p.SignalBucket).OrderBy(g => g.Key))
        {
            var rows = bucket.ToList();
            var won = rows.Count(p => p.Occurred);

            var side = bucket.Key == 1 ? "condition met" : "condition not met";

            Line(
                report,
                Inv($"| {side} | {rows.Count} | {won} | ") +
                Inv($"{(decimal)won / rows.Count:P2} | {rows.Average(p => p.Probability):F4} |"));
        }

        Line(report, string.Empty);
        Line(report, "**Refused for:**");
        Line(report, string.Empty);

        if (admission.IsAdmitted)
        {
            Line(report, "Nothing. This strategy cleared every control.");

            return;
        }

        foreach (var (refusal, reason) in admission.Refusals.Zip(admission.Reasons))
        {
            Line(report, Inv($"- `{refusal}` — {reason}"));
        }

        Line(report, string.Empty);
        Line(report, Inv($"Requirement: {requirement.Basis}"));
    }

    private static string Share(Study study) =>
        study.Reference <= 0m
            ? "—"
            : Inv($"{(study.Reference - study.Brier) / study.Reference:P1} of what was available");

    private static string Bar(SampleRequirement requirement) =>
        requirement.IsUnattainable
            ? "unattainable"
            : Inv($"{requirement.Required} (floor {requirement.Floor}, derived {requirement.Derived})");

    private static DateTime Later(DateTime a, DateTime b) => a >= b ? a : b;

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static string RegisterPath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            StrategyRegister.RelativePath));

    private static async Task WriteAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "research.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report);
    }

    // ---- collected shapes ---------------------------------------------------

    /// <summary>How a strategy's predictions are grouped for the independence discount.</summary>
    private enum Cluster
    {
        Year = 0,
        Month = 1,
    }

    private sealed record Point(DateTime PeriodEndUtc, DateTime PublishedAtUtc, decimal Value);

    private sealed record Session(DateTime SessionUtc, DateTime PublishedAtUtc, decimal Close);

    private sealed record Timeline(string Company, Dictionary<string, List<Point>> ByAttribute)
    {
        public List<Point> Series(string attribute) =>
            ByAttribute.TryGetValue(attribute, out var points) ? points : new List<Point>();
    }

    private sealed record Study(
        string Strategy,
        string Claim,
        string Why,
        Cluster Grouping,
        int Cases,
        IReadOnlyList<ResearchPrediction> Predictions)
    {
        public decimal BaseRate =>
            Predictions.Count == 0
                ? 0m
                : (decimal)Predictions.Count(p => p.Occurred) / Predictions.Count;

        public decimal Reference => BaseRate * (1m - BaseRate);

        public decimal Brier =>
            Predictions.Count == 0
                ? 0m
                : Predictions.Sum(p =>
                {
                    var error = p.Probability - (p.Occurred ? 1m : 0m);

                    return error * error;
                }) / Predictions.Count;

        public decimal Separation => ConditionalFrequency.BucketSeparation(Predictions);

        /// <summary>
        /// The best Brier this conditioning could reach if each bucket's realised rate were known
        /// in advance.
        /// </summary>
        /// <remarks>
        /// The question a large-looking separation cannot answer on its own: how much is the split
        /// actually worth? This states each bucket's own hindsight rate on every case in it, which
        /// no forecaster could do, and scores that. Whatever a walk-forward estimate achieves must
        /// be below it, so the gap between this and the base-rate reference is the entire prize -
        /// and if that gap is small, the conditioning was never going to pay however well it was
        /// estimated.
        /// </remarks>
        public decimal HindsightBrier
        {
            get
            {
                if (Predictions.Count == 0)
                {
                    return 0m;
                }

                var total = 0m;

                foreach (var bucket in Predictions.GroupBy(p => p.SignalBucket))
                {
                    var rows = bucket.Count();
                    var rate = (decimal)bucket.Count(p => p.Occurred) / rows;

                    total += rows * rate * (1m - rate);
                }

                return total / Predictions.Count;
            }
        }

        public List<(int Size, int Successes)> Clusters() =>
            Predictions
                .GroupBy(p => Grouping == Cluster.Month
                    ? (p.DecidedAtUtc.Year * 100) + p.DecidedAtUtc.Month
                    : p.DecidedAtUtc.Year)
                .Select(g => (Size: g.Count(), Successes: g.Count(p => p.Occurred)))
                .ToList();
    }
}
