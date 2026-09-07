using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
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
/// The live accounting, after scoping prior consumption to the active authorisation. Spends nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called, no split request is dispatched, no batch is authorised and no
/// artefact is written, moved or rewritten.</strong> Every artefact under <c>artifacts/universe</c>
/// is read exactly as it was recorded. Run, observation and quarantine counts are asserted
/// unchanged either side, and one report is written.
/// </para>
/// <para>
/// This is the live half of <see cref="SplitPriorConsumptionTests"/>: that one holds the rule with
/// synthetic artefacts in a temporary directory, and this one shows the rule producing the right
/// answer over the real ledger and the real artefacts.
/// </para>
/// </remarks>
public sealed class SplitAccountingReconciliationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SPLIT_ACCOUNTING";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const string PriceDeclaration = "acquisition-eodhd-sample400.json";
    private const string FirstSplitDeclaration = "acquisition-eodhd-splits-sample400.json";

    private const string PredecessorDeclaration =
        "acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json";

    private const string ActiveDeclaration =
        "acquisition-eodhd-splits-final-2021-09-to-2026-08.json";

    private const string FirstSplitAuthorizationId =
        "eodhd-sample400-splits-2021-09-to-2026-08";

    private const string PredecessorAuthorizationId =
        "eodhd-sample400-splits-remainder-2021-09-to-2026-08";

    private const string ActiveAuthorizationId =
        "eodhd-sample400-splits-final-2021-09-to-2026-08";

    private const string SplitSource = "eodhd-splits";
    private const string DefaultSecurityKind = "Security";

    /// <summary>The acquirable universe, the work done against it, and the work still owed.</summary>
    private const int ExpectedMembers = 355;

    /// <summary>
    /// What the active declaration was sized against, and what the ledger has completed now.
    /// </summary>
    /// <remarks>
    /// These were the same number on the day the authorisation was issued and have since diverged
    /// by exactly the work it has paid for. Keeping them apart is the point: the declaration is a
    /// fixed record of what was approved and may not be re-sized, so a test that read live progress
    /// back into it would hide the very drift the ceiling exists to bound.
    /// </remarks>
    private const int ExpectedSizedAgainst = 282;

    private const int ExpectedSatisfied = 355;
    private const int ExpectedOutstanding = 0;
    private const int ExpectedCeiling = 73;

    /// <summary>What has been spent under this authorisation, and what that leaves.</summary>
    private const int ExpectedActiveSpend = 73;

    private const int ExpectedRemaining = 0;

    /// <summary>
    /// The work the ledger still owes, by name rather than by count.
    /// </summary>
    /// <remarks>
    /// Empty now, and kept as a set rather than dropped for a count. "Zero owed" and "this exact
    /// set is owed" are the same claim only while the probe is really asking; keeping the set means
    /// a probe that quietly stopped resolving symbols would fail here instead of reading as
    /// completion.
    /// </remarks>
    private static readonly string[] ExpectedOutstandingSymbols = [];

    /// <summary>
    /// What the two superseded split authorisations spent, and what this one carries as evidence.
    /// </summary>
    /// <remarks>
    /// <see cref="ExpectedCarriedEvidence"/> is the <c>AlreadyConsumed</c> the active declaration
    /// records. It is deliberately equal to <see cref="ExpectedPredecessorSpend"/> and deliberately
    /// <em>not</em> charged against this ceiling: a successor is sized to the work that remains, and
    /// the figure is here so the two can be pinned to each other rather than drifting apart.
    /// </remarks>
    private const int ExpectedFirstSplitSpend = 210;

    private const int ExpectedPredecessorSpend = 71;
    private const int ExpectedCarriedEvidence = 71;

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SplitAccountingReconciliationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Prior_consumption_is_scoped_to_the_active_authorisation_and_nothing_moves()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The split accounting reconciliation is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        // ---- everything on disk, hashed before it is read for anything else -----------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var pricePath = Universe.RepositoryPath("declarations", PriceDeclaration);
        var firstSplitPath = Universe.RepositoryPath("declarations", FirstSplitDeclaration);
        var predecessorPath = Universe.RepositoryPath("declarations", PredecessorDeclaration);
        var activePath = Universe.RepositoryPath("declarations", ActiveDeclaration);

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var priceBytes = await File.ReadAllBytesAsync(pricePath);
        var firstSplitBytes = await File.ReadAllBytesAsync(firstSplitPath);
        var predecessorBytes = await File.ReadAllBytesAsync(predecessorPath);
        var activeBytes = await File.ReadAllBytesAsync(activePath);

        Assert.Equal(SealedDigest, Sha256(manifestBytes));

        var predecessor = AcquisitionAuthorization.Load(predecessorPath, SealedFingerprint);
        var active = AcquisitionAuthorization.Load(activePath, SealedFingerprint);

        Assert.Equal(PredecessorAuthorizationId, predecessor.AuthorizationId);
        Assert.Equal(ActiveAuthorizationId, active.AuthorizationId);
        Assert.NotEqual(predecessor.AuthorizationId, active.AuthorizationId);

        // The chain, checked rather than described. The identifier is digest-covered by the '@2'
        // schema; the digest of the authorisation being replaced is not, so it is compared against
        // what the predecessor's own content recomputes to rather than trusted as written.
        Assert.Equal(PredecessorAuthorizationId, active.SupersedesAuthorizationId);
        Assert.Equal(predecessor.Digest, SupersededDigestIn(activeBytes));
        Assert.Equal(FirstSplitAuthorizationId, predecessor.SupersedesAuthorizationId);

        // The arithmetic of an authorisation, restated here so this test fails if the loader ever
        // stops enforcing it: the ceiling is the plan less what is already done, and nothing else.
        Assert.Equal(active.PlannedRequests - active.AlreadySatisfied, active.DispatchCeiling);
        Assert.Equal(ExpectedMembers, active.PlannedRequests);
        Assert.Equal(ExpectedSizedAgainst, active.AlreadySatisfied);
        Assert.Equal(ExpectedCeiling, active.DispatchCeiling);

        // The batches name a declaration; the declaration names an authorisation. The identifier
        // used for charging is read from there and never written down in the runner.
        Assert.Equal(ActiveDeclaration, AcquisitionSplitBatchTests.Partition[0].Declaration);

        // ---- what each authorisation has spent, from the artefacts themselves ---------------------

        var universe = Universe.RepositoryPath("artifacts", "universe");

        var namedAuthorizations = await AcquisitionSplitBatchTests.AuthorizationsInAsync(universe);

        var underActive = await AcquisitionSplitBatchTests
            .ConsumedUnderAsync(universe, active.AuthorizationId);

        var underPredecessor = await AcquisitionSplitBatchTests
            .ConsumedUnderAsync(universe, predecessor.AuthorizationId);

        var underFirstSplit = await AcquisitionSplitBatchTests
            .ConsumedUnderAsync(universe, FirstSplitAuthorizationId);

        var everyArtefact = await EveryArtefactAsync(universe);

        // Proof B: each superseded authorisation's spending is present, attributed to it, and
        // charged to nothing else. The unscoped total is computed here purely to show what it
        // would have been - and it now spans three authorisations rather than two.
        Assert.True(
            underPredecessor == ExpectedPredecessorSpend,
            Universe.Inv($"the artefacts attribute {underPredecessor} dispatches to the predecessor, not {ExpectedPredecessorSpend}"));

        Assert.True(
            underFirstSplit == ExpectedFirstSplitSpend,
            Universe.Inv($"the artefacts attribute {underFirstSplit} dispatches to the first split authorisation, not {ExpectedFirstSplitSpend}"));

        Assert.True(
            underActive == ExpectedActiveSpend,
            Universe.Inv($"the artefacts attribute {underActive} dispatches to the active authorisation, not {ExpectedActiveSpend}"));

        Assert.Equal(everyArtefact, underActive + underPredecessor + underFirstSplit);

        Assert.Equal(
            [FirstSplitAuthorizationId, ActiveAuthorizationId, PredecessorAuthorizationId],
            namedAuthorizations);

        // Evidence, not a charge. The carried figure is exactly what the artefacts attribute to the
        // authorisation this one replaces, so it can be audited rather than believed - and it is
        // asserted separately from Consumed below, because conflating the two is precisely the
        // mistake that would turn a 73-unit ceiling into a 2-unit one.
        Assert.Equal(ExpectedCarriedEvidence, active.AlreadyConsumed);
        Assert.Equal(underPredecessor, active.AlreadyConsumed);

        // A batch may run only when the artefacts attribute to this authorisation exactly what it
        // declares was spent before it. While work remained, exactly one batch matched and it was
        // the next to run; now that the spend has reached the ceiling, none does - which is the
        // shape of a finished partition rather than a broken one. Derived rather than indexed, so
        // this reads the same before and after completion and needs no flag to tell it which.
        var next = Array.Find(
            AcquisitionSplitBatchTests.Partition,
            b => b.ExpectedPriorConsumption == underActive);

        Assert.True(
            next is null,
            Universe.Inv($"batch {next?.Index} still expects the {underActive} dispatch(es) the artefacts attribute to this authorisation; the work was reported complete"));

        active.RecordPriorConsumption(underActive);

        Assert.Equal(ExpectedActiveSpend, active.Consumed);
        Assert.Equal(ExpectedRemaining, active.Remaining);
        Assert.Equal(active.DispatchCeiling - active.Consumed, active.Remaining);

        // ---- and what the ledger still owes -------------------------------------------------------

        var members = await ReadyMembersAsync();

        Assert.True(
            members.Count == ExpectedMembers,
            Universe.Inv($"{members.Count} ready members, not {ExpectedMembers}"));

        var priorRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.CorporateActions)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorRun?.Request.Region ?? Region.Global;

        var outstanding = new List<string>();
        var probe = 0;

        foreach (var member in members)
        {
            var request = IngestionRequest.Create(
                SourceId.Create(SplitSource),
                DataCategory.CorporateActions,
                region,
                IngestionSubject.Create(subjectKind, member.Symbol),
                CorrelationId.Create(Universe.Inv($"accounting-probe-{probe++}")),
                clock.UtcNow,
                DateRange.Create(WindowStart, WindowEnd));

            if (!await runStore.HasCompletedAsync(request.Fingerprint()))
            {
                outstanding.Add(member.Symbol);
            }
        }

        Assert.True(
            outstanding.Count == ExpectedOutstanding,
            Universe.Inv($"the ledger owes {outstanding.Count} split requests, not {ExpectedOutstanding}"));

        // Satisfied is measured, not declared.
        Assert.Equal(ExpectedSatisfied, members.Count - outstanding.Count);

        // And the work still owed is named, not merely counted.
        Assert.Equal(ExpectedOutstandingSymbols, outstanding);

        // And it reconciles with the declaration: what this authorisation was sized against, plus
        // what it has since paid for, is what the ledger has completed. The equality is exact only
        // while every dispatch under it has succeeded - a failed one spends a unit and satisfies
        // nothing, which is precisely the shortfall that has twice forced a successor. So this
        // fails loudly at the moment that happens rather than at the end of the last batch.
        Assert.Equal(active.AlreadySatisfied + active.Consumed, members.Count - outstanding.Count);

        // The remaining ceiling covers the outstanding set exactly. Not more - that would be slack
        // nobody approved; not less - that is the shortfall this authorisation exists to resolve.
        // Measured against Remaining, not the ceiling: the ceiling is fixed at what was approved
        // and the outstanding count falls as work completes, so only these two track each other.
        // At the end both are zero, which is what "sized to the work" means once the work is done.
        Assert.Equal(outstanding.Count, active.Remaining);
        Assert.Equal(0, active.Remaining);

        // Nothing can be dispatched under this authorisation again, whatever any flag says: no
        // batch is small enough to fit what remains. Said without reference to Authorised on
        // purpose - a spent authorisation must be safe even if a flag is left set by mistake.
        Assert.All(
            AcquisitionSplitBatchTests.Partition,
            batch => Assert.True(
                batch.Count > active.Remaining,
                Universe.Inv($"batch {batch.Index} needs {batch.Count} unit(s) and {active.Remaining} remain")));

        // Every outstanding request is covered by a batch that has not run: the batches still to
        // come are the ones expecting at least what has been spent, and their counts add to what
        // the ledger owes. Both sides are now empty and zero, and they are still computed the same
        // way - a batch left declaring more than the ceiling was ever sized for, or an outstanding
        // symbol reappearing, breaks this rather than passing as "nothing to check".
        var remainingBatches = AcquisitionSplitBatchTests.Partition
            .Where(b => b.ExpectedPriorConsumption >= underActive)
            .ToList();

        Assert.Empty(remainingBatches);
        Assert.Equal(outstanding.Count, remainingBatches.Sum(b => b.Count));

        // And the whole partition adds to the whole ceiling, all of it now spent: the three batches
        // account for every unit this authorisation was approved for, with none left over.
        Assert.Equal(ExpectedCeiling, AcquisitionSplitBatchTests.Partition.Sum(b => b.Count));
        Assert.Equal(active.Consumed, AcquisitionSplitBatchTests.Partition.Sum(b => b.Count));

        // ================================================================================
        // The report
        // ================================================================================

        report.AppendLine("# Split accounting, scoped to the active authorisation");
        report.AppendLine();
        report.AppendLine("**Read-only.** No provider was called, no split request was dispatched, no");
        report.AppendLine("batch is authorised, and not one artefact was written, moved or rewritten.");
        report.AppendLine();

        report.AppendLine("## What each authorisation has spent");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Split artefacts on disk | {everyArtefact} unit(s) across all authorisations |"));
        report.AppendLine(Universe.Inv($"| …attributed to `{FirstSplitAuthorizationId}` | **{underFirstSplit}** |"));
        report.AppendLine(Universe.Inv($"| …attributed to the predecessor `{predecessor.AuthorizationId}` | **{underPredecessor}** |"));
        report.AppendLine(Universe.Inv($"| …attributed to the active `{active.AuthorizationId}` | **{underActive}** |"));
        report.AppendLine(Universe.Inv($"| Authorisations the artefacts name | {string.Join(", ", namedAuthorizations)} |"));
        report.AppendLine("| Artefacts naming no authorisation | 0 - one would have refused this run |");
        report.AppendLine();
        report.AppendLine("Before the fix the runner totalled every artefact and would have charged the");
        report.AppendLine(Universe.Inv($"active authorisation {everyArtefact} against batches that between them expected {AcquisitionSplitBatchTests.Partition.Sum(b => b.ExpectedPriorConsumption)}, refusing them."));
        report.AppendLine("The artefacts are unchanged; only the question asked of them is.");
        report.AppendLine();

        report.AppendLine("## The active authorisation");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Id | `{active.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Digest | `{active.Digest}` |"));
        report.AppendLine(Universe.Inv($"| Supersedes | `{active.SupersedesAuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Carried spend, as evidence only | {active.AlreadyConsumed} |"));
        report.AppendLine(Universe.Inv($"| Planned | {active.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Already satisfied when it was sized | {active.AlreadySatisfied} |"));
        report.AppendLine(Universe.Inv($"| Satisfied in the live ledger now | {members.Count - outstanding.Count} |"));
        report.AppendLine(Universe.Inv($"| Ceiling | **{active.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Consumed against THIS ceiling | **{active.Consumed}** |"));
        report.AppendLine(Universe.Inv($"| Remaining | **{active.Remaining}** |"));
        report.AppendLine(Universe.Inv($"| Outstanding fingerprints in the live ledger | **{outstanding.Count}** |"));
        var nextBatch = next is null
            ? "none - the authorisation is spent and the work is complete"
            : Universe.Inv($"{next.Index} - {next.Count} symbol(s), `{next.First}`..`{next.Last}`");

        report.AppendLine(Universe.Inv($"| Next batch to run | {nextBatch} |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"The carried {active.AlreadyConsumed} is evidence of what the superseded authorisation"));
        report.AppendLine("spent and is deliberately not subtracted from this ceiling: a successor is sized to");
        report.AppendLine(Universe.Inv($"the work that remains, so {active.Remaining} unspent units stand against {outstanding.Count} outstanding requests."));
        report.AppendLine();

        report.AppendLine("## The partition");
        report.AppendLine();
        report.AppendLine("| Batch | Symbols | First | Last | Outstanding before | Prior consumption | Authorised |");
        report.AppendLine("| ---: | ---: | --- | --- | ---: | ---: | --- |");

        foreach (var batch in AcquisitionSplitBatchTests.Partition)
        {
            report.AppendLine(Universe.Inv($"| {batch.Index} | {batch.Count} | `{batch.First}` | `{batch.Last}` | {batch.ExpectedOutstandingBefore} | {batch.ExpectedPriorConsumption} | {(batch.Authorised ? "YES" : "no")} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Batches authorised: **{AcquisitionSplitBatchTests.Partition.Count(b => b.Authorised)}**."));
        report.AppendLine();

        report.AppendLine("## Integrity");
        report.AppendLine();
        report.AppendLine("| File | Bytes | SHA-256 |");
        report.AppendLine("| --- | ---: | --- |");
        report.AppendLine(Universe.Inv($"| Sealed manifest | {manifestBytes.Length} | `{Sha256(manifestBytes)}` |"));
        report.AppendLine(Universe.Inv($"| Price authorisation | {priceBytes.Length} | `{Sha256(priceBytes)}` |"));
        report.AppendLine(Universe.Inv($"| First split authorisation | {firstSplitBytes.Length} | `{Sha256(firstSplitBytes)}` |"));
        report.AppendLine(Universe.Inv($"| Predecessor split authorisation | {predecessorBytes.Length} | `{Sha256(predecessorBytes)}` |"));
        report.AppendLine(Universe.Inv($"| Active split authorisation | {activeBytes.Length} | `{Sha256(activeBytes)}` |"));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Ingestion runs before / after | {runsBefore} / {await context.IngestionRuns.AsNoTracking().CountAsync()} |"));
        report.AppendLine(Universe.Inv($"| Observations before / after | {observationsBefore} / {await context.Observations.AsNoTracking().CountAsync()} |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads before / after | {quarantineBefore} / {await context.QuarantinedPayloads.AsNoTracking().CountAsync()} |"));
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "split-accounting.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -------------------------------------------------------------------------

        Assert.True(manifestBytes.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)), "the sealed manifest changed");
        Assert.True(priceBytes.SequenceEqual(await File.ReadAllBytesAsync(pricePath)), "the price authorisation changed");
        Assert.True(firstSplitBytes.SequenceEqual(await File.ReadAllBytesAsync(firstSplitPath)), "the first split authorisation changed");
        Assert.True(predecessorBytes.SequenceEqual(await File.ReadAllBytesAsync(predecessorPath)), "the predecessor split authorisation changed");
        Assert.True(activeBytes.SequenceEqual(await File.ReadAllBytesAsync(activePath)), "the active split authorisation changed");

        // The artefacts were read and not touched.
        Assert.Equal(everyArtefact, await EveryArtefactAsync(universe));
        Assert.Equal(underPredecessor, await AcquisitionSplitBatchTests.ConsumedUnderAsync(universe, predecessor.AuthorizationId));
        Assert.Equal(underFirstSplit, await AcquisitionSplitBatchTests.ConsumedUnderAsync(universe, FirstSplitAuthorizationId));

        // Reading the accounting cannot have created work: the spend is still the whole ceiling and
        // still nothing remains, so nothing this test did could let a batch through afterwards.
        Assert.Equal(ExpectedActiveSpend, active.Consumed);
        Assert.Equal(0, active.Remaining);
        Assert.Equal(underActive, await AcquisitionSplitBatchTests.ConsumedUnderAsync(universe, active.AuthorizationId));

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());
    }

    // ---- reading -------------------------------------------------------------------------------

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// The digest a declaration records for the authorisation it replaces.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than from <see cref="AcquisitionAuthorization"/>, which does not
    /// surface it: the '@2' canonical form covers the superseded <em>identifier</em> and the carried
    /// spend, not this. That is exactly why it is worth comparing against the predecessor's own
    /// recomputed digest here - an unprotected field that nothing checks is documentation, and an
    /// unprotected field that something checks is evidence.
    /// </remarks>
    private static string SupersededDigestIn(byte[] declaration)
    {
        using var document = JsonDocument.Parse(declaration);

        return document.RootElement
            .GetProperty("SupersedesAuthorizationDigest")
            .GetString() ?? string.Empty;
    }

    /// <summary>
    /// The unscoped total: every split artefact, whatever it charged.
    /// </summary>
    /// <remarks>
    /// This is what the runner used to compute, kept here so the report can state the number the
    /// fix stopped using rather than describing it.
    /// </remarks>
    private static async Task<int> EveryArtefactAsync(string directory)
    {
        var total = 0;

        foreach (var path in Directory
            .EnumerateFiles(directory, AcquisitionSplitBatchTests.ArtefactPattern)
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            total += document.RootElement
                .GetProperty(AcquisitionSplitBatchTests.ConsumedProperty)
                .GetInt32();
        }

        return total;
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

                return new Ready(ticker, AcquisitionPlanning.SymbolFor(ticker));
            })
            .OrderBy(m => m.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Ready(string Ticker, string Symbol);
}
