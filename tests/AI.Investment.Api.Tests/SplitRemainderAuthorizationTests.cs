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
/// Installs the successor sized to the 143 the ledger still owes. Spends nothing, authorises nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called and no split request is dispatched.</strong> One declaration is
/// written and one artefact. Run, observation and quarantine counts are asserted unchanged either
/// side, and every batch in the re-cut partition is unauthorised.
/// </para>
/// <para>
/// <strong>Why a third authorisation rather than an edit to the second.</strong> The second is the
/// record of what was approved for the split phase and what it cost, including the one unit
/// <c>GPC.US</c> spent on a failure. Editing it would destroy the evidence that the shortfall
/// exists. It is left byte-identical and this stage asserts that; the successor names it, carries
/// its final spend forward as evidence, and takes a ceiling sized to the work that remains.
/// </para>
/// <para>
/// <strong>Every number here was approved before it was written.</strong> The digest and the file
/// hash are asserted against the literals reviewed at the preparation stage, so a declaration that
/// differs by a byte from the one that was read fails rather than installs.
/// </para>
/// </remarks>
public sealed class SplitRemainderAuthorizationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SPLIT_REMAINDER";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const string PriceDeclaration = "acquisition-eodhd-sample400.json";
    private const string PredecessorDeclaration = "acquisition-eodhd-splits-sample400.json";

    private const string RemainderDeclaration =
        "acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json";

    private const string RemainderAuthorizationId =
        "eodhd-sample400-splits-remainder-2021-09-to-2026-08";

    private const string PredecessorAuthorizationId =
        "eodhd-sample400-splits-2021-09-to-2026-08";

    private const string PredecessorDigest =
        "c5bf60b940f4455d5839c3613967f25ac0b46a32a482925319c8b0df3558ea13";

    /// <summary>The digest reviewed and approved before this declaration was allowed to exist.</summary>
    private const string ApprovedInternalDigest =
        "340c06ae125fc0cf8d83e66a042dbd6adec285f7e27731982d6d4ea5b4e80f03";

    /// <summary>And the file hash, so the bytes installed are the bytes that were read.</summary>
    private const string ApprovedFileDigest =
        "145a7aff6536419fd7ef7988f7c7918ae270f39668d412544708387b898e05ab";

    private const int ApprovedAlreadyConsumed = 210;
    private const int ApprovedPlanned = 355;
    private const int ApprovedSatisfied = 212;
    private const int ApprovedCeiling = 143;

    private const string SplitSource = "eodhd-splits";
    private const string DefaultSecurityKind = "Security";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The partition approved for this ceiling, written down so the runner is checked against it.</summary>
    private static readonly (int Index, string First, string Last, int Count, int Before, int Prior)[] Approved =
    [
        (1, "GPC.US", "GPC.US", 1, 143, 0),
        (2, "MYO.US", "SAIL.US", 70, 142, 1),
        (3, "SAMG.US", "ZS.US", 70, 72, 71),
        (4, "ZWS.US", "ZYME.US", 2, 2, 141),
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SplitRemainderAuthorizationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_remainder_authorisation_is_installed_and_authorises_nothing()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The split remainder authorisation is off. Set {GateVariable}=1 to run it. It "
            + "dispatches nothing.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        // ---- the universe and both records this one is built on top of ---------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);

        Assert.Equal(
            SealedDigest,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());

        var pricePath = Universe.RepositoryPath("declarations", PriceDeclaration);
        var priceBefore = await File.ReadAllBytesAsync(pricePath);

        var predecessorPath = Universe.RepositoryPath("declarations", PredecessorDeclaration);
        var predecessorBefore = await File.ReadAllBytesAsync(predecessorPath);
        var predecessor = AcquisitionAuthorization.Load(predecessorPath, SealedFingerprint);

        Assert.Equal(AcquisitionAuthorization.SupersedingSchema, predecessor.SchemaVersion);
        Assert.Equal(PredecessorAuthorizationId, predecessor.AuthorizationId);
        Assert.Equal(PredecessorDigest, predecessor.Digest);

        // ---- what the ledger owes, recomputed rather than carried over ----------------------------

        var members = await ReadyMembersAsync();

        Assert.True(members.Count == 355, Universe.Inv($"{members.Count} ready members, not 355"));

        var priorRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.CorporateActions)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorRun?.Request.Region ?? Region.Global;

        var outstanding = new List<string>();
        var satisfied = new List<string>();
        var probe = 0;

        foreach (var member in members)
        {
            var request = IngestionRequest.Create(
                SourceId.Create(SplitSource),
                DataCategory.CorporateActions,
                region,
                IngestionSubject.Create(subjectKind, member.Symbol),
                CorrelationId.Create(Universe.Inv($"remainder-plan-{probe++}")),
                clock.UtcNow,
                DateRange.Create(WindowStart, WindowEnd));

            if (await runStore.HasCompletedAsync(request.Fingerprint()))
            {
                satisfied.Add(member.Symbol);
            }
            else
            {
                outstanding.Add(member.Symbol);
            }
        }

        // Proof 7: the live ledger, not the report, decides what this authorisation is sized to.
        Assert.True(
            outstanding.Count == ApprovedCeiling,
            Universe.Inv($"the ledger owes {outstanding.Count} split requests and this authorisation was approved for {ApprovedCeiling}. Nothing was installed."));

        Assert.True(
            satisfied.Count == ApprovedSatisfied,
            Universe.Inv($"{satisfied.Count} split fingerprints are satisfied and {ApprovedSatisfied} were approved. Nothing was installed."));

        var spentOnSplits = await SplitConsumptionAsync();

        Assert.True(
            spentOnSplits == ApprovedAlreadyConsumed,
            Universe.Inv($"{spentOnSplits} dispatches were spent under the predecessor and {ApprovedAlreadyConsumed} were approved as the carried figure. Nothing was installed."));

        // ---- the declaration, built by the production digest algorithm ----------------------------

        var symbols = members.Select(m => m.Symbol).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(ApprovedPlanned, symbols.Count);

        var digest = AcquisitionAuthorization.DigestFor(
            AcquisitionAuthorization.SupersedingSchema,
            SealedFingerprint,
            predecessor.Vendor,
            [SplitSource],
            DateOnly.FromDateTime(WindowStart),
            DateOnly.FromDateTime(WindowEnd),
            symbols.Count,
            satisfied.Count,
            symbols.Count - satisfied.Count,
            symbols,
            predecessor.AuthorizationId,
            spentOnSplits);

        // Proof 2, first half: the digest is the one that was reviewed. Anything that moved -
        // a symbol, the window, the carried spend, the predecessor's name - changes it.
        Assert.True(
            string.Equals(digest, ApprovedInternalDigest, StringComparison.Ordinal),
            Universe.Inv($"the computed digest `{digest}` is not the approved `{ApprovedInternalDigest}`. Something changed between review and installation. Nothing was installed."));

        var content = JsonSerializer.Serialize(
            new
            {
                Schema = AcquisitionAuthorization.SupersedingSchema,
                AuthorizationId = RemainderAuthorizationId,
                EvidenceBaseFingerprint = SealedFingerprint,
                SupersedesAuthorizationId = predecessor.AuthorizationId,
                SupersedesAuthorizationDigest = predecessor.Digest,
                AlreadyConsumed = spentOnSplits,
                AlreadyConsumedNote = "Spent under the superseded authorisation and never credited "
                    + "back. Evidence only: this ceiling is sized to the work that remains, not "
                    + "inherited from the budget before it. What earlier authorisations in the "
                    + "chain spent is recovered by following SupersedesAuthorizationId back, not "
                    + "by accumulating it into this field.",
                Note = "A ceiling, not a licence. No request is dispatched until a batch in the "
                    + "split runner's partition is marked authorised, and none is.",
                Vendor = predecessor.Vendor,
                Sources = new[] { SplitSource },
                Endpoints = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SplitSource] = "api/splits/{symbol}",
                },
                Categories = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SplitSource] = nameof(DataCategory.CorporateActions),
                },
                WindowFromUtc = Universe.Inv($"{WindowStart:yyyy-MM-dd}"),
                WindowToUtc = Universe.Inv($"{WindowEnd:yyyy-MM-dd}"),
                SubjectKind = subjectKind,
                Region = region.Code,
                PlannedRequests = symbols.Count,
                AlreadySatisfied = satisfied.Count,
                DispatchCeiling = symbols.Count - satisfied.Count,
                AuthorizationDigest = digest,
                Symbols = symbols,
            },
            Universe.Json) + "\n";

        var contentBytes = Encoding.UTF8.GetBytes(content);
        var fileDigest = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();

        Assert.True(
            string.Equals(fileDigest, ApprovedFileDigest, StringComparison.Ordinal),
            Universe.Inv($"the file hash `{fileDigest}` is not the approved `{ApprovedFileDigest}`. These are not the bytes that were reviewed. Nothing was installed."));

        // Validated somewhere harmless first, so a declaration the loader would refuse never lands
        // in declarations/ even for the instant between writing it and reading it back.
        var candidate = LoadTemporary(content);

        Assert.Equal(ApprovedCeiling, candidate.DispatchCeiling);

        // ---- installed, then read back through the production loader ------------------------------

        var installedPath = Universe.RepositoryPath("declarations", RemainderDeclaration);

        await Universe.WriteAsync(installedPath, content);

        // Proof 1: it loads through the production loader from the path production would read.
        var installed = AcquisitionAuthorization.Load(installedPath, SealedFingerprint);

        Assert.Equal(AcquisitionAuthorization.SupersedingSchema, installed.SchemaVersion);
        Assert.Equal(RemainderAuthorizationId, installed.AuthorizationId);
        Assert.Equal(PredecessorAuthorizationId, installed.SupersedesAuthorizationId);
        Assert.Equal(ApprovedInternalDigest, installed.Digest);

        // Proof 3: the carried figure survives the round trip exactly.
        Assert.Equal(ApprovedAlreadyConsumed, installed.AlreadyConsumed);

        // Proof 5: the ceiling is 143, it equals planned less satisfied, and it equals the work the
        // ledger owes. Three independent statements of the same number, with no room between them
        // for headroom nobody approved.
        Assert.Equal(ApprovedCeiling, installed.DispatchCeiling);
        Assert.Equal(ApprovedPlanned, installed.PlannedRequests);
        Assert.Equal(ApprovedSatisfied, installed.AlreadySatisfied);
        Assert.Equal(installed.PlannedRequests - installed.AlreadySatisfied, installed.DispatchCeiling);
        Assert.Equal(outstanding.Count, installed.DispatchCeiling);
        Assert.Equal(0, installed.Consumed);
        Assert.Equal(ApprovedCeiling, installed.Remaining);

        Assert.Equal([SplitSource], installed.Sources.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(DateOnly.FromDateTime(WindowStart), installed.WindowFrom);
        Assert.Equal(DateOnly.FromDateTime(WindowEnd), installed.WindowTo);
        Assert.Equal(ApprovedPlanned, installed.Symbols.Count);

        // Proof 2, second half: both supersession fields are inside the digest. Rewriting either in
        // the installed file makes it refuse to load rather than quietly lie about its lineage.
        var tampered = new List<(string What, string Outcome)>();

        foreach (var (what, edited) in new[]
        {
            ("SupersedesAuthorizationId", content.Replace(
                Universe.Inv($"\"SupersedesAuthorizationId\": \"{PredecessorAuthorizationId}\""),
                "\"SupersedesAuthorizationId\": \"eodhd-sample400-prices-and-splits-2021-09-to-2026-08\"",
                StringComparison.Ordinal)),
            ("AlreadyConsumed", content.Replace(
                Universe.Inv($"\"AlreadyConsumed\": {ApprovedAlreadyConsumed},"),
                "\"AlreadyConsumed\": 0,",
                StringComparison.Ordinal)),
        })
        {
            Assert.NotEqual(content, edited);

            var failure = Record.Exception(() => LoadTemporary(edited));

            Assert.True(
                failure is not null,
                Universe.Inv($"{what} could be rewritten and the file still loaded, so that field is not digest-covered"));

            tampered.Add((what, "refused - the digest covers it"));
        }

        // ---- the partition, checked against the approval and against the ledger --------------------

        var partition = AcquisitionSplitBatchTests.Partition;

        // Proof 6: exactly the four batches that were approved, summing to exactly the ceiling.
        Assert.Equal(Approved.Length, partition.Length);
        Assert.Equal(ApprovedCeiling, partition.Sum(b => b.Count));

        foreach (var (index, first, last, count, before, prior) in Approved)
        {
            var batch = Array.Find(partition, b => b.Index == index);

            Assert.True(batch is not null, Universe.Inv($"the runner has no split batch {index}"));
            Assert.Equal(first, batch!.First);
            Assert.Equal(last, batch.Last);
            Assert.Equal(count, batch.Count);
            Assert.Equal(before, batch.ExpectedOutstandingBefore);
            Assert.Equal(prior, batch.ExpectedPriorConsumption);
            Assert.Equal(RemainderDeclaration, batch.Declaration);

            // Proof 7, per batch: each boundary resolves against the live ledger to the set that
            // was approved, and every symbol in it is covered by the authorisation just installed.
            var slice = outstanding
                .Where(s =>
                    string.CompareOrdinal(s, first) >= 0 &&
                    string.CompareOrdinal(s, last) <= 0)
                .ToList();

            Assert.True(
                slice.Count == count,
                Universe.Inv($"split batch {index} (`{first}`..`{last}`) resolves to {slice.Count} outstanding symbols and {count} were approved"));

            Assert.Equal(first, slice[0]);
            Assert.Equal(last, slice[^1]);

            foreach (var symbol in slice)
            {
                var covered = installed.Covers(
                    SplitSource,
                    symbol,
                    DateOnly.FromDateTime(WindowStart),
                    DateOnly.FromDateTime(WindowEnd));

                Assert.True(covered.Allowed, Universe.Inv($"`{symbol}`: {covered.Reason}"));
            }
        }

        // And between them the four batches cover the outstanding set exactly - no symbol owed but
        // unbatched, and no batched symbol the ledger does not owe.
        var batched = partition
            .SelectMany(b => outstanding.Where(s =>
                string.CompareOrdinal(s, b.First) >= 0 &&
                string.CompareOrdinal(s, b.Last) <= 0))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(outstanding.Order(StringComparer.Ordinal).ToList(), batched);

        // ---- the accounting seam the next approval will meet ---------------------------------------

        // Reported, not repaired. The runner totals every acquisition-splits-*.json artefact when it
        // works out what has already been spent, and those artefacts belong to the predecessor. The
        // partition above expects zero spent under the new authorisation, so batch 1 would refuse
        // before it reached a provider. That refusal is the accounting model working; making the
        // scan authorisation-aware is a change to the runner and is not part of this approval.
        var artefactTotal = await SplitConsumptionAsync();

        var wouldRefuse = artefactTotal != partition[0].ExpectedPriorConsumption;

        // ================================================================================
        // The report
        // ================================================================================

        report.AppendLine("# The split remainder authorisation, installed; nothing authorised");
        report.AppendLine();
        report.AppendLine("**No provider was called and no split request was dispatched.** One");
        report.AppendLine("declaration was written and one artefact. The price authorisation and the");
        report.AppendLine("superseded split authorisation were read and left byte-identical, and every");
        report.AppendLine("batch below is unauthorised.");
        report.AppendLine();

        report.AppendLine("## A. The installed authorisation");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| File | `declarations/{RemainderDeclaration}` |"));
        report.AppendLine(Universe.Inv($"| Schema | `{installed.SchemaVersion}` |"));
        report.AppendLine(Universe.Inv($"| Authorisation id | `{installed.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Internal digest | `{installed.Digest}` |"));
        report.AppendLine(Universe.Inv($"| File SHA-256 | `{fileDigest}` |"));
        report.AppendLine(Universe.Inv($"| Bytes | {contentBytes.Length} |"));
        report.AppendLine(Universe.Inv($"| Source | `{string.Join(", ", installed.Sources)}` |"));
        report.AppendLine(Universe.Inv($"| Window | {installed.WindowFrom:yyyy-MM-dd}..{installed.WindowTo:yyyy-MM-dd} |"));
        report.AppendLine(Universe.Inv($"| Subject kind / region | {subjectKind} / {region.Code} |"));
        report.AppendLine("| Loads through the production loader | yes |");
        report.AppendLine();

        report.AppendLine("## B. The predecessor");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Supersedes | `{installed.SupersedesAuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| …its digest | `{predecessor.Digest}` |"));
        report.AppendLine(Universe.Inv($"| …its ceiling / consumed | {predecessor.DispatchCeiling} / {spentOnSplits} |"));
        report.AppendLine(Universe.Inv($"| …bytes, before and after this stage | {predecessorBefore.Length} / {(await File.ReadAllBytesAsync(predecessorPath)).Length} |"));
        report.AppendLine("| …byte-identical | yes |");
        report.AppendLine("| Price authorisation byte-identical | yes |");
        report.AppendLine("| Sealed manifest byte-identical | yes |");
        report.AppendLine();
        report.AppendLine("Both supersession fields are digest-covered under this schema:");
        report.AppendLine();
        report.AppendLine("| Field rewritten | Outcome |");
        report.AppendLine("| --- | --- |");

        foreach (var (what, outcome) in tampered)
        {
            report.AppendLine(Universe.Inv($"| `{what}` | {outcome} |"));
        }

        report.AppendLine();

        report.AppendLine("## C. Carried spend and ceiling");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| `AlreadyConsumed`, carried as evidence | **{installed.AlreadyConsumed}** |"));
        report.AppendLine(Universe.Inv($"| Planned | {installed.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Already satisfied in the ledger | {installed.AlreadySatisfied} |"));
        report.AppendLine(Universe.Inv($"| **Dispatch ceiling** | **{installed.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Consumed under this authorisation | {installed.Consumed} |"));
        report.AppendLine(Universe.Inv($"| Remaining | {installed.Remaining} |"));
        report.AppendLine(Universe.Inv($"| Outstanding fingerprints in the live ledger | {outstanding.Count} |"));
        report.AppendLine();
        report.AppendLine("The ceiling equals planned less satisfied, and equals what the ledger owes.");
        report.AppendLine("The carried spend is evidence and is not subtracted from it: 210 dispatches");
        report.AppendLine("were made under the predecessor and none was credited back, which is why the");
        report.AppendLine("work outran that ceiling by one in the first place.");
        report.AppendLine();

        report.AppendLine(Universe.Inv($"## D. The partition - {partition.Length} batches, all unauthorised"));
        report.AppendLine();
        report.AppendLine("| Batch | Symbols | First | Last | Outstanding before | Prior consumption | Authorised |");
        report.AppendLine("| ---: | ---: | --- | --- | ---: | ---: | --- |");

        foreach (var batch in partition)
        {
            report.AppendLine(Universe.Inv($"| {batch.Index} | {batch.Count} | `{batch.First}` | `{batch.Last}` | {batch.ExpectedOutstandingBefore} | {batch.ExpectedPriorConsumption} | {(batch.Authorised ? "YES" : "no")} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"They sum to {partition.Sum(b => b.Count)}, and every batch resolves against the live"));
        report.AppendLine("ledger to exactly the symbols it names.");
        report.AppendLine();

        report.AppendLine("## E. Nothing is authorised");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Batches in the partition | {partition.Length} |"));
        report.AppendLine(Universe.Inv($"| **Batches authorised** | **{partition.Count(b => b.Authorised)}** |"));
        report.AppendLine();
        report.AppendLine("A ceiling is not a licence. Installing this declaration states the most that");
        report.AppendLine("could be spent if a batch were approved; no batch is, and the readiness tests");
        report.AppendLine("fail the build if one is flipped.");
        report.AppendLine();

        report.AppendLine("## F. Nothing was dispatched");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Ingestion runs before / after | {runsBefore} / {await context.IngestionRuns.AsNoTracking().CountAsync()} |"));
        report.AppendLine(Universe.Inv($"| Observations before / after | {observationsBefore} / {await context.Observations.AsNoTracking().CountAsync()} |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads before / after | {quarantineBefore} / {await context.QuarantinedPayloads.AsNoTracking().CountAsync()} |"));
        report.AppendLine();

        report.AppendLine("## G. Known, reported, NOT repaired");
        report.AppendLine();
        report.AppendLine("The runner totals every `acquisition-splits-*.json` artefact to work out prior");
        report.AppendLine(Universe.Inv($"consumption. Those artefacts belong to the predecessor and total {artefactTotal},"));
        report.AppendLine(Universe.Inv($"while batch 1 of this partition expects {partition[0].ExpectedPriorConsumption}."));
        report.AppendLine(Universe.Inv($"Batch 1 would therefore be REFUSED before reaching a provider: {wouldRefuse}."));
        report.AppendLine();
        report.AppendLine("That refusal is the accounting model working, not failing - it is the same");
        report.AppendLine("brittle pin that caught the shortfall. Making the scan authorisation-aware is a");
        report.AppendLine("change to the runner's accounting and is deliberately not part of this stage.");
        report.AppendLine("The artefacts already carry an `Authorization` field, so the fix is a filter.");
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "split-remainder-authorization.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -------------------------------------------------------------------------

        // Proof 4: the predecessor and the price record are untouched.
        Assert.True(
            predecessorBefore.SequenceEqual(await File.ReadAllBytesAsync(predecessorPath)),
            "the superseded split authorisation changed; it is a historical record and must not be amended");

        Assert.True(
            priceBefore.SequenceEqual(await File.ReadAllBytesAsync(pricePath)),
            "the price authorisation changed; it is a historical record and must not be amended");

        Assert.True(
            manifestBytes.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)),
            "the sealed manifest changed");

        // The installed bytes are the bytes that were reviewed.
        Assert.Equal(
            ApprovedFileDigest,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installedPath)))
                .ToLowerInvariant());

        // Proofs 8, 9 and 10: no batch authorised, and nothing in the store moved.
        Assert.DoesNotContain(AcquisitionSplitBatchTests.Partition, b => b.Authorised);
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());
    }

    // ---- reading -------------------------------------------------------------------------------

    private static AcquisitionAuthorization LoadTemporary(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"split-remainder-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, content);

            return AcquisitionAuthorization.Load(path, SealedFingerprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>What every split batch spent, from what each batch wrote about itself.</summary>
    private static async Task<int> SplitConsumptionAsync()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var total = 0;

        foreach (var path in Directory
            .EnumerateFiles(universe, "acquisition-splits-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            if (document.RootElement.TryGetProperty("AuthorizationConsumedThisRun", out var value))
            {
                total += value.GetInt32();
            }
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
