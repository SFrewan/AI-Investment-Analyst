using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// One batch of the corporate-actions acquisition. Prepared, and unable to run.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One batch at a time, and only the one approved.</strong> A cut partition is a plan, not
/// a permission: <c>Authorised</c> is a separate approval per batch, and the runner refuses any
/// batch that does not carry it before it reads the ledger, let alone before it reaches a provider.
/// Batch 2 is approved; batch 1 is spent and batches 3 and 4 are not approved, and the readiness
/// tests fail the build if any of them is flipped.
/// </para>
/// <para>
/// <strong>Why a second runner rather than a parameter on the first.</strong> The price runner has
/// no corporate-actions path at all, and that is a guarantee worth keeping: it cannot acquire a
/// split however it is configured. Giving it a source parameter would trade that structural
/// guarantee for a flag. This class is the mirror image - it names
/// <see cref="SplitSource"/> and <see cref="DataCategory.CorporateActions"/> and nothing else, has
/// no price loop, no dividend loop, no SEC loop and no benchmark loop, and so cannot acquire a
/// price however it is configured.
/// </para>
/// <para>
/// <strong>Everything else is the price runner's architecture, unchanged.</strong> Work is resolved
/// from the live ledger; suppression is by fingerprint; correlations name the attempt through
/// <see cref="AcquisitionCorrelation"/>, so a transport failure cannot strand a symbol the way it
/// stranded <c>NXST.US</c>; the expected authorisation consumption is pinned before dispatch and a
/// ledger that has moved refuses the run; the batch cap and the ceiling both bind; ten consecutive
/// failures end the batch; the transport diagnostic chain is preserved verbatim; and the sealed
/// manifest is checked before and after.
/// </para>
/// </remarks>
public sealed class AcquisitionSplitBatchTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SPLIT_BATCH";

    /// <summary>Which split batch to run. Deliberately has no default.</summary>
    private const string IndexVariable = "AIINV_SPLIT_BATCH_INDEX";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    /// <summary>The only source this runner names. There is no other, and no price source.</summary>
    private const string SplitSource = "eodhd-splits";

    private const string DefaultSecurityKind = "Security";

    /// <summary>
    /// The declaration that authorises corporate actions, named here and per batch.
    /// </summary>
    /// <remarks>
    /// Twice on purpose. Naming it on the batch means a split batch cannot be run against the price
    /// authorisation by editing one line; naming it here means the two have to agree, and the
    /// readiness tests check that they do.
    /// </remarks>
    private const string SplitDeclaration = "acquisition-eodhd-splits-final-2021-09-to-2026-08.json";

    /// <summary>The most requests any split batch may dispatch, whatever it declares.</summary>
    private const int MaxBatchSize = 70;

    /// <summary>Consecutive failures that end the batch rather than continuing through them.</summary>
    private const int MaxConsecutiveFailures = 10;

    /// <summary>The gap kept between dispatches, against a declared sixty a minute.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1100);

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The split partition, re-cut against the 73 outstanding fingerprints. Every batch unauthorised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boundaries carry forward from the partition cut against the live ledger for the previous
    /// authorisation, less the work that has since completed, and are written down here so that a
    /// batch means the same set of symbols every time it is named. Not one of them is authorised:
    /// <c>Authorised</c> is a separate approval per batch, and
    /// <see cref="AcquisitionSplitRunnerReadinessTests"/> fails the build if any is flipped
    /// without the partition being re-cut against an approved authorisation.
    /// </para>
    /// <para>
    /// <strong>Batch 1 is one symbol, and that is the point.</strong> <c>MYO.US</c> failed in the
    /// second batch of the previous partition, and because authorisation is charged at intent the
    /// unit it spent was never returned - leaving the ledger owing 73 requests against a ceiling
    /// that could pay for 72. This is the second time the shortfall has taken this shape, the first
    /// being <c>GPC.US</c> one authorisation earlier. The successor is sized to the 73, and
    /// recovering <c>MYO.US</c> first returns the outstanding count to 72 before anything else
    /// moves, which is why the two batches after it keep the boundaries and the outstanding-before
    /// figures they were already approved with.
    /// </para>
    /// <para>
    /// <c>ExpectedOutstandingBefore</c> steps down by each batch's own size because each batch is
    /// expected to complete before the next is approved. It is a projection, and deliberately a
    /// brittle one: a batch that partially failed leaves a different number and the following batch
    /// refuses to run rather than quietly dispatching a set nobody approved. That brittleness is
    /// what caught this shortfall, twice.
    /// </para>
    /// <para>
    /// <c>ExpectedPriorConsumption</c> starts at zero and steps up alongside it. What earlier
    /// authorisations spent belongs to them and is carried in this one as evidence, not charged
    /// against its ceiling of 73, so the first batch expects nothing spent; each later batch
    /// expects exactly what the batches before it spent, which is the ceiling less what the ledger
    /// still owes. The two columns are pinned to each other in the readiness tests so neither can
    /// be nudged on its own.
    /// </para>
    /// </remarks>
    internal static readonly SplitBatchDefinition[] Partition =
    [
        new(1, "MYO.US", "MYO.US", 1, 73, 0, SplitDeclaration, Authorised: false),
        new(2, "SAMG.US", "ZS.US", 70, 72, 1, SplitDeclaration, Authorised: false),
        new(3, "ZWS.US", "ZYME.US", 2, 2, 71, SplitDeclaration, Authorised: false),
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public AcquisitionSplitBatchTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task One_approved_batch_of_corporate_actions_is_acquired_and_nothing_else_is()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The batched split acquisition is off. Set {GateVariable}=1 and {IndexVariable} to a "
            + "batch number to run it. It SPENDS PROVIDER REQUESTS.");

        var watch = Stopwatch.StartNew();

        // ---- which batch, stated rather than assumed -------------------------------------------------

        var requested = Environment.GetEnvironmentVariable(IndexVariable);

        Assert.True(
            int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index),
            Universe.Inv($"{IndexVariable} is '{requested ?? "(unset)"}', which is not a batch number. There is no default: a run that does not say which batch it is must not pick one."));

        var batch = Array.Find(Partition, b => b.Index == index);

        Assert.True(
            batch is not null,
            Universe.Inv($"split batch {index} is not in the approved partition, which has {Partition.Length} batches. The partition is cut when a superseding authorisation for the split work is approved, and not before."));

        Assert.True(
            batch!.Authorised,
            Universe.Inv($"split batch {batch.Index} (`{batch.First}`..`{batch.Last}`, {batch.Count} symbols) is defined but NOT authorised to run. Each batch is approved separately. Nothing was dispatched."));

        // ---- nothing moves until the universe and the authorisation agree -----------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(
            IdentityResolution.IsSealedAs(Encoding.UTF8.GetString(manifestBefore), SealedFingerprint),
            "the manifest in this repository is not the sealed universe this authorisation is for");

        Assert.Equal(
            SealedDigest,
            Convert.ToHexString(SHA256.HashData(manifestBefore)).ToLowerInvariant());

        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", batch.Declaration),
            SealedFingerprint);

        Assert.Equal(DateOnly.FromDateTime(WindowStart), authorization.WindowFrom);
        Assert.Equal(DateOnly.FromDateTime(WindowEnd), authorization.WindowTo);
        Assert.Contains(SplitSource, authorization.Sources);

        var members = await ReadyMembersAsync();

        Assert.True(members.Count == 355, Universe.Inv($"{members.Count} ready members, not 355"));

        // ---- what earlier processes already spent, charged before anything is dispatched --------------

        // Scoped to the authorisation this batch names, and to that authorisation's own identifier
        // as the loaded declaration states it - never a literal written here. An artefact left by a
        // superseded authorisation is history, not this ceiling's spending.
        var prior = await PriorConsumptionAsync(batch.Index, authorization.AuthorizationId);

        Assert.True(
            prior.Total == batch.ExpectedPriorConsumption,
            Universe.Inv($"{prior.Total} dispatches have been consumed against this authorisation, and split batch {batch.Index} was approved against {batch.ExpectedPriorConsumption}. The accounting has moved since the approval; nothing was dispatched."));

        _output.WriteLine(Universe.Inv($"prior consumption under `{authorization.AuthorizationId}`: {prior.Total} dispatch(es) from {prior.Counted} artefact(s); {prior.Skipped} artefact(s) charged a superseded authorisation and were not counted."));

        authorization.RecordPriorConsumption(prior.Total);

        var attemptNumber = prior.Attempts + 1;
        var cap = batch.Count;

        Assert.True(
            cap <= MaxBatchSize,
            Universe.Inv($"split batch {batch.Index} declares {cap} symbols against a hard maximum of {MaxBatchSize}"));

        Assert.True(
            authorization.Remaining >= cap,
            Universe.Inv($"{authorization.Remaining} dispatches remain against a batch of {cap}. Nothing was dispatched."));

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var acquisition = services.GetRequiredService<IDataAcquisition>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();

        // The request shape the completed fingerprints were computed against, taken from the ledger
        // rather than assumed: a different subject kind or region is a different fingerprint.
        var priorRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.CorporateActions)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorRun?.Request.Region ?? Region.Global;

        // ---- the batch, resolved from the live ledger and checked against what was approved -----------

        var outstanding = new List<string>();

        foreach (var member in members)
        {
            var probe = BuildRequest(
                member.Symbol, subjectKind, region, clock, batch.Index, attemptNumber, "probe");

            if (!await runStore.HasCompletedAsync(probe.Fingerprint()))
            {
                outstanding.Add(member.Symbol);
            }
        }

        Assert.True(
            outstanding.Count == batch.ExpectedOutstandingBefore,
            Universe.Inv($"the ledger owes {outstanding.Count} split requests and split batch {batch.Index} was approved against {batch.ExpectedOutstandingBefore}. Nothing was dispatched."));

        var slice = outstanding
            .Where(s =>
                string.CompareOrdinal(s, batch.First) >= 0 &&
                string.CompareOrdinal(s, batch.Last) <= 0)
            .ToList();

        Assert.True(
            slice.Count == batch.Count,
            Universe.Inv($"split batch {batch.Index} resolves to {slice.Count} outstanding symbols, and {batch.Count} were approved. Nothing was dispatched."));

        Assert.Equal(batch.First, slice[0]);
        Assert.Equal(batch.Last, slice[^1]);

        foreach (var symbol in slice)
        {
            var covered = authorization.Covers(
                SplitSource,
                symbol,
                DateOnly.FromDateTime(WindowStart),
                DateOnly.FromDateTime(WindowEnd));

            Assert.True(covered.Allowed, Universe.Inv($"`{symbol}`: {covered.Reason}"));
        }

        Assert.Equal(0, authorization.Consumed - prior.Total);

        var byMember = members.ToDictionary(m => m.Symbol, StringComparer.Ordinal);

        var outsideBatch = outstanding
            .Where(s => !slice.Contains(s, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // ---- the attempt is new, and it is new about the same work -------------------------------------

        foreach (var symbol in slice)
        {
            var ticker = byMember[symbol].Ticker;

            var correlation = AcquisitionCorrelation
                .Text(batch.Index, attemptNumber, ticker, DataCategory.CorporateActions);

            var burned = new HashSet<string>(StringComparer.Ordinal)
            {
                Universe.Inv($"batch-{AcquisitionCorrelation.Safe(ticker)}-CorporateActions"),
                Universe.Inv($"batch-{AcquisitionCorrelation.Safe(ticker)}-MarketPrices"),
            };

            foreach (var (priorBatch, priorAttempt) in prior.Recorded)
            {
                burned.Add(AcquisitionCorrelation
                    .Text(priorBatch, priorAttempt, ticker, DataCategory.CorporateActions));
            }

            Assert.True(
                !burned.Contains(correlation),
                Universe.Inv($"`{symbol}` would carry `{correlation}`, which an earlier attempt already claimed. Nothing was dispatched."));

            if (prior.Fingerprints.TryGetValue(symbol, out var before))
            {
                var now = BuildRequest(
                    symbol, subjectKind, region, clock, batch.Index, attemptNumber, ticker)
                    .Fingerprint();

                Assert.True(
                    string.Equals(before, now, StringComparison.Ordinal),
                    Universe.Inv($"`{symbol}` hashed to {before} on an earlier attempt and to {now} now. The work has changed, not just the attempt. Nothing was dispatched."));
            }
        }

        // ---- the batch itself ----------------------------------------------------------------------------

        var results = new List<SplitAttempt>(slice.Count);
        var stopped = (string?)null;
        var lastDispatchUtc = DateTime.MinValue;
        var consecutiveFailures = 0;
        var dispatchedHere = 0;

        foreach (var symbol in slice)
        {
            if (stopped is not null)
            {
                break;
            }

            var member = byMember[symbol];

            var request = BuildRequest(
                symbol, subjectKind, region, clock, batch.Index, attemptNumber, member.Ticker);

            var fingerprint = request.Fingerprint();

            if (await runStore.HasCompletedAsync(fingerprint))
            {
                authorization.RecordSuppressed();
                results.Add(SplitAttempt.Suppressed(member, fingerprint));
                continue;
            }

            if (dispatchedHere >= cap)
            {
                stopped = Universe.Inv($"the batch cap of {cap} dispatches is reached at `{symbol}`.");
                break;
            }

            var permitted = authorization.TryConsume(
                SplitSource,
                symbol,
                DateOnly.FromDateTime(WindowStart),
                DateOnly.FromDateTime(WindowEnd));

            if (!permitted.Allowed)
            {
                stopped = Universe.Inv($"{SplitSource} {symbol}: {permitted.Reason}");
                results.Add(SplitAttempt.Unauthorised(member, fingerprint, permitted.Reason!));
                break;
            }

            await PaceAsync(lastDispatchUtc);
            lastDispatchUtc = DateTime.UtcNow;
            dispatchedHere++;

            SplitAttempt attempt;

            try
            {
                var outcome = await acquisition.AcquireAsync(request);

                attempt = SplitAttempt.Dispatched(
                    member,
                    fingerprint,
                    outcome.Run.Outcome.ToString(),
                    outcome.ObservationsRecorded,
                    outcome.Normalization?.PayloadsQuarantined ?? 0,
                    outcome.Run.Reason,
                    outcome.Run.RefusalRuleId,
                    DiagnosticIn(outcome.Run.Reason));
            }
#pragma warning disable CA1031 // Deliberate: an unhandled exception here would abort the batch
                              // before the record of what has already been spent is written, which
                              // is the one outcome worse than a failed request.
            catch (Exception exception)
            {
                attempt = SplitAttempt.Threw(
                    member, fingerprint, exception.GetType().Name, DiagnosticOf(exception));
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
                    $"{consecutiveFailures} consecutive requests failed, most recently {SplitSource} {symbol}. Stopping rather than spending the rest of this batch against a transport that is not answering.");
                break;
            }
        }

        // ---- what happened -------------------------------------------------------------------------------

        var dispatched = results.Where(r => r.Kind == SplitAttempt.DispatchedKind).ToList();
        var suppressed = results.Where(r => r.Kind == SplitAttempt.SuppressedKind).ToList();

        var succeeded = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Succeeded));
        var failed = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Failed));
        var refused = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Refused));

        var observationsAfter = await context.Observations.AsNoTracking().CountAsync();
        var runsAfter = await context.IngestionRuns.AsNoTracking().CountAsync();
        var consumedHere = authorization.Consumed - prior.Total;

        var diagnostics = dispatched
            .Where(r => !r.Succeeded)
            .GroupBy(r => r.Diagnostic, StringComparer.Ordinal)
            .Select(g => new { Token = g.Key, Requests = g.Count() })
            .OrderByDescending(g => g.Requests)
            .ThenBy(g => g.Token, StringComparer.Ordinal)
            .ToList();

        var attemptPath = Universe.RepositoryPath(
            "artifacts", "universe", Universe.Inv($"acquisition-splits-{batch.Index:00}-attempt-{attemptNumber:00}.json"));

        await Universe.WriteAsync(
            attemptPath,
            JsonSerializer.Serialize(
                new
                {
                    EvidenceBaseFingerprint = SealedFingerprint,
                    Authorization = authorization.AuthorizationId,
                    AuthorizationDigest = authorization.Digest,
                    BatchIndex = batch.Index,
                    BatchFirst = batch.First,
                    BatchLast = batch.Last,
                    BatchSymbols = slice,
                    Source = SplitSource,
                    AcquiredAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    WindowFromUtc = "2021-09-01",
                    WindowToUtc = "2026-08-31",
                    SubjectKind = subjectKind,
                    Region = region.Code,
                    OutstandingSplitsBefore = outstanding.Count,
                    Suppressed = suppressed.Count,
                    Dispatched = dispatched.Count,
                    Succeeded = succeeded,
                    Failed = failed,
                    Refused = refused,
                    AuthorizationConsumedThisRun = consumedHere,
                    AuthorizationConsumedBefore = prior.Total,
                    AuthorizationConsumedTotal = authorization.Consumed,
                    AuthorizationRemaining = authorization.Remaining,
                    ObservationsBefore = observationsBefore,
                    ObservationsAfter = observationsAfter,
                    RunsBefore = runsBefore,
                    RunsAfter = runsAfter,
                    StoppedBecause = stopped,
                    TransportDiagnostics = diagnostics,
                    Attempts = results,
                },
                Universe.Json) + "\n");

        var report = new StringBuilder();

        report.AppendLine(Universe.Inv($"# Split batch {batch.Index}"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"`{SplitSource}` only, {slice.Count} symbols `{batch.First}`..`{batch.Last}`, 2021-09-01..2026-08-31."));
        report.AppendLine(Universe.Inv($"Authorisation `{authorization.AuthorizationId}`, digest `{authorization.Digest}`, unamended."));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Outstanding split requests before this batch | {outstanding.Count} |"));
        report.AppendLine(Universe.Inv($"| In this batch | {slice.Count} |"));
        report.AppendLine(Universe.Inv($"| Suppressed by the ledger | {suppressed.Count} |"));
        report.AppendLine(Universe.Inv($"| **Dispatched** | **{dispatched.Count}** |"));
        report.AppendLine(Universe.Inv($"| …succeeded | {succeeded} |"));
        report.AppendLine(Universe.Inv($"| …failed | {failed} |"));
        report.AppendLine(Universe.Inv($"| …refused before the network | {refused} |"));
        report.AppendLine(Universe.Inv($"| Batch cap | {batch.Count} of a hard maximum of {MaxBatchSize} |"));
        report.AppendLine(Universe.Inv($"| **Authorisation consumed by this batch** | **{consumedHere}** |"));
        report.AppendLine(Universe.Inv($"| Authorisation consumed in total | {authorization.Consumed} of {authorization.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| Authorisation remaining | {authorization.Remaining} |"));
        report.AppendLine(Universe.Inv($"| Price requests | 0 - this runner has no market-prices path |"));
        report.AppendLine(Universe.Inv($"| Dividend requests | 0 - no source exists and none is authorised |"));
        report.AppendLine("| SEC requests | 0 - the connector was not enabled |");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"| Observations before / after | {observationsBefore} / {observationsAfter} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs before / after | {runsBefore} / {runsAfter} |"));
        report.AppendLine();

        if (diagnostics.Count > 0)
        {
            report.AppendLine("| Transport diagnostic | Requests |");
            report.AppendLine("| --- | ---: |");

            foreach (var token in diagnostics)
            {
                report.AppendLine(Universe.Inv($"| `{token.Token}` | {token.Requests} |"));
            }

            report.AppendLine();
        }

        if (stopped is not null)
        {
            report.AppendLine(Universe.Inv($"**The batch stopped: {stopped}**"));
            report.AppendLine();
        }

        report.AppendLine(Universe.Inv(
            $"The attempt-by-attempt record is at `artifacts/universe/{Path.GetFileName(attemptPath)}`. Elapsed {watch.Elapsed.TotalMinutes:F1} minutes."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"**No further batch runs automatically.** Split batch {batch.Index + 1} requires its own approval."));

        await Universe.WriteAsync(
            Universe.RepositoryPath(
                "artifacts", "verify", Universe.Inv($"acquisition-splits-{batch.Index:00}.md")),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants that must hold whatever the provider did ---------------------------------------

        Assert.True(
            manifestBefore.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)),
            "the sealed manifest changed");

        Assert.True(
            consumedHere <= cap,
            Universe.Inv($"{consumedHere} dispatches were consumed against a batch cap of {cap}"));

        Assert.True(
            authorization.Consumed <= authorization.DispatchCeiling,
            Universe.Inv($"{authorization.Consumed} dispatches consumed against a ceiling of {authorization.DispatchCeiling}"));

        Assert.True(
            dispatched.Count == consumedHere,
            Universe.Inv($"{dispatched.Count} dispatches were made but {consumedHere} were consumed"));

        // Corporate actions only. There is no price loop in this class, and this says so from the record.
        Assert.DoesNotContain(results, r => r.Source != SplitSource);
        Assert.All(results, r => Assert.Contains(r.Symbol, authorization.Symbols));
        Assert.All(results, r => Assert.Contains(r.Symbol, slice));
        Assert.DoesNotContain(results, r => outsideBatch.Contains(r.Symbol));

        Assert.DoesNotContain(dispatched, r => !r.Succeeded && r.ObservationsRecorded > 0);

        Assert.True(
            runsAfter - runsBefore == dispatched.Count,
            Universe.Inv($"{runsAfter - runsBefore} ingestion runs appeared for {dispatched.Count} dispatches"));

        Assert.Null(stopped);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>Builds one corporate-actions request, and can build nothing else.</summary>
    private static IngestionRequest BuildRequest(
        string symbol,
        string subjectKind,
        Region region,
        IClock clock,
        int batchIndex,
        int attempt,
        string ticker) =>
        IngestionRequest.Create(
            SourceId.Create(SplitSource),
            DataCategory.CorporateActions,
            region,
            IngestionSubject.Create(subjectKind, symbol),
            AcquisitionCorrelation.For(batchIndex, attempt, ticker, DataCategory.CorporateActions),
            clock.UtcNow,
            DateRange.Create(WindowStart, WindowEnd));

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

    private const string NoDiagnostic = "(none)";

    private static string DiagnosticIn(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return NoDiagnostic;
        }

        for (var i = 0; i < reason.Length; i++)
        {
            if (reason[i] != '[')
            {
                continue;
            }

            var close = reason.IndexOf(']', i + 1);

            if (close < 0)
            {
                break;
            }

            var token = reason[(i + 1)..close];

            if (IsTransportToken(token))
            {
                return token;
            }

            i = close;
        }

        return NoDiagnostic;
    }

    private static string DiagnosticOf(Exception exception)
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
        {
            if (current is ITransportDiagnostic diagnostic)
            {
                return diagnostic.TransportDiagnostic;
            }
        }

        return NoDiagnostic;
    }

    private static bool IsTransportToken(string token) =>
        token.StartsWith("socket:", StringComparison.Ordinal)
        || token.StartsWith("http:", StringComparison.Ordinal)
        || token.StartsWith("status:", StringComparison.Ordinal)
        || string.Equals(token, ProviderTransportException.Unclassified, StringComparison.Ordinal);

    /// <summary>The glob every split batch artefact answers to.</summary>
    internal const string ArtefactPattern = "acquisition-splits-*.json";

    /// <summary>The artefact field naming the authorisation a run charged.</summary>
    internal const string AuthorizationProperty = "Authorization";

    /// <summary>And the field holding what it charged.</summary>
    internal const string ConsumedProperty = "AuthorizationConsumedThisRun";

    /// <summary>
    /// The authorisation an artefact says it charged, or null when it does not say.
    /// </summary>
    /// <remarks>
    /// Null and "some other authorisation" are different answers and the callers treat them
    /// differently: the second is history, the first is a malformed record.
    /// </remarks>
    internal static string? AuthorizationOf(JsonElement root) =>
        root.TryGetProperty(AuthorizationProperty, out var named) &&
        named.ValueKind == JsonValueKind.String
            ? named.GetString()
            : null;

    /// <summary>
    /// What one named authorisation has spent, from the artefacts that say they charged it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The successor to a spent authorisation starts at zero, and the artefacts its predecessor
    /// left behind are evidence of a closed account rather than spending against the new ceiling.
    /// Totalling every artefact in the directory conflated the two: after the split authorisation
    /// was superseded, 210 units belonging to it would have been charged against a successor whose
    /// first batch correctly expects nothing spent, and that batch would have been refused before
    /// it reached a provider.
    /// </para>
    /// <para>
    /// Nothing here rewrites, moves or reinterprets an artefact. The <c>Authorization</c> field has
    /// been written by every split run since the first, so this only reads what was already
    /// recorded - and an artefact that does not carry it is refused rather than ignored.
    /// </para>
    /// </remarks>
    internal static async Task<int> ConsumedUnderAsync(string directory, string authorizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);

        var total = 0;

        foreach (var path in Directory
            .EnumerateFiles(directory, ArtefactPattern)
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            var named = AuthorizationOf(document.RootElement);

            if (string.IsNullOrWhiteSpace(named))
            {
                throw new InvalidOperationException(
                    $"`{Path.GetFileName(path)}` does not name the authorisation it charged. " +
                    "Prior consumption cannot be attributed to an authorisation from it.");
            }

            if (string.Equals(named, authorizationId, StringComparison.Ordinal))
            {
                total += document.RootElement.GetProperty(ConsumedProperty).GetInt32();
            }
        }

        return total;
    }

    /// <summary>Every authorisation the artefacts in a directory say they charged.</summary>
    internal static async Task<IReadOnlyList<string>> AuthorizationsInAsync(string directory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(directory, ArtefactPattern))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            var named = AuthorizationOf(document.RootElement);

            if (!string.IsNullOrWhiteSpace(named))
            {
                seen.Add(named);
            }
        }

        return seen.Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<Prior> PriorConsumptionAsync(int index, string authorizationId)
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var total = 0;
        var counted = 0;
        var skipped = 0;
        var attempts = 0;
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var recorded = new List<(int Batch, int Attempt)>();

        var mine = Universe.Inv($"acquisition-splits-{index:00}-attempt-");

        foreach (var path in Directory
            .EnumerateFiles(universe, ArtefactPattern)
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            // Spending is charged to the authorisation the artefact says it was charged to. An
            // artefact that names a different one belongs to a closed account and is skipped; one
            // that names none at all is malformed and stops the run rather than being invisible,
            // because "not counted" and "not seen" must not look the same to the pin below.
            var named = AuthorizationOf(document.RootElement);

            Assert.True(
                !string.IsNullOrWhiteSpace(named),
                Universe.Inv($"`{Path.GetFileName(path)}` does not name the authorisation it charged. Prior consumption cannot be attributed and nothing was dispatched."));

            if (string.Equals(named, authorizationId, StringComparison.Ordinal))
            {
                total += document.RootElement.GetProperty(ConsumedProperty).GetInt32();
                counted++;
            }
            else
            {
                skipped++;
            }

            var parts = Path.GetFileNameWithoutExtension(path).Split('-');

            if (parts.Length == 5 &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) &&
                int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
            {
                recorded.Add((b, a));
            }

            if (!Path.GetFileName(path).StartsWith(mine, StringComparison.Ordinal))
            {
                continue;
            }

            attempts++;

            foreach (var entry in document.RootElement.GetProperty("Attempts").EnumerateArray())
            {
                var symbol = entry.GetProperty(nameof(SplitAttempt.Symbol)).GetString();
                var fingerprint = entry.GetProperty(nameof(SplitAttempt.Fingerprint)).GetString();

                if (symbol is not null && fingerprint is not null)
                {
                    fingerprints[symbol] = fingerprint;
                }
            }
        }

        return new Prior(total, attempts, fingerprints, recorded, counted, skipped);
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

    /// <param name="Index">The batch number an operator names.</param>
    /// <param name="First">The first symbol in the batch, ordinal.</param>
    /// <param name="Last">The last symbol in the batch, ordinal.</param>
    /// <param name="Count">How many outstanding symbols the batch was cut to hold.</param>
    /// <param name="ExpectedOutstandingBefore">What the ledger must still owe when it runs.</param>
    /// <param name="ExpectedPriorConsumption">What must already have been spent when it runs.</param>
    /// <param name="Declaration">
    /// The authorisation file in <c>declarations/</c> that covers it. Named per batch so a split
    /// batch cannot be run against the price authorisation by accident.
    /// </param>
    /// <param name="Authorised">Whether this batch has been approved to run.</param>
    internal sealed record SplitBatchDefinition(
        int Index,
        string First,
        string Last,
        int Count,
        int ExpectedOutstandingBefore,
        int ExpectedPriorConsumption,
        string Declaration,
        bool Authorised);

    private sealed record Prior(
        int Total,
        int Attempts,
        IReadOnlyDictionary<string, string> Fingerprints,
        IReadOnlyList<(int Batch, int Attempt)> Recorded,
        int Counted,
        int Skipped);

    private sealed record Ready(string Cik, string Name, string Ticker, string Symbol);

    private sealed record SplitAttempt(
        string Kind,
        string Cik,
        string Name,
        string Symbol,
        string Source,
        string Fingerprint,
        string? Outcome,
        int ObservationsRecorded,
        int PayloadsQuarantined,
        string? Reason,
        string? RefusalRuleId,
        string Diagnostic)
    {
        public const string DispatchedKind = "dispatched";
        public const string SuppressedKind = "suppressed";
        public const string UnauthorisedKind = "unauthorised";

        public bool Succeeded =>
            Outcome is nameof(IngestionOutcome.Succeeded) or nameof(IngestionOutcome.PartiallySucceeded);

        public static SplitAttempt Suppressed(Ready member, string fingerprint) =>
            new(SuppressedKind, member.Cik, member.Name, member.Symbol, SplitSource, fingerprint,
                null, 0, 0, "already completed in the ledger", null, NoDiagnostic);

        public static SplitAttempt Unauthorised(Ready member, string fingerprint, string reason) =>
            new(UnauthorisedKind, member.Cik, member.Name, member.Symbol, SplitSource, fingerprint,
                null, 0, 0, reason, null, NoDiagnostic);

        public static SplitAttempt Threw(
            Ready member, string fingerprint, string exceptionType, string diagnostic) =>
            new(DispatchedKind, member.Cik, member.Name, member.Symbol, SplitSource, fingerprint,
                nameof(IngestionOutcome.Failed), 0, 0,
                $"{exceptionType} escaped the gateway", null, diagnostic);

        public static SplitAttempt Dispatched(
            Ready member,
            string fingerprint,
            string outcome,
            int observations,
            int quarantined,
            string? reason,
            string? refusalRuleId,
            string diagnostic) =>
            new(DispatchedKind, member.Cik, member.Name, member.Symbol, SplitSource, fingerprint,
                outcome, observations, quarantined, reason, refusalRuleId, diagnostic);
    }
}
