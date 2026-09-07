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
/// One batch of the interrupted price acquisition, resumed. Spends at most seventy requests.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a batch and not a resumption.</strong> The first run dispatched 209 of 704 and then
/// stopped itself: ten consecutive transport failures in a row, which is the containment rule doing
/// exactly what it is for. Fifty-seven of those 209 were spent discovering that the connector threw
/// away the one fact needed to diagnose them. Resuming the whole plan in a single process would put
/// the remaining 495 behind the same unproven transport; a batch puts seventy behind it, writes down
/// what happened, and stops. Nothing carries between batches except the ledger, so a batch that ends
/// early costs only the requests it made.
/// </para>
/// <para>
/// <strong>Prices only.</strong> This runner has no corporate-actions loop at all - not a disabled
/// one, not a skipped one. The splits phase is a separate approval and there is nothing here that
/// could begin it by accident, which is a stronger guarantee than a flag.
/// </para>
/// <para>
/// <strong>The ceiling survives the process.</strong> <see cref="AcquisitionAuthorization"/> loads
/// from the declaration with a counter at zero, so three batches of seventy would each believe they
/// had 704 left. What earlier processes already spent is read from the outcome artefacts on disk and
/// charged before anything is dispatched, and a batch is additionally capped at its own declared
/// size and at <see cref="MaxBatchSize"/>, whatever the authorisation still has room for.
/// </para>
/// <para>
/// <strong>Every request goes through the ordinary path.</strong> <see cref="IDataAcquisition"/>
/// calls the ingestion gateway, which applies source admission, capability, the declared rate limit
/// and the Action/Policy seam, archives before it normalises, and reads the archive rather than the
/// response. Nothing is fetched directly, nothing retries, and no failure can become an observation.
/// </para>
/// </remarks>
public sealed class AcquisitionBatchTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_ACQUISITION_BATCH";

    /// <summary>
    /// Which batch to run. Deliberately has no default: an unstated batch is not batch one.
    /// </summary>
    private const string IndexVariable = "AIINV_BATCH_INDEX";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";
    private const string DefaultSecurityKind = "Security";

    /// <summary>
    /// The most requests any batch may dispatch, whatever it declares and whatever the
    /// authorisation still allows.
    /// </summary>
    /// <remarks>
    /// A backstop rather than the operative limit: each batch is additionally capped at its own
    /// declared size, so batch 3 may spend sixty and not seventy. Two limits rather than one,
    /// because the declared size is the thing that was approved and this is the thing that cannot
    /// be raised by editing a table.
    /// </remarks>
    private const int MaxBatchSize = 70;

    /// <summary>Consecutive failures that end the batch rather than continuing through them.</summary>
    /// <remarks>
    /// Unchanged from the interrupted run, deliberately. It is not a retry policy - nothing here
    /// retries - it is the point at which "this request failed" stops being a fact about one symbol
    /// and becomes a fact about the transport.
    /// </remarks>
    private const int MaxConsecutiveFailures = 10;

    /// <summary>The gap kept between dispatches, against a declared sixty a minute.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(1100);

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The price partition, as reported before execution, and which parts of it are approved.
    /// </summary>
    /// <remarks>
    /// The boundaries are written down rather than recomputed so that a batch means the same set of
    /// symbols every time it is named. <c>Authorised</c> is a separate approval per batch: batches
    /// two and three are defined here so that the partition is auditable in one place, and refused
    /// here so that defining them cannot run them.
    /// </remarks>
    private static readonly BatchDefinition[] Partition =
    [
        // Batch 1 ran on 2026-09-05 and its 70 symbols are in the ledger. It is marked
        // unauthorised again deliberately: the approval that covered it is spent, and an
        // authorisation that stays open after it has been used is not an authorisation.
        new(1, "CMTL.US", "NVEE.US", 70, 200, 209, Authorised: false),
        new(2, "NXST.US", "SIRE.US", 70, 130, 279, Authorised: false),
        new(3, "SIRI.US", "ZYME.US", 60, 61, 349, Authorised: false),

        // The completion. Not a fourth slice of the partition but the remainder of it: the two
        // symbols whose dispatches failed on transport in batches 2 and 3 and which the ledger
        // therefore still owes. Its range spans them and nothing else, and it declares that two is
        // all that is left - so if a third price request were outstanding this would refuse to run.
        new(4, "NXST.US", "SIRI.US", 2, 2, 411, Authorised: false),

        // The last price request. SIRI.US completed on batch 4's second attempt; NXST.US failed on
        // transport there and in batch 2, and is the only price work the ledger still owes.
        new(5, "NXST.US", "NXST.US", 1, 1, 413, Authorised: true),
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public AcquisitionBatchTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task One_approved_batch_of_prices_is_acquired_and_nothing_else_is()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The batched acquisition is off. Set {GateVariable}=1 and {IndexVariable} to a batch "
            + "number to run it. It SPENDS PROVIDER REQUESTS.");

        var watch = Stopwatch.StartNew();

        // ---- which batch, stated rather than assumed -----------------------------------------------

        var requested = Environment.GetEnvironmentVariable(IndexVariable);

        Assert.True(
            int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index),
            Universe.Inv($"{IndexVariable} is '{requested ?? "(unset)"}', which is not a batch number. There is no default: a run that does not say which batch it is must not pick one."));

        var batch = Array.Find(Partition, b => b.Index == index);

        Assert.True(
            batch is not null,
            Universe.Inv($"batch {index} is not in the approved partition, which has {Partition.Length} batches."));

        Assert.True(
            batch!.Authorised,
            Universe.Inv($"batch {batch.Index} (`{batch.First}`..`{batch.Last}`, {batch.Count} symbols) is defined but NOT authorised to run. Each batch is approved separately; this one has not been. Nothing was dispatched."));

        // ---- nothing moves until the universe and the authorisation agree ---------------------------

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
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        Assert.Equal(704, authorization.DispatchCeiling);
        Assert.Equal(DateOnly.FromDateTime(WindowStart), authorization.WindowFrom);
        Assert.Equal(DateOnly.FromDateTime(WindowEnd), authorization.WindowTo);
        Assert.Contains(PriceSource, authorization.Sources);

        var members = await ReadyMembersAsync();

        Assert.True(members.Count == 355, Universe.Inv($"{members.Count} ready members, not 355"));

        // ---- what earlier processes already spent, charged before anything is dispatched -------------

        var prior = await PriorConsumptionAsync(batch.Index);

        // The accounting the approval was granted against. A batch approved when 349 were spent is
        // not the same batch once some other process has spent more, and this refuses rather than
        // adapts: a ledger that has moved underneath an approval invalidates the approval.
        Assert.True(
            prior.Total == batch.ExpectedPriorConsumption,
            Universe.Inv($"{prior.Total} dispatches have been consumed, and batch {batch.Index} was approved against {batch.ExpectedPriorConsumption}. The accounting has moved since the approval; nothing was dispatched."));

        authorization.RecordPriorConsumption(prior.Total);

        // Which approved attempt at this batch this is. It goes into every correlation so that a
        // re-approved attempt is a new act to the seam rather than a duplicate of the failed one.
        var attemptNumber = prior.Attempts + 1;

        var cap = batch.Count;

        Assert.True(
            cap <= MaxBatchSize,
            Universe.Inv($"batch {batch.Index} declares {cap} symbols against a hard maximum of {MaxBatchSize}"));

        Assert.True(
            authorization.Remaining >= cap,
            Universe.Inv($"{authorization.Remaining} dispatches remain against a batch of {cap}. This batch cannot be run within the approved ceiling and nothing was dispatched."));

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var acquisition = services.GetRequiredService<IDataAcquisition>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();

        // The request shape the completed fingerprints were computed against. Taken from the ledger
        // rather than assumed: a different subject kind or region is a different fingerprint, and
        // every symbol already acquired would be fetched again.
        var priorPriceRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        // ---- the batch, resolved from the ledger and checked against what was approved ---------------

        var outstanding = new List<string>();

        foreach (var member in members)
        {
            // Built only to compute a fingerprint and then discarded; it is never dispatched, so
            // its correlation names no attempt.
            var probe = BuildRequest(
                PriceSource, member.Symbol, subjectKind, region, clock, batch.Index, attemptNumber, "probe");

            if (!await runStore.HasCompletedAsync(probe.Fingerprint()))
            {
                outstanding.Add(member.Symbol);
            }
        }

        var slice = outstanding
            .Where(s =>
                string.CompareOrdinal(s, batch.First) >= 0 &&
                string.CompareOrdinal(s, batch.Last) <= 0)
            .ToList();

        Assert.True(
            outstanding.Count == batch.ExpectedOutstandingBefore,
            Universe.Inv($"the ledger owes {outstanding.Count} price requests and batch {batch.Index} was approved against {batch.ExpectedOutstandingBefore}. Nothing was dispatched."));

        Assert.True(
            slice.Count == batch.Count,
            Universe.Inv($"batch {batch.Index} resolves to {slice.Count} outstanding symbols, and {batch.Count} were approved. The ledger has moved since the partition was cut; stopping rather than dispatching a set nobody approved."));

        Assert.Equal(batch.First, slice[0]);
        Assert.Equal(batch.Last, slice[^1]);

        // Scope, for every symbol, before a single one is consumed. Covers counts nothing.
        foreach (var symbol in slice)
        {
            var covered = authorization.Covers(
                PriceSource,
                symbol,
                DateOnly.FromDateTime(WindowStart),
                DateOnly.FromDateTime(WindowEnd));

            Assert.True(covered.Allowed, Universe.Inv($"`{symbol}`: {covered.Reason}"));
        }

        Assert.Equal(0, authorization.Consumed - prior.Total);

        // Everything the authorisation would still permit but this batch must not touch. The loop
        // below iterates the slice and nothing else, so this set exists to be checked afterwards
        // rather than to be avoided at runtime: if a single one of them acquires a run, the runner
        // dispatched something nobody approved and the test says so.
        var outsideBatch = outstanding
            .Where(s => !slice.Contains(s, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var byMember = members.ToDictionary(m => m.Symbol, StringComparer.Ordinal);

        // ---- the attempt is new, and it is new about the same work -----------------------------------

        foreach (var symbol in slice)
        {
            var ticker = byMember[symbol].Ticker;

            var correlation = AcquisitionCorrelation
                .Text(batch.Index, attemptNumber, ticker, DataCategory.MarketPrices);

            // Every identifier an earlier attempt could have claimed for this symbol: the
            // symbol-only form the runner used before the fix, and the form each recorded
            // (batch, attempt) pair would have produced. The seam does not release a claim on
            // failure, so reusing any of them would be refused exactly as NXST.US was.
            var burned = new HashSet<string>(StringComparer.Ordinal)
            {
                Universe.Inv($"batch-{AcquisitionCorrelation.Safe(ticker)}-MarketPrices"),
            };

            foreach (var (priorBatch, priorAttempt) in prior.Recorded)
            {
                burned.Add(AcquisitionCorrelation
                    .Text(priorBatch, priorAttempt, ticker, DataCategory.MarketPrices));
            }

            Assert.True(
                !burned.Contains(correlation),
                Universe.Inv($"`{symbol}` would carry `{correlation}`, which an earlier attempt already claimed. Nothing was dispatched."));

            // And the work is the same work. If an earlier attempt at this batch recorded a
            // different fingerprint for this symbol, the request has drifted and this is no longer
            // the acquisition that was approved.
            if (prior.Fingerprints.TryGetValue(symbol, out var before))
            {
                var now = BuildRequest(
                    PriceSource, symbol, subjectKind, region, clock, batch.Index, attemptNumber, ticker)
                    .Fingerprint();

                Assert.True(
                    string.Equals(before, now, StringComparison.Ordinal),
                    Universe.Inv($"`{symbol}` hashed to {before} on an earlier attempt and to {now} now. The work has changed, not just the attempt. Nothing was dispatched."));
            }
        }

        // ---- the batch itself ------------------------------------------------------------------------

        var results = new List<Attempt>(slice.Count);
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
                PriceSource, symbol, subjectKind, region, clock, batch.Index, attemptNumber, member.Ticker);

            var fingerprint = request.Fingerprint();

            // Suppression first, and before the authorisation is asked for anything. A request the
            // ledger has already satisfied does not leave the process, so charging it a dispatch
            // would spend an authorisation on a call nobody makes. The slice was built from the
            // ledger a moment ago, so this should find nothing - it is asked again because "should"
            // is not a guarantee and the cost of asking is a dictionary lookup.
            if (await runStore.HasCompletedAsync(fingerprint))
            {
                authorization.RecordSuppressed();
                results.Add(Attempt.Suppressed(member, fingerprint));
                continue;
            }

            if (dispatchedHere >= cap)
            {
                stopped = Universe.Inv(
                    $"the batch cap of {cap} dispatches is reached at `{symbol}`.");
                break;
            }

            var permitted = authorization.TryConsume(
                PriceSource,
                symbol,
                DateOnly.FromDateTime(WindowStart),
                DateOnly.FromDateTime(WindowEnd));

            if (!permitted.Allowed)
            {
                // Not an outcome to work around. Either the ceiling is reached or the plan has
                // drifted from what was approved, and both mean stop.
                stopped = Universe.Inv($"{PriceSource} {symbol}: {permitted.Reason}");
                results.Add(Attempt.Unauthorised(member, fingerprint, permitted.Reason!));
                break;
            }

            await PaceAsync(lastDispatchUtc);
            lastDispatchUtc = DateTime.UtcNow;
            dispatchedHere++;

            Attempt attempt;

            try
            {
                var outcome = await acquisition.AcquireAsync(request);

                attempt = Attempt.Dispatched(
                    member,
                    fingerprint,
                    outcome.Run.Outcome.ToString(),
                    outcome.Run.Artifacts.Count,
                    outcome.Normalization?.PayloadsRead ?? 0,
                    outcome.ObservationsRecorded,
                    outcome.Normalization?.PayloadsQuarantined ?? 0,
                    outcome.Run.Reason,
                    outcome.Run.RefusalRuleId,
                    DiagnosticIn(outcome.Run.Reason));
            }
#pragma warning disable CA1031 // Deliberate: an unhandled exception here would abort the batch
                              // before the record of what has already been spent is written, which
                              // is the one outcome worse than a failed request. The failure is
                              // recorded as a failure - never as a success - and the consecutive
                              // counter below ends the batch rather than spending the rest of it
                              // against a transport that is plainly not working.
            catch (Exception exception)
            {
                attempt = Attempt.Threw(
                    member,
                    fingerprint,
                    exception.GetType().Name,
                    DiagnosticOf(exception));
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
                    $"{consecutiveFailures} consecutive requests failed, most recently {PriceSource} {symbol}. Stopping rather than spending the rest of this batch against a transport that is not answering.");
                break;
            }
        }

        // ---- what happened -----------------------------------------------------------------------------

        var dispatched = results.Where(r => r.Kind == Attempt.DispatchedKind).ToList();
        var suppressed = results.Where(r => r.Kind == Attempt.SuppressedKind).ToList();

        var succeeded = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.Succeeded));
        var partial = dispatched.Count(r => r.Outcome == nameof(IngestionOutcome.PartiallySucceeded));
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
            "artifacts", "universe", Universe.Inv($"acquisition-batch-{batch.Index:00}-attempt-{attemptNumber:00}.json"));

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
                    Source = PriceSource,
                    AcquiredAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    WindowFromUtc = "2021-09-01",
                    WindowToUtc = "2026-08-31",
                    SubjectKind = subjectKind,
                    Region = region.Code,
                    OutstandingPricesBefore = outstanding.Count,
                    Suppressed = suppressed.Count,
                    Dispatched = dispatched.Count,
                    Succeeded = succeeded,
                    PartiallySucceeded = partial,
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

        var report = Compose(
            batch, authorization, prior, slice, outstanding.Count, results, dispatched, suppressed,
            succeeded, partial, failed, refused, consumedHere,
            observationsBefore, observationsAfter, runsBefore, runsAfter,
            diagnostics.Select(d => (d.Token, d.Requests)).ToList(), stopped, watch, attemptPath);

        await Universe.WriteAsync(
            Universe.RepositoryPath(
                "artifacts", "verify", Universe.Inv($"acquisition-batch-{batch.Index:00}.md")),
            report);

        _output.WriteLine(report);

        // ---- the invariants that must hold whatever the provider did ------------------------------------

        var manifestAfter = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(manifestBefore.SequenceEqual(manifestAfter), "the sealed manifest changed");

        // The batch cap and the authorisation ceiling, from both directions.
        Assert.True(
            consumedHere <= cap,
            Universe.Inv($"{consumedHere} dispatches were consumed against a batch cap of {cap}"));

        Assert.True(
            authorization.Consumed <= authorization.DispatchCeiling,
            Universe.Inv($"{authorization.Consumed} dispatches consumed against a ceiling of {authorization.DispatchCeiling}"));

        Assert.True(
            dispatched.Count == consumedHere,
            Universe.Inv($"{dispatched.Count} dispatches were made but {consumedHere} were consumed"));

        // Prices only. There is no splits loop in this class, and this says so from the record.
        Assert.DoesNotContain(results, r => r.Source != PriceSource);
        Assert.All(results, r => Assert.Contains(r.Symbol, authorization.Symbols));

        // Everything dispatched was inside the approved batch, and nothing else was.
        Assert.All(results, r => Assert.Contains(r.Symbol, slice));
        Assert.DoesNotContain(results, r => outsideBatch.Contains(r.Symbol));

        // A refused or failed run archives nothing, so it cannot have produced an observation.
        Assert.DoesNotContain(dispatched, r => !r.Succeeded && r.ObservationsRecorded > 0);

        // No run may appear that this batch did not make.
        Assert.True(
            runsAfter - runsBefore == dispatched.Count,
            Universe.Inv($"{runsAfter - runsBefore} ingestion runs appeared for {dispatched.Count} dispatches"));

        Assert.Null(stopped);
    }

    /// <summary>
    /// Builds one request, with a correlation that names the attempt as well as the symbol.
    /// </summary>
    /// <remarks>
    /// The attempt is in the correlation and deliberately not in the fingerprint: the fingerprint
    /// is what the ledger suppresses on and must stay identical across attempts, while the
    /// correlation is what the Action/Policy seam keys idempotency on and must differ, or a
    /// transport failure claims the key forever and the symbol can never be re-attempted. See
    /// <see cref="AcquisitionCorrelation"/> for the failure this fixes.
    /// </remarks>
    private static IngestionRequest BuildRequest(
        string source,
        string symbol,
        string subjectKind,
        Region region,
        IClock clock,
        int batchIndex,
        int attempt,
        string ticker) =>
        IngestionRequest.Create(
            SourceId.Create(source),
            DataCategory.MarketPrices,
            region,
            IngestionSubject.Create(subjectKind, symbol),
            AcquisitionCorrelation.For(batchIndex, attempt, ticker, DataCategory.MarketPrices),
            clock.UtcNow,
            DateRange.Create(WindowStart, WindowEnd));

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

    /// <summary>
    /// What every earlier process spent against this authorisation, read from what they wrote.
    /// </summary>
    private static async Task<Prior> PriorConsumptionAsync(int index)
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var total = 0;
        var attempts = 0;
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var recorded = new List<(int Batch, int Attempt)>();

        var outcome = Path.Combine(universe, "acquisition-outcome.json");

        if (File.Exists(outcome))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outcome));

            total += document.RootElement.GetProperty("AuthorizationConsumed").GetInt32();
        }

        // Consumption is summed across every batch, because the ceiling is one. Attempts are
        // counted only for this batch, because the number is used to name this batch's artefact
        // and an attempt at another batch is not an attempt at this one.
        var mine = Universe.Inv($"acquisition-batch-{index:00}-attempt-");

        foreach (var path in Directory
            .EnumerateFiles(universe, "acquisition-batch-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            total += document.RootElement.GetProperty("AuthorizationConsumedThisRun").GetInt32();

            // From the file's own name rather than its contents: the name is what the runner
            // chooses, and artefacts written before an attempt number existed still parse.
            if (AttemptOf(Path.GetFileName(path)) is { } pair)
            {
                recorded.Add(pair);
            }

            if (!Path.GetFileName(path).StartsWith(mine, StringComparison.Ordinal))
            {
                continue;
            }

            attempts++;

            // What this batch's earlier attempts asked for. The fingerprint must not have moved:
            // a new correlation is a new act, and it must still be an act about the same work.
            foreach (var entry in document.RootElement.GetProperty("Attempts").EnumerateArray())
            {
                var symbol = entry.GetProperty(nameof(Attempt.Symbol)).GetString();
                var fingerprint = entry.GetProperty(nameof(Attempt.Fingerprint)).GetString();

                if (symbol is not null && fingerprint is not null)
                {
                    fingerprints[symbol] = fingerprint;
                }
            }
        }

        return new Prior(total, attempts, fingerprints, recorded);
    }

    /// <summary>The classification the connector attached to a recorded failure, if any.</summary>
    /// <remarks>
    /// The gateway writes type names and, where a frame carries one, a closed-set transport token in
    /// square brackets. Reading it back is a string scan rather than a rethrow: the ledger row is the
    /// only surviving evidence once the process has ended, so the report is built from the same text
    /// an operator would read there.
    /// </remarks>
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

    /// <summary>The classification carried by an exception that escaped the gateway entirely.</summary>
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

    private const string NoDiagnostic = "(none)";

    private static bool IsTransportToken(string token) =>
        token.StartsWith("socket:", StringComparison.Ordinal)
        || token.StartsWith("http:", StringComparison.Ordinal)
        || token.StartsWith("status:", StringComparison.Ordinal)
        || string.Equals(token, ProviderTransportException.Unclassified, StringComparison.Ordinal);

    /// <summary>The batch and attempt an artefact's file name names, or null if it names neither.</summary>
    private static (int Batch, int Attempt)? AttemptOf(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');

        return parts.Length == 5 &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var batch) &&
            int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt)
            ? (batch, attempt)
            : null;
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
        BatchDefinition batch,
        AcquisitionAuthorization authorization,
        Prior prior,
        List<string> slice,
        int outstandingBefore,
        List<Attempt> results,
        List<Attempt> dispatched,
        List<Attempt> suppressed,
        int succeeded,
        int partial,
        int failed,
        int refused,
        int consumedHere,
        int observationsBefore,
        int observationsAfter,
        int runsBefore,
        int runsAfter,
        List<(string Token, int Requests)> diagnostics,
        string? stopped,
        Stopwatch watch,
        string attemptPath)
    {
        var report = new StringBuilder();

        report.AppendLine(Universe.Inv($"# Batch {batch.Index} of the resumed price acquisition"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"`{PriceSource}` only, {slice.Count} symbols `{batch.First}`..`{batch.Last}`, 2021-09-01..2026-08-31."));
        report.AppendLine(Universe.Inv($"Sealed universe `{SealedFingerprint}`, byte-identical and not resealed."));
        report.AppendLine(Universe.Inv($"Authorisation `{authorization.AuthorizationId}`, digest `{authorization.Digest}`, unamended."));
        report.AppendLine();

        report.AppendLine("## Provider accounting");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Outstanding price requests before this batch | {outstandingBefore} |"));
        report.AppendLine(Universe.Inv($"| In this batch | {slice.Count} |"));
        report.AppendLine(Universe.Inv($"| Outstanding left for a later batch | {outstandingBefore - slice.Count} - none dispatched here |"));
        report.AppendLine(Universe.Inv($"| Suppressed by the ledger | {suppressed.Count} |"));
        report.AppendLine(Universe.Inv($"| **Dispatched** | **{dispatched.Count}** |"));
        report.AppendLine(Universe.Inv($"| …succeeded | {succeeded} |"));
        report.AppendLine(Universe.Inv($"| …partially succeeded | {partial} |"));
        report.AppendLine(Universe.Inv($"| …failed | {failed} |"));
        report.AppendLine(Universe.Inv($"| …refused before the network | {refused} |"));
        report.AppendLine(Universe.Inv($"| Batch cap | {batch.Count} of a hard maximum of {MaxBatchSize} |"));
        report.AppendLine(Universe.Inv($"| **Authorisation consumed by this batch** | **{consumedHere}** |"));
        report.AppendLine(Universe.Inv($"| Authorisation consumed before it | {prior.Total} |"));
        report.AppendLine(Universe.Inv($"| Authorisation consumed in total | {authorization.Consumed} of {authorization.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| Authorisation remaining | {authorization.Remaining} |"));
        report.AppendLine(Universe.Inv($"| `{ActionsSource}` requests | 0 - this runner has no corporate-actions path |"));
        report.AppendLine(Universe.Inv($"| Dividend requests | 0 - no source exists and none is authorised |"));
        report.AppendLine("| SEC requests | 0 - the connector was not enabled |");
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

        report.AppendLine("## Transport diagnostics");
        report.AppendLine();

        if (diagnostics.Count == 0)
        {
            report.AppendLine("No dispatch failed, so the connector classified nothing.");
        }
        else
        {
            report.AppendLine("The tokens the connector attached to each failure, built from framework");
            report.AppendLine("enumerations rather than from any message, and recorded in the ledger.");
            report.AppendLine();
            report.AppendLine("| Token | Requests |");
            report.AppendLine("| --- | ---: |");

            foreach (var (token, requests) in diagnostics)
            {
                report.AppendLine(Universe.Inv($"| `{token}` | {requests} |"));
            }
        }

        report.AppendLine();

        if (stopped is not null)
        {
            report.AppendLine("## The batch stopped");
            report.AppendLine();
            report.AppendLine(Universe.Inv($"**{stopped}**"));
            report.AppendLine();
            report.AppendLine("Stopping rather than improvising is the instruction and the design. What");
            report.AppendLine("was fetched before the stop is in the ledger and the archive; nothing was");
            report.AppendLine("discarded and nothing was invented, and the next batch suppresses it.");
            report.AppendLine();
        }

        var bad = dispatched.Where(r => !r.Succeeded).ToList();

        report.AppendLine("## Every dispatch that did not succeed");
        report.AppendLine();

        if (bad.Count == 0)
        {
            report.AppendLine("None. Every dispatched request returned a payload that was archived.");
        }
        else
        {
            report.AppendLine("| Symbol | Outcome | Diagnostic | Rule | Reason |");
            report.AppendLine("| --- | --- | --- | --- | --- |");

            foreach (var row in bad)
            {
                report.AppendLine(Universe.Inv(
                    $"| `{row.Symbol}` | {row.Outcome} | `{row.Diagnostic}` | {row.RefusalRuleId ?? "-"} | {Trim(row.Reason)} |"));
            }
        }

        report.AppendLine();

        var empty = dispatched
            .Where(r => r.Succeeded && r.ObservationsRecorded == 0)
            .ToList();

        report.AppendLine(Universe.Inv(
            $"Succeeded but recorded no observation: **{empty.Count}**. For `{PriceSource}` that is not ordinary and each one is listed in the JSON - a success with nothing in it is either an empty vendor series or a payload the normaliser refused."));
        report.AppendLine();

        report.AppendLine(Universe.Inv(
            $"The attempt-by-attempt record is at `artifacts/universe/{Path.GetFileName(attemptPath)}`. Elapsed {watch.Elapsed.TotalMinutes:F1} minutes."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"**No further batch runs automatically.** Batch {batch.Index + 1} requires its own approval; the runner refuses any batch not marked authorised."));

        return report.ToString();
    }

    private static string Trim(string? reason) =>
        reason is null ? "-" : reason.Length <= 120 ? reason : reason[..120] + "…";

    /// <param name="Index">The batch number an operator names.</param>
    /// <param name="First">The first symbol in the batch, ordinal.</param>
    /// <param name="Last">The last symbol in the batch, ordinal.</param>
    /// <param name="Count">How many outstanding symbols the batch was cut to hold.</param>
    /// <param name="Authorised">Whether this batch has been approved to run.</param>
    /// <param name="ExpectedOutstandingBefore">
    /// How many price requests the ledger must still owe when this batch runs. For the completion
    /// batch it is the whole remainder, which is how "these are the only two left" becomes a check
    /// rather than a claim.
    /// </param>
    /// <param name="ExpectedPriorConsumption">
    /// What must already have been spent when this batch runs. Pins the accounting the approval was
    /// granted against, so a batch cannot run against a ledger that has moved underneath it.
    /// </param>
    private sealed record BatchDefinition(
        int Index,
        string First,
        string Last,
        int Count,
        int ExpectedOutstandingBefore,
        int ExpectedPriorConsumption,
        bool Authorised);

    /// <param name="Total">Dispatches every earlier process made against this authorisation.</param>
    /// <param name="Attempts">How many attempts at THIS batch have already been recorded.</param>
    /// <param name="Fingerprints">
    /// What each symbol's request hashed to on an earlier attempt at this batch. Used to prove the
    /// new attempt asks for the same work, not merely for something with the same name.
    /// </param>
    /// <param name="Recorded">
    /// Every (batch, attempt) pair that has already written an artefact, across all batches, read
    /// from the artefact file names. The correlation each of them would have produced for a given
    /// ticker is reconstructible from those two numbers, which is how a new attempt proves it is
    /// not reusing an identifier the seam has already claimed.
    /// </param>
    private sealed record Prior(
        int Total,
        int Attempts,
        IReadOnlyDictionary<string, string> Fingerprints,
        IReadOnlyList<(int Batch, int Attempt)> Recorded);

    private sealed record Ready(string Cik, string Name, string Ticker, string Symbol);

    private sealed record Attempt(
        string Kind,
        string Cik,
        string Name,
        string Symbol,
        string Source,
        string Fingerprint,
        string? Outcome,
        int Artifacts,
        int PayloadsRead,
        int ObservationsRecorded,
        int PayloadsQuarantined,
        string? Reason,
        string? RefusalRuleId,
        string Diagnostic)
    {
        public const string DispatchedKind = "dispatched";
        public const string SuppressedKind = "suppressed";
        public const string UnauthorisedKind = "unauthorised";

        /// <summary>A dispatch that came back with something the archive could keep.</summary>
        public bool Succeeded =>
            Outcome is nameof(IngestionOutcome.Succeeded) or nameof(IngestionOutcome.PartiallySucceeded);

        public static Attempt Suppressed(Ready member, string fingerprint) =>
            new(SuppressedKind, member.Cik, member.Name, member.Symbol, PriceSource, fingerprint,
                null, 0, 0, 0, 0, "already completed in the ledger", null, NoDiagnostic);

        public static Attempt Unauthorised(Ready member, string fingerprint, string reason) =>
            new(UnauthorisedKind, member.Cik, member.Name, member.Symbol, PriceSource, fingerprint,
                null, 0, 0, 0, 0, reason, null, NoDiagnostic);

        public static Attempt Threw(
            Ready member, string fingerprint, string exceptionType, string diagnostic) =>
            new(DispatchedKind, member.Cik, member.Name, member.Symbol, PriceSource, fingerprint,
                nameof(IngestionOutcome.Failed), 0, 0, 0, 0,
                $"{exceptionType} escaped the gateway", null, diagnostic);

        public static Attempt Dispatched(
            Ready member,
            string fingerprint,
            string outcome,
            int artifacts,
            int payloadsRead,
            int observations,
            int quarantined,
            string? reason,
            string? refusalRuleId,
            string diagnostic) =>
            new(DispatchedKind, member.Cik, member.Name, member.Symbol, PriceSource, fingerprint,
                outcome, artifacts, payloadsRead, observations, quarantined, reason, refusalRuleId,
                diagnostic);
    }
}
