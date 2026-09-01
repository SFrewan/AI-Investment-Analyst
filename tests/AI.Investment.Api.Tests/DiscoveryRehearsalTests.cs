using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Counts what the existing screen would have found over the stored year. Observational only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This creates nothing and changes nothing.</strong> It reads the observation store through
/// the same point-in-time, split-adjusted path the cycle uses, applies the same rule with the same
/// configured parameters, and counts the answers. No opportunity is drafted, no prediction is
/// written, no order is placed, and no provider is called. Two guards at the end assert that the
/// opportunity and observation counts are unchanged, so "observational" is a property of the run
/// rather than a claim about it.
/// </para>
/// <para>
/// <strong>Why it exists.</strong> Promotion requires 100 scored predictions, a hit rate of 0.60, a
/// Brier score of 0.20 or better, and calibration bins with ten samples each. Whether a
/// ten-per-cent-drawdown rule can produce that from one year of twenty instruments is an empirical
/// question, and the answer is already in the database. Building the rest of Block 3 before asking
/// it would be building on an assumption.
/// </para>
/// <para>
/// <strong>The decision points.</strong> The rule needs sixty sessions of history, and an outcome
/// needs twenty-one sessions to resolve. So the decisions that can both be made and judged run from
/// the sixtieth session to twenty-one before the last - and each one is read as at the moment that
/// session's close became public, so nothing later is visible to it.
/// </para>
/// <para>
/// Gated on <c>AIINV_REHEARSE=1</c>.
/// </para>
/// </remarks>
public sealed class DiscoveryRehearsalTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_REHEARSE";

    private const string SubjectKind = "Security";

    /// <summary>Ten bins, matching <c>CalibrationCurve</c>'s shape.</summary>
    private const int BinCount = 10;

    /// <summary>What the promotion gate asks for, restated here so the report can compare.</summary>
    private const int RequiredScoredPredictions = 100;

    private const decimal RequiredHitRate = 0.60m;

    private const decimal MaximumBrierScore = 0.20m;

    private const int RequiredPerCalibrationBin = 10;

    /// <summary>
    /// The event threshold is read from the settings now rather than restated here, so the
    /// rehearsal cannot disagree with the rule about what event it is counting.
    /// </summary>

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public DiscoveryRehearsalTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_screen_is_rehearsed_over_the_stored_year_without_creating_anything()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Rehearsal is off. Set {GateVariable}=1 to run it. It reads only.");

        var watch = Stopwatch.StartNew();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var context = services.GetRequiredService<AppDbContext>();
        var settings = services.GetRequiredService<DiscoverySettings>();
        var reader = services.GetRequiredService<PriceSeriesReader>();
        var clock = services.GetRequiredService<IClock>();

        // Counted before and after. This is what makes "observational" checkable.
        var opportunitiesBefore = await context.Opportunities.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();

        var universe = await UniverseAsync(services);
        var results = new List<InstrumentResult>();

        foreach (var instrument in universe)
        {
            results.Add(await RehearseAsync(reader, settings, instrument, clock.UtcNow));

            _output.WriteLine(Inv($"{instrument}: done at {watch.Elapsed.TotalSeconds:F0}s"));
        }

        var report = Compose(results, settings, clock.UtcNow, watch.Elapsed);

        await WriteAsync(report);
        _output.WriteLine(report);

        var opportunitiesAfter = await context.Opportunities.AsNoTracking().CountAsync();
        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();

        Assert.Equal(opportunitiesBefore, opportunitiesAfter);
        Assert.Equal(observationsBefore, observationsAfter);
    }

    // ---- the rehearsal ------------------------------------------------------

    /// <summary>
    /// Replays the cycle's own collect-and-screen for one instrument, at every decision point.
    /// </summary>
    /// <remarks>
    /// The two calls below are the two the work plan makes, in the same order with the same
    /// arguments: <c>ReadAdjustedAsync</c> at the pinned instant, then <c>PriceRecoveryRule.Evaluate</c>
    /// over the observations it returned. Nothing is reimplemented, so a difference between this and
    /// production would have to be a difference in the arguments, which are read from the same
    /// settings object the cycle uses.
    /// </remarks>
    private static async Task<InstrumentResult> RehearseAsync(
        PriceSeriesReader reader,
        DiscoverySettings settings,
        string instrument,
        DateTime nowUtc)
    {
        var subject = IngestionSubject.Create(SubjectKind, instrument);
        var result = new InstrumentResult(instrument);

        // The whole series once, to find the sessions and their publication times. Read through the
        // same adjusted path, so an instrument this platform will not screen at all is visible here
        // as a refusal rather than as an empty result.
        var whole = await reader.ReadAdjustedAsync(
            subject,
            settings.PriceAttribute,
            settings.SplitAttribute,
            int.MaxValue,
            nowUtc,
            settings.MaxUnexplainedMove);

        if (!whole.IsUsable)
        {
            result.WholeSeriesRefusal = whole.Refusal;
            result.WholeSeriesExplanation = whole.Explanation;

            return result;
        }

        var sessions = whole.Observations;

        result.SessionsHeld = sessions.Count;

        var warmUp = settings.Rule.MinimumSessions;
        var horizon = settings.Rule.HorizonSessions;

        // From the sixtieth session to twenty-one before the last: the decisions that can be both
        // made and judged inside what is stored.
        var first = warmUp - 1;
        var last = sessions.Count - 1 - horizon;

        if (last < first)
        {
            return result;
        }

        var previousFired = false;

        for (var i = first; i <= last; i++)
        {
            result.DecisionPoints++;

            // The instant that session's close became public. Reading a minute later makes sessions
            // up to and including i visible, and nothing after.
            var asAt = sessions[i].Provenance.PublishedAtUtc.AddMinutes(1);

            var series = await reader.ReadAdjustedAsync(
                subject,
                settings.PriceAttribute,
                settings.SplitAttribute,

                // One more than the screen reads, exactly as the work plan and the discoverer now
                // read it: the extra session is what tells a new drawdown from one already open.
                settings.MaxSessions + 1,
                asAt,
                settings.MaxUnexplainedMove);

            if (!series.IsUsable)
            {
                result.SeriesRefusals[series.Refusal] = result.SeriesRefusals.GetValueOrDefault(series.Refusal) + 1;
                previousFired = false;

                continue;
            }

            result.Eligible++;

            var closes = series.Observations.Select(price => price.ToClosingPrice()).ToList();

            var window = closes.Count <= settings.MaxSessions
                ? closes
                : closes.Skip(closes.Count - settings.MaxSessions).ToList();

            // Two populations, through the two production methods.
            //
            //   perSession is what the screen said on this day, comparable to the frozen baseline
            //              of 1,108 firings.
            //   episode    is what the discoverer now RAISES - one per drawdown, not one per day.
            var perSession = PriceRecoveryRule.Evaluate(window, settings.Rule);
            var episode = PriceRecoveryRule.EvaluateEpisode(closes, settings.Rule, settings.MaxSessions);

            result.RuleRefusals[perSession.Refusal] =
                result.RuleRefusals.GetValueOrDefault(perSession.Refusal) + 1;

            if (episode.Refusal == PriceRecoveryRefusal.EpisodeAlreadyOpen)
            {
                result.Suppressed++;
            }

            var verdict = perSession;

            // An independent count of the same quantity: a run of consecutive firing sessions is
            // an episode. If this disagrees with what the rule says, one of them is wrong and
            // neither number should be reported until it is known which.
            if (verdict.HasCandidate && !previousFired)
            {
                result.Runs++;
            }

            if (episode.HasCandidate != (verdict.HasCandidate && !previousFired))
            {
                result.Disagreements++;
            }

            previousFired = verdict.HasCandidate;

            if (!verdict.HasCandidate)
            {
                continue;
            }

            var candidate = verdict.Candidate!;

            // TWO different outcomes, because the rule and the validation run do not currently
            // describe the same event - which is the finding, not an inconvenience.
            //
            //   "Profitable"     is the validation run's event: the return beat the configured
            //                    threshold of zero over the horizon.
            //   "Reached target" is the RULE's own event: the close got back to its prior peak
            //                    within the horizon, which is what SuccessProbability is a
            //                    probability OF.
            //
            // A calibration curve or a Brier score built from one probability and the other event
            // measures the mismatch rather than the screen.
            var exit = sessions[i + horizon].Close;
            var realised = (exit - candidate.EntryPrice) / candidate.EntryPrice;
            var occurred = realised >= settings.Rule.EventThresholdRatio;

            var reachedTarget = false;

            for (var j = i + 1; j <= i + horizon; j++)
            {
                if (sessions[j].Close >= candidate.TargetPrice)
                {
                    reachedTarget = true;

                    break;
                }
            }

            // Straight from the rule now, rather than inferred here. The rehearsal measures what
            // production does; a second implementation of the same idea would agree with itself.
            var startsEpisode = episode.HasCandidate;

            result.Firings.Add(new Firing(
                sessions[i].SessionCloseUtc,
                candidate.SuccessProbability,
                candidate.Drawdown,
                candidate.Trials,
                candidate.Successes,
                realised,
                occurred,
                reachedTarget,
                startsEpisode));
        }

        return result;
    }

    private static async Task<List<string>> UniverseAsync(IServiceProvider services)
    {
        var watches = await services.GetRequiredService<IWatchStore>().GetAllAsync();

        return watches
            .Select(w => w.Target.Identifier)
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Select(identifier => identifier!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(identifier => identifier, StringComparer.Ordinal)
            .ToList();
    }

    // ---- the report ---------------------------------------------------------

    private static string Compose(
        List<InstrumentResult> results,
        DiscoverySettings settings,
        DateTime nowUtc,
        TimeSpan elapsed)
    {
        var report = new StringBuilder();

        var firings = results.SelectMany(r => r.Firings).ToList();
        var decisions = results.Sum(r => r.DecisionPoints);
        var eligible = results.Sum(r => r.Eligible);
        var successes = firings.Count(f => f.Succeeded);

        Line(report, "# Block 3 — read-only discovery rehearsal");
        Line(report, string.Empty);
        Line(report, Inv($"Generated {nowUtc:yyyy-MM-dd HH:mm:ss}Z in {elapsed.TotalSeconds:F0}s. Nothing was created, written or fetched."));
        Line(report, string.Empty);
        Line(report, Inv($"Rule: drawdown at least {settings.Rule.DrawdownRatio:P0}, warm-up {settings.Rule.MinimumSessions} sessions, horizon {settings.Rule.HorizonSessions} sessions, minimum {settings.Rule.MinimumTrials} past occurrences, window {settings.MaxSessions} sessions."));

        // ---- headline -------------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Headline");
        Line(report, string.Empty);
        Line(report, "| | |");
        Line(report, "| --- | ---: |");
        Line(report, Inv($"| Instruments rehearsed | {results.Count} |"));
        Line(report, Inv($"| Total decision points | {decisions} |"));
        Line(report, Inv($"| Eligible (series usable) | {eligible} |"));
        Line(report, Inv($"| Opportunities that would have fired | **{firings.Count}** |"));
        Line(report, Inv($"| Firing rate (of eligible) | **{Rate(firings.Count, eligible)}** |"));
        Line(report, Inv($"| Of those, resolved profitably | {successes} |"));
        Line(report, Inv($"| Implied hit rate | {Rate(successes, firings.Count)} |"));

        var episodes = firings.Count(f => f.StartsEpisode);
        var reached = firings.Count(f => f.ReachedTarget);

        Line(report, Inv($"| **Distinct drawdown episodes (from the rule)** | **{episodes}** |"));
        Line(report, Inv($"| Runs of consecutive firings (counted independently) | {results.Sum(r => r.Runs)} |"));
        Line(report, Inv($"| **Decision points where the two disagree** | **{results.Sum(r => r.Disagreements)}** |"));
        Line(report, Inv($"| Firings per episode | {PerEpisode(firings.Count, episodes)} |"));

        if (firings.Count > 0)
        {
            var mean = firings.Average(f => f.Realised);

            Line(report, Inv($"| Mean realised return per firing | {mean:P2} |"));
            Line(report, Inv($"| Reached the stated target within the horizon | {reached} ({Rate(reached, firings.Count)}) |"));
        }

        // ---- against the bar ------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Against the promotion bar");
        Line(report, string.Empty);
        Line(report, "| Criterion | Required | Observed | |");
        Line(report, "| --- | ---: | ---: | --- |");
        var meetsCount = Verdict(firings.Count >= RequiredScoredPredictions);

        Line(report, Inv($"| Scored predictions | {RequiredScoredPredictions} | {firings.Count} | {meetsCount} |"));

        if (firings.Count > 0)
        {
            var hit = (decimal)successes / firings.Count;
            var hitTarget = (decimal)reached / firings.Count;

            var brierProfit = Brier(firings, f => f.Succeeded);
            var brierTarget = Brier(firings, f => f.ReachedTarget);

            Line(report, Inv($"| Hit rate — return beat zero | {RequiredHitRate:F2} | {hit:F4} | {Verdict(hit >= RequiredHitRate)} |"));
            Line(report, Inv($"| Hit rate — reached the stated target | {RequiredHitRate:F2} | {hitTarget:F4} | {Verdict(hitTarget >= RequiredHitRate)} |"));
            Line(report, Inv($"| Brier — probability vs return beat zero | at most {MaximumBrierScore:F2} | {brierProfit:F4} | {Verdict(brierProfit <= MaximumBrierScore)} |"));
            Line(report, Inv($"| Brier — probability vs its own event | at most {MaximumBrierScore:F2} | {brierTarget:F4} | {Verdict(brierTarget <= MaximumBrierScore)} |"));

            Line(report, string.Empty);
            Line(report, "The two Brier rows are the point. `SuccessProbability` is a probability that the");
            Line(report, "close returns to its prior peak within the horizon. The validation run's event is");
            Line(report, "that the return beat zero. Scoring one against the other measures the mismatch, not");
            Line(report, "the screen.");
        }

        // ---- calibration ----------------------------------------------------

        // ---- the two populations, side by side ------------------------------

        var episodeFirings = firings.Where(f => f.StartsEpisode).ToList();

        Line(report, string.Empty);
        Line(report, "## The two populations");
        Line(report, string.Empty);
        Line(report, "Every firing is a session the screen would have spoken on. Only an episode start");
        Line(report, "is an opportunity the discoverer now raises. The promotion bar counts predictions,");
        Line(report, "so the right column is the one it should be read against.");
        Line(report, string.Empty);
        Line(report, "| | All firings | Episode starts |");
        Line(report, "| --- | ---: | ---: |");
        Line(report, Inv($"| Count | {firings.Count} | {episodeFirings.Count} |"));
        Line(report, Inv($"| Return beat the threshold | {Rate(successes, firings.Count)} | {Rate(episodeFirings.Count(f => f.Succeeded), episodeFirings.Count)} |"));

        if (firings.Count > 0 && episodeFirings.Count > 0)
        {
            Line(report, Inv($"| Brier score | {Brier(firings, f => f.Succeeded):F4} | {Brier(episodeFirings, f => f.Succeeded):F4} |"));
            Line(report, Inv($"| Mean stated probability | {firings.Average(f => f.Probability):F4} | {episodeFirings.Average(f => f.Probability):F4} |"));
            Line(report, Inv($"| Mean realised return | {firings.Average(f => f.Realised):P2} | {episodeFirings.Average(f => f.Realised):P2} |"));
        }

        Line(report, string.Empty);
        Line(report, "## Calibration spread");
        Line(report, string.Empty);
        Line(report, Inv($"Ten bins, each needing {RequiredPerCalibrationBin} samples to count."));
        Line(report, string.Empty);
        Line(report, "| Bin | All firings | Beat threshold | Rate | Episode starts | Rate |");
        Line(report, "| --- | ---: | ---: | ---: | ---: | ---: |");

        var usableBins = 0;

        for (var bin = 0; bin < BinCount; bin++)
        {
            var low = bin / (decimal)BinCount;
            var high = (bin + 1) / (decimal)BinCount;

            var inBin = firings
                .Where(f => f.Probability >= low && (bin == BinCount - 1 ? f.Probability <= high : f.Probability < high))
                .ToList();

            if (inBin.Count >= RequiredPerCalibrationBin)
            {
                usableBins++;
            }

            var won = inBin.Count(f => f.Succeeded);
            var episodesInBin = inBin.Count(f => f.StartsEpisode);
            var episodeWon = inBin.Count(f => f.StartsEpisode && f.Succeeded);

            Line(report, Inv($"| {low:F1}–{high:F1} | {inBin.Count} | {won} | {Rate(won, inBin.Count)} | {episodesInBin} | {Rate(episodeWon, episodesInBin)} |"));
        }

        Line(report, string.Empty);
        Line(report, Inv($"**Bins with at least {RequiredPerCalibrationBin} firings: {usableBins} of {BinCount}.**"));

        var episodeBins = 0;

        for (var bin = 0; bin < BinCount; bin++)
        {
            var low = bin / (decimal)BinCount;
            var high = (bin + 1) / (decimal)BinCount;

            var inBin = episodeFirings.Count(f =>
                f.Probability >= low && (bin == BinCount - 1 ? f.Probability <= high : f.Probability < high));

            if (inBin >= RequiredPerCalibrationBin)
            {
                episodeBins++;
            }
        }

        Line(report, Inv($"**Bins with at least {RequiredPerCalibrationBin} episode starts: {episodeBins} of {BinCount}.**"));

        // ---- per instrument -------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Per instrument");
        Line(report, string.Empty);
        Line(report, "| Instrument | Decision points | Fired | Suppressed | Episodes | Episodes that won |");
        Line(report, "| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (var r in results.OrderByDescending(r => r.Firings.Count).ThenBy(r => r.Instrument, StringComparer.Ordinal))
        {
            var fired = r.Firings.Count;
            var eps = r.Firings.Count(f => f.StartsEpisode);
            var epsWon = r.Firings.Count(f => f.StartsEpisode && f.Succeeded);

            Line(report, Inv($"| {r.Instrument} | {r.DecisionPoints} | {fired} | {r.Suppressed} | {eps} | {epsWon} |"));
        }

        var silent = results.Where(r => r.Firings.Count == 0).Select(r => r.Instrument).ToList();
        var rare = results.Where(r => r.Firings.Count is > 0 and < 5).Select(r => r.Instrument).ToList();

        Line(report, string.Empty);
        Line(report, Inv($"- never fired ({silent.Count}): {Names(silent)}"));
        Line(report, Inv($"- fired fewer than five times ({rare.Count}): {Names(rare)}"));

        // ---- over time ------------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Over time");
        Line(report, string.Empty);
        Line(report, "| Month | Fired | Succeeded | Instruments |");
        Line(report, "| --- | ---: | ---: | ---: |");

        foreach (var month in firings
                     .GroupBy(f => new DateTime(f.DecidedAtUtc.Year, f.DecidedAtUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc))
                     .OrderBy(g => g.Key))
        {
            var byInstrument = results
                .Count(r => r.Firings.Any(f => f.DecidedAtUtc.Year == month.Key.Year
                                            && f.DecidedAtUtc.Month == month.Key.Month));

            var monthFired = month.Count();
            var monthWon = month.Count(f => f.Succeeded);

            Line(report, Inv($"| {month.Key:yyyy-MM} | {monthFired} | {monthWon} | {byInstrument} |"));
        }

        // ---- refusals -------------------------------------------------------

        Line(report, string.Empty);
        Line(report, "## Refusals and data quality");
        Line(report, string.Empty);
        Line(report, "### Why the rule declined, across every eligible decision point");
        Line(report, string.Empty);
        Line(report, "| Reason | Count | Share |");
        Line(report, "| --- | ---: | ---: |");

        foreach (var refusal in Enum.GetValues<PriceRecoveryRefusal>())
        {
            var count = results.Sum(r => r.RuleRefusals.GetValueOrDefault(refusal));

            Line(report, Inv($"| `{refusal}` | {count} | {Rate(count, eligible)} |"));
        }

        Line(report, string.Empty);
        Line(report, "### Series the platform would not screen at all");
        Line(report, string.Empty);

        var seriesRefusals = results
            .SelectMany(r => r.SeriesRefusals)
            .GroupBy(pair => pair.Key)
            .ToDictionary(g => g.Key, g => g.Sum(pair => pair.Value));

        if (seriesRefusals.Count == 0)
        {
            Line(report, "None. Every decision point produced a series the screen was willing to read.");
        }
        else
        {
            Line(report, "| Refusal | Decision points |");
            Line(report, "| --- | ---: |");

            foreach (var pair in seriesRefusals.OrderByDescending(p => p.Value))
            {
                Line(report, Inv($"| `{pair.Key}` | {pair.Value} |"));
            }
        }

        var whole = results.Where(r => r.WholeSeriesRefusal is not null).ToList();

        Line(report, string.Empty);

        if (whole.Count == 0)
        {
            Line(report, "No instrument was refused outright.");
        }
        else
        {
            foreach (var r in whole)
            {
                Line(report, Inv($"- **{r.Instrument} refused outright**: {r.WholeSeriesRefusal} — {r.WholeSeriesExplanation}"));
            }
        }

        Line(report, string.Empty);
        Line(report, "This platform holds no split observations at all — the free tier does not carry");
        Line(report, "corporate actions — so any instrument that split inside the window would appear");
        Line(report, "above as an unexplained discontinuity rather than as a wrong number. That the");
        Line(report, "count is what it is, is the measurement of that constraint.");

        return report.ToString();
    }

    private static string Verdict(bool met) => met ? "**met**" : "**NOT met**";

    private static decimal Brier(List<Firing> firings, Func<Firing, bool> occurred) =>
        firings.Sum(f =>
        {
            var error = f.Probability - (occurred(f) ? 1m : 0m);

            return error * error;
        }) / firings.Count;

    private static string PerEpisode(int firings, int episodes) =>
        episodes == 0 ? "—" : Inv($"{(decimal)firings / episodes:F1}");

    private static string Rate(int part, int whole) =>
        whole == 0 ? "—" : Inv($"{(decimal)part / whole:P2}");

    private static string Names(List<string> names) =>
        names.Count == 0 ? "none" : string.Join(", ", names);

    private static void Line(StringBuilder report, string text) => report.AppendLine(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(string report)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "artifacts", "verify", "rehearsal.md"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report);
    }

    // ---- collected shapes ---------------------------------------------------

    private sealed record Firing(
        DateTime DecidedAtUtc,
        decimal Probability,
        decimal Drawdown,
        int Trials,
        int Successes,
        decimal Realised,
        bool Succeeded,
        bool ReachedTarget,
        bool StartsEpisode);

    private sealed class InstrumentResult
    {
        public InstrumentResult(string instrument) => Instrument = instrument;

        public string Instrument { get; }

        public int SessionsHeld { get; set; }

        public int DecisionPoints { get; set; }

        public int Eligible { get; set; }

        /// <summary>Sessions where the drawdown held but its episode was already open.</summary>
        public int Suppressed { get; set; }

        /// <summary>Runs of consecutive firing sessions, counted here rather than by the rule.</summary>
        public int Runs { get; set; }

        /// <summary>Decision points where the rule and the run count disagree.</summary>
        public int Disagreements { get; set; }

        public SeriesRefusal? WholeSeriesRefusal { get; set; }

        public string WholeSeriesExplanation { get; set; } = string.Empty;

        public Dictionary<PriceRecoveryRefusal, int> RuleRefusals { get; } = [];

        public Dictionary<SeriesRefusal, int> SeriesRefusals { get; } = [];

        public List<Firing> Firings { get; } = [];
    }
}
