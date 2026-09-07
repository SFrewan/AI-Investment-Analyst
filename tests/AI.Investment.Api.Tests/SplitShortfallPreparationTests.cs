using System.Globalization;
using System.Reflection;
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
/// Establishes the one-unit shortfall, and drafts the authorisation that would close it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called, no split request is dispatched, and nothing is installed.</strong>
/// This stage reads the ledger, reads the archive, and writes one artefact. The declaration it
/// drafts is validated through the production loader in a temporary file and then deleted; nothing
/// reaches <c>declarations/</c> and no batch becomes authorised. Run, observation and quarantine
/// counts are asserted unchanged either side.
/// </para>
/// <para>
/// <strong>Why there is a shortfall at all.</strong> Authorisation is charged at intent: a dispatch
/// is claimed before the request leaves, and a failure does not give it back. That is the whole
/// point of the model - an authorisation that can be credited is one that can be reset - but it
/// means a failed attempt permanently converts one unit of budget into no data. <c>GPC.US</c> in
/// split batch 3 is the first time this project has spent a unit that produced nothing, so the
/// ledger now owes one more request than the ceiling can pay for.
/// </para>
/// <para>
/// <strong>What this stage refuses to do about it.</strong> It does not credit the unit back, raise
/// the ceiling, amend a digest, relax the loader's arithmetic, suppress the fingerprint, or touch
/// the seam. Section C proves each of those doors is shut by testing it rather than asserting it in
/// prose. The only resolution offered is a further superseding authorisation sized, like its
/// predecessors, to the work the ledger actually owes.
/// </para>
/// </remarks>
public sealed class SplitShortfallPreparationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SPLIT_SHORTFALL";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const string PriceDeclaration = "acquisition-eodhd-sample400.json";
    private const string SplitDeclaration = "acquisition-eodhd-splits-sample400.json";

    /// <summary>The identifier the drafted successor would carry. Nothing is written under it.</summary>
    private const string ProposedAuthorizationId =
        "eodhd-sample400-splits-remainder-2021-09-to-2026-08";

    private const string SplitSource = "eodhd-splits";
    private const string PriceSource = "eodhd-eod";
    private const string DefaultSecurityKind = "Security";

    /// <summary>The symbol whose attempt failed, and whose unit was spent on nothing.</summary>
    private const string ShortfallSymbol = "GPC.US";

    /// <summary>The rule the empty JSON array was first quarantined under, by the price normaliser.</summary>
    private const string EmptySeriesRule = "market-data.empty-series@1";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SplitShortfallPreparationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_shortfall_is_established_and_a_successor_is_drafted_but_not_installed()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The split shortfall preparation is off. Set {GateVariable}=1 to run it. It "
            + "dispatches nothing and installs nothing.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        // ---- the sealed universe and both standing declarations, unchanged ------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);

        Assert.Equal(
            SealedDigest,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());

        var pricePath = Universe.RepositoryPath("declarations", PriceDeclaration);
        var priceBefore = await File.ReadAllBytesAsync(pricePath);

        var splitPath = Universe.RepositoryPath("declarations", SplitDeclaration);
        var splitBefore = await File.ReadAllBytesAsync(splitPath);
        var superseded = AcquisitionAuthorization.Load(splitPath, SealedFingerprint);

        Assert.Equal(AcquisitionAuthorization.SupersedingSchema, superseded.SchemaVersion);

        // ================================================================================
        // A. The live accounting
        // ================================================================================

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
            var request = BuildProbe(member.Symbol, subjectKind, region, clock, probe++);

            if (await runStore.HasCompletedAsync(request.Fingerprint()))
            {
                satisfied.Add(member.Symbol);
            }
            else
            {
                outstanding.Add(member.Symbol);
            }
        }

        var spentOnSplits = await SplitConsumptionAsync();
        var spentOnPrices = await PriceConsumptionAsync();

        superseded.RecordPriorConsumption(spentOnSplits);

        // Fact 1: exactly 143 outstanding.
        Assert.True(
            outstanding.Count == 143,
            Universe.Inv($"the ledger owes {outstanding.Count} split requests, not 143. The reconciliation was cut against 143 and nothing else was done."));

        Assert.True(
            satisfied.Count == 212,
            Universe.Inv($"{satisfied.Count} split fingerprints are satisfied, not 212"));

        // Fact 2: 142 available, and it is less than the work.
        Assert.True(
            superseded.Remaining == 142,
            Universe.Inv($"{superseded.Remaining} dispatches remain against this ceiling, not 142"));

        Assert.True(
            superseded.Remaining < outstanding.Count,
            "there is no shortfall; this preparation stage exists only because there is one");

        Assert.Equal(1, outstanding.Count - superseded.Remaining);

        // ================================================================================
        // B. The cause, from the ledger rather than from the run's own account of itself
        // ================================================================================

        // Read once, in full, and answered in memory from here on. The questions below are about
        // artifact hashes and owned sub-properties, and a translation that silently fell back to
        // client evaluation would answer them from a different set of rows than it appeared to.
        var allRuns = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var failedHere = allRuns
            .Where(r =>
                r.Request.Category == DataCategory.CorporateActions &&
                string.Equals(r.Request.Subject.Identifier, ShortfallSymbol, StringComparison.Ordinal))
            .OrderBy(r => r.StartedAtUtc)
            .ToList();

        var failedAttempts = failedHere.Count(r => r.Outcome != IngestionOutcome.Succeeded);

        Assert.True(
            failedAttempts >= 1,
            Universe.Inv($"the ledger holds no failed corporate-actions run for `{ShortfallSymbol}`, so the shortfall has some other cause and this reconciliation does not describe it"));

        Assert.DoesNotContain(ShortfallSymbol, satisfied, StringComparer.Ordinal);
        Assert.Contains(ShortfallSymbol, outstanding, StringComparer.Ordinal);

        // Fact 3, second half: there is no way to give the unit back. Not "we chose not to" - the
        // type has no member that could, and Consumed cannot be assigned from outside.
        var creditBack = typeof(AcquisitionAuthorization)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .Where(n =>
                n.Contains("Release", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Reset", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Return", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Unconsume", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            creditBack.Count == 0,
            Universe.Inv($"AcquisitionAuthorization exposes {string.Join(", ", creditBack)}, which could return a spent unit"));

        var consumedSetter = typeof(AcquisitionAuthorization)
            .GetProperty(nameof(AcquisitionAuthorization.Consumed))?
            .SetMethod;

        Assert.True(consumedSetter is null, "Consumed has a setter, so the spend can be rewritten");

        // ================================================================================
        // C. Every door that would make 143 fit under 142, tried and shut
        // ================================================================================

        var doors = new List<(string Door, string Outcome)>();

        // C1. Spend past the ceiling. TryConsume refuses rather than clamping.
        var exhausted = AcquisitionAuthorization.Load(splitPath, SealedFingerprint);

        exhausted.RecordPriorConsumption(exhausted.DispatchCeiling);

        var overspend = exhausted.TryConsume(
            SplitSource,
            ShortfallSymbol,
            DateOnly.FromDateTime(WindowStart),
            DateOnly.FromDateTime(WindowEnd));

        Assert.False(overspend.Allowed, "a fully spent authorisation still allowed a dispatch");
        doors.Add(("Dispatch past the ceiling", Universe.Inv($"refused - {overspend.Reason}")));

        // C2. Retrospectively over-spend. RecordPriorConsumption throws rather than clamping.
        var retro = AcquisitionAuthorization.Load(splitPath, SealedFingerprint);
        var retroFailure = Record.Exception(() => retro.RecordPriorConsumption(retro.DispatchCeiling + 1));

        Assert.NotNull(retroFailure);
        doors.Add(("Record more prior spend than the ceiling", "refused - the loader throws"));

        // C3. Raise the ceiling in the file. The loader recomputes it from planned less satisfied.
        var raised = Tampered(splitBefore, (nameof(AcquisitionAuthorization.DispatchCeiling), 143));
        var raisedFailure = Record.Exception(() => LoadTemporary(raised));

        Assert.NotNull(raisedFailure);
        doors.Add(("Raise DispatchCeiling to 143", "refused - the digest no longer matches"));

        // C4. Raise the ceiling *and* the arithmetic that justifies it. The digest still covers both.
        var reworked = Tampered(
            splitBefore,
            (nameof(AcquisitionAuthorization.DispatchCeiling), 143),
            (nameof(AcquisitionAuthorization.AlreadySatisfied), 212));

        var reworkedFailure = Record.Exception(() => LoadTemporary(reworked));

        Assert.NotNull(reworkedFailure);
        doors.Add(("Raise the ceiling and restate AlreadySatisfied", "refused - the digest covers both"));

        // C5. Rewrite the carried spend, which is digest-covered under this schema.
        var restated = Tampered(splitBefore, (nameof(AcquisitionAuthorization.AlreadyConsumed), 0));
        var restatedFailure = Record.Exception(() => LoadTemporary(restated));

        Assert.NotNull(restatedFailure);
        doors.Add(("Rewrite AlreadyConsumed", "refused - the digest covers it"));

        // C6. Suppress the fingerprint instead. The ledger answers from what completed, and the
        // failed attempt did not complete, so no amount of asking makes it suppressible.
        var shortfallProbe = BuildProbe(ShortfallSymbol, subjectKind, region, clock, probe++);

        Assert.False(
            await runStore.HasCompletedAsync(shortfallProbe.Fingerprint()),
            "the ledger reports the failed fingerprint as completed, which would hide the shortfall rather than resolve it");

        doors.Add(("Let the ledger suppress the request", "refused - the fingerprint never completed"));

        // ================================================================================
        // D. The successor, drafted and validated, and installed nowhere
        // ================================================================================

        var symbols = members.Select(m => m.Symbol).Order(StringComparer.Ordinal).ToList();

        string DigestOf(int alreadySatisfied, int ceiling) =>
            AcquisitionAuthorization.DigestFor(
                AcquisitionAuthorization.SupersedingSchema,
                SealedFingerprint,
                superseded.Vendor,
                [SplitSource],
                DateOnly.FromDateTime(WindowStart),
                DateOnly.FromDateTime(WindowEnd),
                symbols.Count,
                alreadySatisfied,
                ceiling,
                symbols,
                superseded.AuthorizationId,
                spentOnSplits);

        string Declaration(int alreadySatisfied, int ceiling) => JsonSerializer.Serialize(
            new
            {
                Schema = AcquisitionAuthorization.SupersedingSchema,
                AuthorizationId = ProposedAuthorizationId,
                EvidenceBaseFingerprint = SealedFingerprint,
                SupersedesAuthorizationId = superseded.AuthorizationId,
                SupersedesAuthorizationDigest = superseded.Digest,
                AlreadyConsumed = spentOnSplits,
                AlreadyConsumedNote = "Spent under the superseded authorisation and never credited "
                    + "back. Evidence only: this ceiling is sized to the work that remains, not "
                    + "inherited from the budget before it. What earlier authorisations in the "
                    + "chain spent is recovered by following SupersedesAuthorizationId back, not "
                    + "by accumulating it into this field.",
                Note = "A ceiling, not a licence. No request is dispatched until a batch in the "
                    + "split runner's partition is marked authorised, and none is.",
                Vendor = superseded.Vendor,
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
                AlreadySatisfied = alreadySatisfied,
                DispatchCeiling = ceiling,
                AuthorizationDigest = DigestOf(alreadySatisfied, ceiling),
                Symbols = symbols,
            },
            Universe.Json) + "\n";

        // C7, and the point of the whole section: forging the digest does not help either. This is
        // a declaration claiming a ceiling of 143 while still claiming only the 3 fingerprints the
        // price phase satisfied, with a digest correctly computed over exactly those numbers - the
        // best a determined editor could do. The loader recomputes the arithmetic afterwards and
        // refuses it. The only file that loads with a ceiling of 143 is one that also declares 212
        // satisfied, which is a claim the ledger independently confirms or contradicts.
        var forged = Record.Exception(() => LoadTemporary(Declaration(3, 143)));

        Assert.NotNull(forged);
        doors.Add(("Forge a valid digest over a ceiling of 143", "refused - the loader recomputes planned less satisfied"));

        var draft = Declaration(satisfied.Count, symbols.Count - satisfied.Count);
        var draftDigest = DigestOf(satisfied.Count, symbols.Count - satisfied.Count);

        Assert.Contains(draftDigest, draft, StringComparison.Ordinal);

        // Validated through the production loader, in a temporary file, and deleted. The draft is
        // reported so it can be reviewed; it is not written where anything could load it.
        var candidate = LoadTemporary(draft);

        Assert.Equal(AcquisitionAuthorization.SupersedingSchema, candidate.SchemaVersion);
        Assert.Equal(superseded.AuthorizationId, candidate.SupersedesAuthorizationId);
        Assert.NotEqual(superseded.AuthorizationId, candidate.AuthorizationId);
        Assert.NotEqual(superseded.Digest, candidate.Digest);
        Assert.Equal(spentOnSplits, candidate.AlreadyConsumed);
        Assert.Equal(outstanding.Count, candidate.DispatchCeiling);
        Assert.Equal(symbols.Count, candidate.PlannedRequests);
        Assert.Equal(satisfied.Count, candidate.AlreadySatisfied);
        Assert.Equal(0, candidate.Consumed);
        Assert.Equal([SplitSource], candidate.Sources.Order(StringComparer.Ordinal).ToList());

        var draftBytes = Encoding.UTF8.GetBytes(draft);
        var draftFileDigest = Convert.ToHexString(SHA256.HashData(draftBytes)).ToLowerInvariant();

        // ---- the partition the successor would carry ------------------------------------------------

        // Deliberately not a fresh cut. Batches 4, 5 and 6 of the standing partition were approved
        // against boundaries the ledger still agrees with, and re-cutting 143 from scratch would
        // move every one of them for the sake of one symbol. The recovery batch goes first instead,
        // which returns the outstanding count to 142 and leaves the approved rows arithmetically
        // exactly as they are.
        var carried = AcquisitionSplitBatchTests.Partition
            .Where(b => b.Index >= 4)
            .OrderBy(b => b.Index)
            .ToList();

        var carriedSymbols = outstanding
            .Where(s => carried.Any(b =>
                string.CompareOrdinal(s, b.First) >= 0 &&
                string.CompareOrdinal(s, b.Last) <= 0))
            .ToList();

        var recovery = outstanding
            .Where(s => !carriedSymbols.Contains(s, StringComparer.Ordinal))
            .ToList();

        Assert.Equal([ShortfallSymbol], recovery);
        Assert.Equal(142, carriedSymbols.Count);

        foreach (var batch in carried)
        {
            var slice = outstanding
                .Where(s =>
                    string.CompareOrdinal(s, batch.First) >= 0 &&
                    string.CompareOrdinal(s, batch.Last) <= 0)
                .ToList();

            Assert.True(
                slice.Count == batch.Count,
                Universe.Inv($"standing batch {batch.Index} (`{batch.First}`..`{batch.Last}`) now resolves to {slice.Count} outstanding symbols, not the {batch.Count} it was approved for; the boundaries cannot be carried forward unchanged"));
        }

        // The recovery batch is ordinally ahead of everything carried, so it is batch 1 of the
        // successor rather than an appendix, and the outstanding count each later batch expects is
        // the one it already expects today.
        Assert.True(
            string.CompareOrdinal(recovery[0], carried[0].First) < 0,
            Universe.Inv($"`{recovery[0]}` does not sort before `{carried[0].First}`, so it cannot run first"));

        var proposed = new List<(int Index, string First, string Last, int Count, int Before, int Prior)>();
        var runningPrior = 0;
        var runningBefore = outstanding.Count;

        proposed.Add((1, recovery[0], recovery[0], 1, runningBefore, runningPrior));
        runningPrior += 1;
        runningBefore -= 1;

        foreach (var batch in carried)
        {
            proposed.Add((proposed.Count + 1, batch.First, batch.Last, batch.Count, runningBefore, runningPrior));
            runningPrior += batch.Count;
            runningBefore -= batch.Count;
        }

        Assert.Equal(0, runningBefore);
        Assert.Equal(outstanding.Count, runningPrior);
        Assert.Equal(outstanding.Count, proposed.Sum(p => p.Count));

        // Every carried batch keeps the outstanding-before figure it carries today.
        foreach (var batch in carried)
        {
            var row = proposed.Single(p =>
                string.Equals(p.First, batch.First, StringComparison.Ordinal));

            Assert.True(
                row.Before == batch.ExpectedOutstandingBefore,
                Universe.Inv($"batch `{batch.First}`..`{batch.Last}` would expect {row.Before} outstanding where it currently declares {batch.ExpectedOutstandingBefore}; the point of the recovery-first ordering is that this number does not move"));
        }

        // ================================================================================
        // E. The empty-body provenance finding
        // ================================================================================

        // Fifty rows. Read whole and filtered in memory, for the same reason the runs were.
        var allQuarantined = await context.QuarantinedPayloads.AsNoTracking().ToListAsync();

        var emptyRows = allQuarantined
            .Where(q => string.Equals(q.RuleId, EmptySeriesRule, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            emptyRows.Count == 1,
            Universe.Inv($"{emptyRows.Count} payloads are quarantined under `{EmptySeriesRule}`, not 1; the finding below describes a single shared record"));

        var emptyRow = emptyRows[0];
        var emptyBytes = await archive.RetrieveAsync(emptyRow.Id);

        var touching = allRuns
            .Where(r => r.Artifacts.Any(h => string.Equals(h.Value, emptyRow.Id.Value, StringComparison.Ordinal)))
            .ToList();

        var bySource = touching
            .GroupBy(r => r.Request.SourceId.Value, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (Runs: g.Count(),
                    Symbols: g.Select(r => r.Request.Subject.Identifier ?? "(none)")
                        .Distinct(StringComparer.Ordinal)
                        .Count()),
                StringComparer.Ordinal);

        var splitSymbolsOnThisPayload = touching
            .Where(r => string.Equals(r.Request.SourceId.Value, SplitSource, StringComparison.Ordinal))
            .Select(r => r.Request.Subject.Identifier ?? "(none)")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The record itself belongs to the price source: that is who first failed to read these
        // bytes, and it is accurate. What is not accurate is reading the survey's "symbols
        // affected" column as a list of symbols whose payloads were quarantined.
        Assert.Equal(PriceSource, emptyRow.SourceId.Value);

        var splitRuns = allRuns
            .Count(r => string.Equals(r.Request.SourceId.Value, SplitSource, StringComparison.Ordinal));

        // Not one of them was quarantined: the splits normaliser treats an empty array as a fact.
        var splitPayloadsQuarantined = allQuarantined
            .Count(q => string.Equals(q.SourceId.Value, SplitSource, StringComparison.Ordinal));

        Assert.Equal(0, splitPayloadsQuarantined);

        // ================================================================================
        // The report
        // ================================================================================

        report.AppendLine("# The one-unit split shortfall: reconciliation and draft resolution");
        report.AppendLine();
        report.AppendLine("**Nothing was dispatched, nothing was installed, nothing was reprocessed.**");
        report.AppendLine("One artefact was written. Both standing declarations and the sealed manifest");
        report.AppendLine("were read and left byte-identical, and no batch became authorised.");
        report.AppendLine();

        report.AppendLine("## A. Live accounting");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Ready members | {members.Count} |"));
        report.AppendLine(Universe.Inv($"| Split fingerprints satisfied | {satisfied.Count} |"));
        report.AppendLine(Universe.Inv($"| **Outstanding split fingerprints** | **{outstanding.Count}** |"));
        report.AppendLine(Universe.Inv($"| Standing ceiling | {superseded.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| …consumed | {superseded.Consumed} |"));
        report.AppendLine(Universe.Inv($"| …**remaining** | **{superseded.Remaining}** |"));
        report.AppendLine(Universe.Inv($"| **Shortfall** | **{outstanding.Count - superseded.Remaining}** |"));
        report.AppendLine(Universe.Inv($"| Spent under the split authorisation | {spentOnSplits} |"));
        report.AppendLine(Universe.Inv($"| Spent under the price authorisation before it | {spentOnPrices} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs | {runsBefore} |"));
        report.AppendLine(Universe.Inv($"| Observations | {observationsBefore} |"));
        report.AppendLine();

        report.AppendLine("## B. The cause");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"`{ShortfallSymbol}` holds {failedHere.Count} corporate-actions run(s) in the ledger, of which"));
        report.AppendLine(Universe.Inv($"{failedAttempts} did not succeed. Its fingerprint is not among the {satisfied.Count} satisfied,"));
        report.AppendLine("so the work is still owed - but the unit that was claimed for it before");
        report.AppendLine("dispatch was never returned, because the authorisation has no member that");
        report.AppendLine("could return it and `Consumed` has no setter. One unit of ceiling was");
        report.AppendLine("converted into no data, permanently, by design.");
        report.AppendLine();

        report.AppendLine("## C. Doors that would make 143 fit under 142");
        report.AppendLine();
        report.AppendLine("Each was tried against production code in this run, not argued in prose.");
        report.AppendLine();
        report.AppendLine("| Door | Outcome |");
        report.AppendLine("| --- | --- |");

        foreach (var (door, outcome) in doors)
        {
            report.AppendLine(Universe.Inv($"| {door} | {outcome} |"));
        }

        report.AppendLine();

        report.AppendLine("## D. The drafted successor - NOT INSTALLED");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Schema | `{candidate.SchemaVersion}` |"));
        report.AppendLine(Universe.Inv($"| Authorisation id | `{candidate.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Supersedes | `{candidate.SupersedesAuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| …its digest | `{superseded.Digest}` |"));
        report.AppendLine(Universe.Inv($"| Already consumed, as evidence | **{candidate.AlreadyConsumed}** |"));
        report.AppendLine(Universe.Inv($"| Source | `{string.Join(", ", candidate.Sources)}` |"));
        report.AppendLine(Universe.Inv($"| Window | {candidate.WindowFrom:yyyy-MM-dd}..{candidate.WindowTo:yyyy-MM-dd} |"));
        report.AppendLine(Universe.Inv($"| Subject kind / region | {subjectKind} / {region.Code} |"));
        report.AppendLine(Universe.Inv($"| Symbols | {candidate.Symbols.Count} |"));
        report.AppendLine(Universe.Inv($"| Planned | {candidate.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Already satisfied | {candidate.AlreadySatisfied} |"));
        report.AppendLine(Universe.Inv($"| **Dispatch ceiling** | **{candidate.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Internal digest | `{candidate.Digest}` |"));
        report.AppendLine(Universe.Inv($"| File SHA-256, were it written | `{draftFileDigest}` |"));
        report.AppendLine(Universe.Inv($"| Bytes | {draftBytes.Length} |"));
        report.AppendLine();
        report.AppendLine("The ceiling is forced, not chosen: the loader recomputes it as planned less");
        report.AppendLine("satisfied and refuses any file where the two disagree, so 143 is the only");
        report.AppendLine("number this declaration could carry over this universe and this ledger.");
        report.AppendLine();

        report.AppendLine(Universe.Inv($"## D2. The partition it would carry - {proposed.Count} batches, all unauthorised"));
        report.AppendLine();
        report.AppendLine("| Batch | Symbols | First | Last | Outstanding before | Prior consumption |");
        report.AppendLine("| ---: | ---: | --- | --- | ---: | ---: |");

        foreach (var (i, first, last, count, before, prior) in proposed)
        {
            report.AppendLine(Universe.Inv($"| {i} | {count} | `{first}` | `{last}` | {before} | {prior} |"));
        }

        report.AppendLine();
        report.AppendLine("```csharp");

        foreach (var (i, first, last, count, before, prior) in proposed)
        {
            report.AppendLine(Universe.Inv(
                $"new({i}, \"{first}\", \"{last}\", {count}, {before}, {prior}, SplitDeclaration, Authorised: false),"));
        }

        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("The recovery batch runs first on purpose. It returns the outstanding count");
        report.AppendLine("to 142 before anything else moves, which is why every carried batch keeps the");
        report.AppendLine("boundaries and the outstanding-before figure it already declares.");
        report.AppendLine();

        report.AppendLine("## E. Provenance: the shared empty body");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Payload | `{emptyRow.Id.Abbreviated}` |"));
        report.AppendLine(Universe.Inv($"| Bytes | {emptyBytes?.Length ?? -1} |"));
        report.AppendLine(Universe.Inv($"| Rule | `{emptyRow.RuleId}` |"));
        report.AppendLine(Universe.Inv($"| Recorded against source | `{emptyRow.SourceId.Value}` |"));
        report.AppendLine(Universe.Inv($"| Recorded against category | `{emptyRow.Category}` |"));
        report.AppendLine(Universe.Inv($"| Runs whose artifacts contain it | {touching.Count} |"));

        foreach (var (source, counts) in bySource.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv($"| …from `{source}` | {counts.Runs} run(s), {counts.Symbols} symbol(s) |"));
        }

        report.AppendLine(Universe.Inv($"| **`{SplitSource}` symbols listed against it** | **{splitSymbolsOnThisPayload.Count}** |"));
        report.AppendLine(Universe.Inv($"| `{SplitSource}` runs in the ledger | {splitRuns} |"));
        report.AppendLine(Universe.Inv($"| `{SplitSource}` payloads actually quarantined | {splitPayloadsQuarantined} |"));
        report.AppendLine();
        report.AppendLine("### Why they attach");
        report.AppendLine();
        report.AppendLine("1. `ContentHash.Compute` hashes the payload bytes and nothing else - not the");
        report.AppendLine("   source, the category or the subject.");
        report.AppendLine("2. An empty JSON array is two bytes and is byte-identical whichever endpoint");
        report.AppendLine("   returned it, so one hash names every empty body the platform has ever held.");
        report.AppendLine("3. `QuarantinedPayload` is keyed by that hash alone. Its `SourceId` and");
        report.AppendLine("   `Category` are ordinary fields captured when the row was first written.");
        report.AppendLine("4. `NormalizationPipeline.QuarantineAsync` returns early when the hash is");
        report.AppendLine("   already quarantined, so the first writer's source label is the only one");
        report.AppendLine("   the row will ever carry.");
        report.AppendLine("5. `EodhdSplitsNormalizer` does **not** quarantine an empty array - it states");
        report.AppendLine("   that an empty array is a fact, not a failure, and returns zero");
        report.AppendLine("   observations. No split payload has ever been quarantined.");
        report.AppendLine("6. The survey builds its `symbols affected` column by grouping every");
        report.AppendLine("   ingestion run's artifacts by hash, with no filter on source. Split runs");
        report.AppendLine("   that archived the same two bytes therefore appear beside a quarantine");
        report.AppendLine("   record that was never about them.");
        report.AppendLine();
        report.AppendLine("The record is accurate about itself; the survey column is what misleads. This");
        report.AppendLine("is a lineage-reporting defect, not an acquisition failure and not a data");
        report.AppendLine("defect: every affected split run succeeded, completed its fingerprint, and");
        report.AppendLine("correctly recorded zero splits.");
        report.AppendLine();

        report.AppendLine("## What was not touched");
        report.AppendLine();
        report.AppendLine("No provider call. No split dispatch. No declaration installed or amended. No");
        report.AppendLine("batch authorised. No parser, quarantine record, transport setting, manifest,");
        report.AppendLine("identity table or seam behaviour changed. Nothing reprocessed.");
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "split-shortfall.md"),
            report.ToString());

        // The draft itself, beside the report, under a name nothing loads.
        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "split-remainder-authorization.draft.json"),
            draft);

        _output.WriteLine(report.ToString());

        // ---- the invariants ---------------------------------------------------------------------

        Assert.True(
            priceBefore.SequenceEqual(await File.ReadAllBytesAsync(pricePath)),
            "the price authorisation changed; it is a historical record and must not be amended");

        Assert.True(
            splitBefore.SequenceEqual(await File.ReadAllBytesAsync(splitPath)),
            "the split authorisation changed; it is the standing record and must not be amended");

        Assert.True(
            manifestBytes.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)),
            "the sealed manifest changed");

        Assert.False(
            File.Exists(Universe.RepositoryPath("declarations",
                ProposedAuthorizationId + ".json")),
            "the drafted successor reached declarations/, which would make it loadable");

        // Nothing was dispatched, and no batch became runnable.
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());
        Assert.DoesNotContain(AcquisitionSplitBatchTests.Partition, b => b.Authorised);
    }

    // ---- reading -------------------------------------------------------------------------------

    private static IngestionRequest BuildProbe(
        string symbol,
        string subjectKind,
        Region region,
        IClock clock,
        int ordinal) =>
        IngestionRequest.Create(
            SourceId.Create(SplitSource),
            DataCategory.CorporateActions,
            region,
            IngestionSubject.Create(subjectKind, symbol),
            CorrelationId.Create(Universe.Inv($"shortfall-probe-{ordinal}")),
            clock.UtcNow,
            DateRange.Create(WindowStart, WindowEnd));

    /// <summary>Loads a declaration from a temporary file, and always deletes it.</summary>
    private static AcquisitionAuthorization LoadTemporary(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"split-shortfall-{Guid.NewGuid():N}.json");

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

    /// <summary>
    /// The standing declaration with one or more integer fields rewritten.
    /// </summary>
    /// <remarks>
    /// Textual replacement rather than a re-serialise, so the tampering is exactly what somebody
    /// editing the file by hand would produce - which is the threat being tested.
    /// </remarks>
    private static string Tampered(byte[] original, params (string Property, int Value)[] edits)
    {
        var text = Encoding.UTF8.GetString(original);

        foreach (var (property, value) in edits)
        {
            var needle = Universe.Inv($"\"{property}\": ");
            var start = text.IndexOf(needle, StringComparison.Ordinal);

            Assert.True(start >= 0, Universe.Inv($"the declaration has no `{property}` field to tamper with"));

            var from = start + needle.Length;
            var to = from;

            while (to < text.Length && (char.IsAsciiDigit(text[to]) || text[to] == '-'))
            {
                to++;
            }

            text = string.Concat(
                text.AsSpan(0, from),
                value.ToString(CultureInfo.InvariantCulture),
                text.AsSpan(to));
        }

        return text;
    }

    /// <summary>What every split batch spent, from what each batch wrote about itself.</summary>
    private static async Task<int> SplitConsumptionAsync() =>
        await SumAsync("acquisition-splits-*.json", "AuthorizationConsumedThisRun");

    /// <summary>And what the price phase spent before it, for the record.</summary>
    private static async Task<int> PriceConsumptionAsync()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var total = 0;

        var outcome = Path.Combine(universe, "acquisition-outcome.json");

        if (File.Exists(outcome))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outcome));

            total += document.RootElement.GetProperty("AuthorizationConsumed").GetInt32();
        }

        return total + await SumAsync("acquisition-batch-*.json", "AuthorizationConsumedThisRun");
    }

    private static async Task<int> SumAsync(string pattern, string property)
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var total = 0;

        foreach (var path in Directory
            .EnumerateFiles(universe, pattern)
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            if (document.RootElement.TryGetProperty(property, out var value))
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
