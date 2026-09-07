using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
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
/// The authorised acquisition. Spends real requests, and can only spend the ones approved.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only stage in this repository that costs money.</strong> Everything it may
/// do is written down in <c>declarations/acquisition-eodhd-sample400.json</c> and enforced by
/// <see cref="AcquisitionAuthorization"/>: 355 named symbols, two endpoints, one window, 704
/// dispatches. Nothing here can widen that. A symbol outside the list, a third endpoint, a wider
/// window or a 705th request is refused by the authorisation before it reaches the gateway, and
/// the run stops rather than continuing past a refusal it did not expect.
/// </para>
/// <para>
/// <strong>It is written to be run once.</strong> The runner script skips it once the outcome
/// artefact exists, and a request whose fingerprint has already completed is suppressed here
/// before any authorisation is consumed - so a re-run costs nothing even if the guard is removed.
/// </para>
/// <para>
/// <strong>Every request goes through the ordinary path.</strong> <see cref="IDataAcquisition"/>
/// calls the ingestion gateway, which applies source admission, capability, the declared rate
/// limit and then the Action/Policy seam, and normalises only what was actually archived. Nothing
/// is fetched directly, and no failure can become an observation, because normalisation reads the
/// archive rather than the response.
/// </para>
/// </remarks>
public sealed class AcquisitionExecutionTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_ACQUISITION_EXECUTE";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";
    private const string DefaultSecurityKind = "Security";

    /// <summary>
    /// The gap the runner keeps between dispatches, against a declared 60 a minute.
    /// </summary>
    /// <remarks>
    /// The limiter never waits: a saturated window is recorded as a refused run, not a pause. So
    /// pacing is this runner's job, and it errs slow. Refusals cost no money but they cost the
    /// request its place in the plan, and a run that manufactures three hundred of them has to be
    /// understood before it can be repeated - which is worse than taking a few more minutes.
    /// </remarks>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// Consecutive failures that end the run rather than continuing through it.
    /// </summary>
    /// <remarks>
    /// Not a retry policy - nothing here retries. It is the point at which "this request failed"
    /// stops being a fact about one symbol and becomes a fact about the provider, and continuing
    /// would spend the rest of an authorisation discovering the same thing six hundred more times.
    /// </remarks>
    private const int MaxConsecutiveFailures = 10;

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public AcquisitionExecutionTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_authorised_704_are_acquired_and_nothing_else_is()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The authorised acquisition is off. Set {GateVariable}=1 to run it. It SPENDS PROVIDER "
            + "REQUESTS.");

        var watch = Stopwatch.StartNew();

        // ---- nothing moves until the authorisation and the universe agree --------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(
            IdentityResolution.IsSealedAs(Encoding.UTF8.GetString(manifestBefore), SealedFingerprint),
            "the manifest in this repository is not the sealed universe this authorisation is for");

        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        var members = await ReadyMembersAsync();

        Assert.True(members.Count == 355, Universe.Inv($"{members.Count} ready members, not 355"));
        Assert.True(
            authorization.Symbols.Count == members.Count,
            "the authorisation and the identity table disagree about how many symbols there are");

        foreach (var member in members)
        {
            Assert.Contains(member.Symbol, authorization.Symbols);
        }

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var acquisition = services.GetRequiredService<IDataAcquisition>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();

        // The request shape the six suppressed fingerprints were computed against. Taken from the
        // ledger rather than assumed, because if it were wrong those six would be re-fetched.
        var priorPriceRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        var results = new List<Attempt>(authorization.PlannedRequests);
        var stopped = (string?)null;
        var lastDispatchUtc = DateTime.MinValue;
        var consecutiveFailures = 0;

        // ---- prices first, then splits. Nothing else, in either order. -----------------------------

        foreach (var (source, category) in new[]
        {
            (PriceSource, DataCategory.MarketPrices),
            (ActionsSource, DataCategory.CorporateActions),
        })
        {
            foreach (var member in members)
            {
                if (stopped is not null)
                {
                    break;
                }

                var request = IngestionRequest.Create(
                    SourceId.Create(source),
                    category,
                    region,
                    IngestionSubject.Create(subjectKind, member.Symbol),
                    CorrelationId.Create(Universe.Inv($"acquire-{member.Ticker}-{category}")),
                    clock.UtcNow,
                    DateRange.Create(WindowStart, WindowEnd));

                var fingerprint = request.Fingerprint();

                // Suppression first, and before the authorisation is asked for anything. A request
                // the ledger has already satisfied will not leave the process, so charging it a
                // dispatch would spend an authorisation on a call nobody makes.
                if (await runStore.HasCompletedAsync(fingerprint))
                {
                    authorization.RecordSuppressed();

                    results.Add(Attempt.Suppressed(member, source, category.ToString(), fingerprint));
                    continue;
                }

                var permitted = authorization.TryConsume(
                    source,
                    member.Symbol,
                    DateOnly.FromDateTime(WindowStart),
                    DateOnly.FromDateTime(WindowEnd));

                if (!permitted.Allowed)
                {
                    // Not an outcome to work around. Either the ceiling is reached or the plan has
                    // drifted from what was approved, and both mean stop.
                    stopped = Universe.Inv($"{source} {member.Symbol}: {permitted.Reason}");
                    results.Add(Attempt.Unauthorised(member, source, category.ToString(), fingerprint, permitted.Reason!));
                    break;
                }

                await PaceAsync(lastDispatchUtc);
                lastDispatchUtc = DateTime.UtcNow;

                Attempt attempt;

                try
                {
                    var outcome = await acquisition.AcquireAsync(request);

                    attempt = Attempt.Dispatched(
                        member,
                        source,
                        category.ToString(),
                        fingerprint,
                        outcome.Run.Outcome.ToString(),
                        outcome.Run.Artifacts.Count,
                        outcome.Normalization?.PayloadsRead ?? 0,
                        outcome.ObservationsRecorded,
                        outcome.Normalization?.PayloadsQuarantined ?? 0,
                        outcome.Run.Reason,
                        outcome.Run.RefusalRuleId);
                }
#pragma warning disable CA1031 // Deliberate: an unhandled exception here would abort the run
                              // before the record of what has already been spent is written, which
                              // is the one outcome worse than a failed request. The failure is
                              // recorded as a failure - never as a success - and the consecutive
                              // counter below stops the run rather than spending the rest of the
                              // authorisation on a provider that is plainly not answering.
                catch (Exception exception)
                {
                    attempt = Attempt.Threw(
                        member, source, category.ToString(), fingerprint, exception.GetType().Name);
                }
#pragma warning restore CA1031

                results.Add(attempt);

                if (attempt.Succeeded)
                {
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    stopped = Universe.Inv(
                        $"{consecutiveFailures} consecutive requests failed, most recently {source} {member.Symbol}. Stopping rather than spending the remaining {authorization.Remaining} dispatches against a provider that is not answering.");
                    break;
                }
            }
        }

        // ---- what happened -------------------------------------------------------------------------

        var dispatched = results.Where(r => r.Kind == Attempt.DispatchedKind).ToList();
        var suppressed = results.Where(r => r.Kind == Attempt.SuppressedKind).ToList();

        var succeeded = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Succeeded));
        var partial = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.PartiallySucceeded));
        var failed = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Failed));
        var refused = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Refused));

        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();
        var runsAfter = await context.IngestionRuns.AsNoTracking().CountAsync();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "universe", "acquisition-outcome.json"),
            JsonSerializer.Serialize(
                new
                {
                    EvidenceBaseFingerprint = SealedFingerprint,
                    Authorization = authorization.AuthorizationId,
                    AuthorizationDigest = authorization.Digest,
                    AcquiredAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    WindowFromUtc = "2021-09-01",
                    WindowToUtc = "2026-08-31",
                    SubjectKind = subjectKind,
                    Region = region.Code,
                    Planned = authorization.PlannedRequests,
                    Suppressed = suppressed.Count,
                    Dispatched = dispatched.Count,
                    Succeeded = succeeded,
                    PartiallySucceeded = partial,
                    Failed = failed,
                    Refused = refused,
                    AuthorizationConsumed = authorization.Consumed,
                    AuthorizationRemaining = authorization.Remaining,
                    ObservationsBefore = observationsBefore,
                    ObservationsAfter = observationsAfter,
                    RunsBefore = runsBefore,
                    RunsAfter = runsAfter,
                    StoppedBecause = stopped,
                    Attempts = results,
                },
                Universe.Json) + "\n");

        var report = Compose(
            authorization, results, dispatched, suppressed,
            succeeded, partial, failed, refused,
            observationsBefore, observationsAfter, runsBefore, runsAfter, stopped, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "acquisition-outcome.md"),
            report);

        _output.WriteLine(report);

        // ---- the invariants that must hold whatever the provider did --------------------------------

        var manifestAfter = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(manifestBefore.SequenceEqual(manifestAfter), "the sealed manifest changed");

        // The ceiling, from both directions.
        Assert.True(
            authorization.Consumed <= authorization.DispatchCeiling,
            Universe.Inv($"{authorization.Consumed} dispatches consumed against a ceiling of {authorization.DispatchCeiling}"));

        Assert.True(
            dispatched.Count == authorization.Consumed,
            Universe.Inv($"{dispatched.Count} dispatches were made but {authorization.Consumed} were authorised"));

        // Suppression is free, and there were exactly the six the plan named.
        Assert.True(
            suppressed.Count == authorization.AlreadySatisfied,
            Universe.Inv($"{suppressed.Count} suppressed, and the authorisation names {authorization.AlreadySatisfied}"));

        // Every attempt was inside the authorised set: no other symbol, source or window appears.
        Assert.All(results, r => Assert.Contains(r.Symbol, authorization.Symbols));
        Assert.All(results, r => Assert.Contains(r.Source, authorization.Sources));

        // A refused or failed run archives nothing, so it cannot have produced an observation.
        Assert.DoesNotContain(dispatched, r => !r.Succeeded && r.ObservationsRecorded > 0);

        Assert.Null(stopped);
    }

    /// <summary>Waits until the declared quota has room, rather than being refused by it.</summary>
    private static async Task PaceAsync(DateTime lastDispatchUtc)
    {
        if (lastDispatchUtc == DateTime.MinValue)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - lastDispatchUtc;

        if (elapsed < MinimumInterval)
        {
            await Task.Delay(MinimumInterval - elapsed);
        }
    }

    private static async Task<List<Ready>> ReadyMembersAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True)
            .Select(m =>
            {
                var ticker = m.GetProperty("Ticker").GetString()!;

                return new Ready(
                    m.GetProperty("Cik").GetString() ?? string.Empty,
                    m.GetProperty("Name").GetString() ?? "(unnamed)",
                    ticker,
                    AcquisitionPlanning.SymbolFor(ticker));
            })
            .OrderBy(m => m.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    private static string Compose(
        AcquisitionAuthorization authorization,
        List<Attempt> results,
        List<Attempt> dispatched,
        List<Attempt> suppressed,
        int succeeded,
        int partial,
        int failed,
        int refused,
        int observationsBefore,
        int observationsAfter,
        int runsBefore,
        int runsAfter,
        string? stopped,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        report.AppendLine("# Authorised EODHD acquisition - outcome");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Sealed universe `{SealedFingerprint}`, byte-identical and not resealed."));
        report.AppendLine(Universe.Inv($"Authorisation `{authorization.AuthorizationId}`, digest `{authorization.Digest}`."));
        report.AppendLine();

        report.AppendLine("## Provider accounting");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Planned | {authorization.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Suppressed by the ledger | {suppressed.Count} |"));
        report.AppendLine(Universe.Inv($"| **Dispatched** | **{dispatched.Count}** |"));
        report.AppendLine(Universe.Inv($"| …succeeded | {succeeded} |"));
        report.AppendLine(Universe.Inv($"| …partially succeeded | {partial} |"));
        report.AppendLine(Universe.Inv($"| …failed | {failed} |"));
        report.AppendLine(Universe.Inv($"| …refused before the network | {refused} |"));
        report.AppendLine(Universe.Inv($"| Authorisation consumed | **{authorization.Consumed}** of {authorization.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| Authorisation remaining | {authorization.Remaining} |"));
        report.AppendLine(Universe.Inv($"| Price requests | {results.Count(r => r.Source == PriceSource && r.Kind == Attempt.DispatchedKind)} dispatched |"));
        report.AppendLine(Universe.Inv($"| Split requests | {results.Count(r => r.Source == ActionsSource && r.Kind == Attempt.DispatchedKind)} dispatched |"));
        report.AppendLine(Universe.Inv($"| Dividend requests | 0 - no source exists and none is authorised |"));
        report.AppendLine(Universe.Inv($"| SEC requests | 0 - the connector was not enabled |"));
        report.AppendLine();

        report.AppendLine("## What reached the store");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Observations before | {observationsBefore} |"));
        report.AppendLine(Universe.Inv($"| Observations after | {observationsAfter} |"));
        report.AppendLine(Universe.Inv($"| **Added** | **{observationsAfter - observationsBefore}** |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs before | {runsBefore} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs after | {runsAfter} |"));
        report.AppendLine(Universe.Inv($"| Payloads quarantined | {dispatched.Sum(r => r.PayloadsQuarantined)} |"));
        report.AppendLine();

        if (stopped is not null)
        {
            report.AppendLine("## The run stopped");
            report.AppendLine();
            report.AppendLine(Universe.Inv($"**{stopped}**"));
            report.AppendLine();
            report.AppendLine("Stopping rather than improvising is the instruction and the design. What");
            report.AppendLine("was fetched before the stop is in the ledger and the archive; nothing was");
            report.AppendLine("discarded and nothing was invented.");
            report.AppendLine();
        }

        var bad = dispatched
            .Where(r => r.Outcome is not (nameof(IngestionOutcome.Succeeded) or nameof(IngestionOutcome.PartiallySucceeded)))
            .ToList();

        report.AppendLine("## Every dispatch that did not succeed");
        report.AppendLine();

        if (bad.Count == 0)
        {
            report.AppendLine("None. Every dispatched request returned a payload that was archived.");
        }
        else
        {
            report.AppendLine("| Symbol | Source | Outcome | Rule | Reason |");
            report.AppendLine("| --- | --- | --- | --- | --- |");

            foreach (var row in bad)
            {
                report.AppendLine(Universe.Inv(
                    $"| `{row.Symbol}` | `{row.Source}` | {row.Outcome} | {row.RefusalRuleId ?? "-"} | {Trim(row.Reason)} |"));
            }
        }

        report.AppendLine();

        var empty = dispatched
            .Where(r => r.Outcome == nameof(IngestionOutcome.Succeeded) && r.ObservationsRecorded == 0)
            .ToList();

        report.AppendLine(Universe.Inv(
            $"Succeeded but recorded no observation: **{empty.Count}**. For `{ActionsSource}` this is ordinary - a company with no split in five years has nothing to record - and for `{PriceSource}` it is not, and each one is listed in the JSON."));
        report.AppendLine();

        report.AppendLine(Universe.Inv(
            $"The full attempt-by-attempt record is at `artifacts/universe/acquisition-outcome.json`. Elapsed {watch.Elapsed.TotalMinutes:F1} minutes."));

        return report.ToString();
    }

    private static string Trim(string? reason) =>
        reason is null ? "-" : reason.Length <= 120 ? reason : reason[..120] + "…";

    private sealed record Ready(string Cik, string Name, string Ticker, string Symbol);

    private sealed record Attempt(
        string Kind,
        string Cik,
        string Name,
        string Symbol,
        string Source,
        string Category,
        string Fingerprint,
        string? Outcome,
        int Artifacts,
        int PayloadsRead,
        int ObservationsRecorded,
        int PayloadsQuarantined,
        string? Reason,
        string? RefusalRuleId)
    {
        public const string DispatchedKind = "dispatched";
        public const string SuppressedKind = "suppressed";
        public const string UnauthorisedKind = "unauthorised";

        public static Attempt Suppressed(Ready member, string source, string category, string fingerprint) =>
            new(SuppressedKind, member.Cik, member.Name, member.Symbol, source, category, fingerprint,
                null, 0, 0, 0, 0, "already completed in the ledger", null);

        public static Attempt Unauthorised(
            Ready member, string source, string category, string fingerprint, string reason) =>
            new(UnauthorisedKind, member.Cik, member.Name, member.Symbol, source, category, fingerprint,
                null, 0, 0, 0, 0, reason, null);

        /// <summary>A dispatch that came back with something the archive could keep.</summary>
        public bool Succeeded =>
            Outcome is nameof(IngestionOutcome.Succeeded) or nameof(IngestionOutcome.PartiallySucceeded);

        public static Attempt Threw(
            Ready member, string source, string category, string fingerprint, string exceptionType) =>
            new(DispatchedKind, member.Cik, member.Name, member.Symbol, source, category, fingerprint,
                nameof(IngestionOutcome.Failed), 0, 0, 0, 0,
                $"{exceptionType} escaped the gateway", null);

        public static Attempt Dispatched(
            Ready member,
            string source,
            string category,
            string fingerprint,
            string outcome,
            int artifacts,
            int payloadsRead,
            int observations,
            int quarantined,
            string? reason,
            string? refusalRuleId) =>
            new(DispatchedKind, member.Cik, member.Name, member.Symbol, source, category, fingerprint,
                outcome, artifacts, payloadsRead, observations, quarantined, reason, refusalRuleId);
    }
}
