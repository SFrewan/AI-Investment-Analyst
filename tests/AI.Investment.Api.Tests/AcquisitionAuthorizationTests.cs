using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The last look before money is authorised, and the first one that assumes nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only. No provider is called.</strong> Run, observation, execution and audit counts
/// are asserted unchanged either side, and the sealed manifest's bytes and digest are compared
/// against the approved values rather than recomputed into agreement with themselves.
/// </para>
/// <para>
/// This exists because every number in the authorisation has now been wrong at least once: the
/// billable spend was stated as one and the ledger holds eighty-six runs; the coverage gate was
/// reported as failing when nothing had been asked for; the identity table was silently reverted
/// by the stage meant to read it. Each was found by looking rather than by reasoning, and this is
/// the looking, gathered in one place and made to fail rather than to reassure.
/// </para>
/// </remarks>
public sealed class AcquisitionAuthorizationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_ACQUISITION_AUDIT";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    /// <summary>The approved manifest's digest, pinned rather than recomputed.</summary>
    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const int SealedBytes = 245126;

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";
    private const string VendorPrefix = "eodhd";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public AcquisitionAuthorizationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Everything_the_authorisation_rests_on_is_checked_before_it_is_granted()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The pre-acquisition audit is off. Set {GateVariable}=1 to run it. It reads only.");

        var watch = Stopwatch.StartNew();
        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var configuration = services.GetRequiredService<IConfiguration>();
        var eodhd = services.GetRequiredService<IOptions<EodhdOptions>>().Value;

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var executionsBefore = await context.ActionExecutions.AsNoTracking().CountAsync();
        var auditsBefore = await context.AuditRecords.AsNoTracking().CountAsync();

        report.AppendLine("# Pre-acquisition authorisation audit");
        report.AppendLine();
        report.AppendLine("**Read-only. No provider was called.** Every figure below is read from the");
        report.AppendLine("repository, the ledger or the running host's own configuration. Nothing was");
        report.AppendLine("acquired and nothing was written to the store.");
        report.AppendLine();

        // ---- 1. the manifest, before anything is said about it ---------------------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var digest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();

        report.AppendLine("## The sealed manifest");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        report.AppendLine(Universe.Inv($"| Fingerprint | `{SealedFingerprint}` |"));
        report.AppendLine(Universe.Inv($"| Bytes | {manifestBytes.Length} against the approved {SealedBytes} |"));
        report.AppendLine(Universe.Inv($"| SHA-256 | `{digest}` |"));
        report.AppendLine(Universe.Inv($"| Matches the approved digest | {(string.Equals(digest, SealedDigest, StringComparison.Ordinal) ? "**yes**" : "**NO**")} |"));
        report.AppendLine();

        // ---- 2. the budget, and what the repository actually says about it ----------------------------

        var maxActionsPerDay = configuration.GetValue<int?>("Limits:MaxActionsPerCapabilityPerDay");
        var maxCostPerCycle = configuration.GetValue<decimal?>("Limits:MaxCostPerCycle");
        var perMinute = eodhd.MaxRequestsPerMinute;

        var ingestion = configuration
            .GetSection("Safety:Capabilities")
            .GetChildren()
            .FirstOrDefault(c => string.Equals(
                c["Capability"], nameof(Capability.DataIngestion), StringComparison.Ordinal));

        report.AppendLine("## What the repository authorises");
        report.AppendLine();
        report.AppendLine("| Control | Value | What it limits |");
        report.AppendLine("| --- | --- | --- |");
        report.AppendLine(Universe.Inv($"| `Providers:Eodhd:Enabled` | **{eodhd.Enabled}** | whether the connector is registered at all |"));
        report.AppendLine(Universe.Inv($"| `Providers:Eodhd:ApiKey` | {(string.IsNullOrWhiteSpace(eodhd.ApiKey) ? "absent" : "**present**")} | whether a dispatch could actually reach the vendor |"));
        report.AppendLine(Universe.Inv($"| `Providers:Eodhd:MaxRequestsPerMinute` | {perMinute} | pacing, not spend |"));
        report.AppendLine(Universe.Inv($"| Configured exchange sessions | {(eodhd.Exchanges.Count == 0 ? "none" : string.Join(", ", eodhd.Exchanges.Select(x => Universe.Inv($"{x.Code} close {x.SessionCloseUtc:hh\\:mm} +{x.PublicationDelay:hh\\:mm}"))))} | a symbol on any other exchange quarantines |"));
        report.AppendLine(Universe.Inv($"| `Limits:MaxActionsPerCapabilityPerDay` | {maxActionsPerDay?.ToString(CultureInfo.InvariantCulture) ?? "unset"} | actions through the seam, per capability, per day |"));
        report.AppendLine(Universe.Inv($"| `Limits:MaxCostPerCycle` | {maxCostPerCycle?.ToString(CultureInfo.InvariantCulture) ?? "unset"} | cost per operating cycle |"));
        report.AppendLine(Universe.Inv($"| `Safety:Capabilities` → DataIngestion | enabled={ingestion?["Enabled"] ?? "absent"}, tier={ingestion?["MaxAutoExecuteRiskTier"] ?? "absent"}, irreversible={ingestion?["AllowIrreversibleAutoExecute"] ?? "absent"} | what may auto-execute |"));
        report.AppendLine(Universe.Inv($"| Manifest `FamilyBudget` | {Universe.FamilyBudget} | hypothesis families, **not** provider calls |"));
        report.AppendLine();
        report.AppendLine("**There is no provider-spend budget anywhere in the repository.** Not in the");
        report.AppendLine("declarations, not in configuration, not in the manifest. Every stage budget so");
        report.AppendLine("far has lived in a constant inside a test class or in the approval itself, and");
        report.AppendLine("neither is a thing the platform can enforce or a later reader can find. The");
        report.AppendLine("figures above are pacing and policy limits; none of them caps spend.");
        report.AppendLine();

        // ---- 3. the ledger, as accounting -------------------------------------------------------------

        var ledger = await context.IngestionRuns.AsNoTracking().ToListAsync();

        var vendor = ledger
            .Where(r => r.Request.SourceId.Value.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var dispatched = vendor.Count(r => r.Outcome != IngestionOutcome.Refused);

        var satisfied = ledger
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .Select(r => r.Request.Fingerprint())
            .ToHashSet(StringComparer.Ordinal);

        // A failed or refused run must not have archived anything. If one had, a failure could
        // become an observation by the normal path, which is the single worst thing this ledger
        // could allow: the store would hold a number nobody fetched.
        var failedWithPayload = vendor
            .Where(r => r.Outcome is IngestionOutcome.Failed or IngestionOutcome.Refused)
            .Where(r => r.Artifacts.Count > 0)
            .ToList();

        var executions = (await context.ActionExecutions.AsNoTracking().ToListAsync())
            .Select(e => e.IdempotencyKey)
            .ToHashSet(StringComparer.Ordinal);

        var throughSeam = vendor.Count(r => executions.Contains(r.Request.Fingerprint()));

        var quarantined = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        report.AppendLine("## Historical spend, as the ledger records it");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Runs against an `{VendorPrefix}` source | {vendor.Count} |"));
        report.AppendLine(Universe.Inv($"| Dispatched, not refused | {dispatched} |"));
        report.AppendLine(Universe.Inv($"| Distinct request identities | {vendor.Select(r => r.Request.Fingerprint()).Distinct(StringComparer.Ordinal).Count()} |"));
        report.AppendLine(Universe.Inv($"| …of which carry a seam execution | {throughSeam} |"));
        report.AppendLine(Universe.Inv($"| Failed or refused runs holding a payload | **{failedWithPayload.Count}** |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads | {quarantined} |"));
        report.AppendLine();
        report.AppendLine("This is spend already made. It is not a budget, and the repository holds no");
        report.AppendLine("statement of what remains authorised - that lives only in the approval.");
        report.AppendLine();

        // ---- 4. the 355 identities ---------------------------------------------------------------------

        var members = await MembersAsync();

        var ready = members.Where(m => m.Ready).ToList();
        var excluded = members.Where(m => !m.Ready).ToList();

        var malformedTicker = ready
            .Where(m => m.Ticker is null || !WellFormedTicker(m.Ticker))
            .ToList();

        var notAuthoritative = ready
            .Where(m => !IdentityResolution.IsAuthoritative(m.Status))
            .ToList();

        var nonEquity = ready
            .Where(m => m.SecurityKind is not null &&
                !string.Equals(m.SecurityKind, "equity", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var duplicateTickers = ready
            .GroupBy(m => m.Ticker!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        report.AppendLine("## The 355 acquisition-ready identities");
        report.AppendLine();
        report.AppendLine("| Check | Result |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Members in the sealed universe | {members.Count} |"));
        report.AppendLine(Universe.Inv($"| Acquisition-ready | {ready.Count} |"));
        report.AppendLine(Universe.Inv($"| Not ready, and still members | {excluded.Count} |"));
        report.AppendLine(Universe.Inv($"| Ready with a malformed or missing ticker | **{malformedTicker.Count}** |"));
        report.AppendLine(Universe.Inv($"| Ready resting on a non-authoritative status | **{notAuthoritative.Count}** |"));
        report.AppendLine(Universe.Inv($"| Ready classified as anything but equity | **{nonEquity.Count}** |"));
        report.AppendLine(Universe.Inv($"| Tickers shared by two ready members | **{duplicateTickers.Count}** |"));
        report.AppendLine();

        report.AppendLine("| Ready, by status | Members |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in ready.GroupBy(m => m.Status, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| `{group.Key}` | {group.Count()} |"));
        }

        report.AppendLine();
        report.AppendLine("| Excluded, by status | Members |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in excluded.GroupBy(m => m.Status, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| `{group.Key}` | {group.Count()} |"));
        }

        report.AppendLine();

        // ---- 5. the 710 requests -------------------------------------------------------------------------

        var priorPriceRun = ledger
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefault();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? "Security";
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        var first = Build(ready, subjectKind, region, clock.UtcNow, "audit-a");
        var second = Build(ready, subjectKind, region, clock.UtcNow.AddDays(11), "audit-b");

        var deterministic = first.Count == second.Count &&
            first.Zip(second).All(p => string.Equals(p.First.Fingerprint, p.Second.Fingerprint, StringComparison.Ordinal));

        var distinct = first.Select(p => p.Fingerprint).Distinct(StringComparer.Ordinal).Count();
        var suppressed = first.Where(p => satisfied.Contains(p.Fingerprint)).ToList();
        var net = first.Count - suppressed.Count;

        var wrongWindow = first.Count(p => !string.Equals(p.Window, "2021-09-01..2026-08-31", StringComparison.Ordinal));
        var wrongEndpoint = first.Count(p => p.Source is not (PriceSource or ActionsSource));
        var wrongCategory = first.Count(p =>
            (p.Source == PriceSource && p.Category != nameof(DataCategory.MarketPrices)) ||
            (p.Source == ActionsSource && p.Category != nameof(DataCategory.CorporateActions)));

        report.AppendLine("## The 710 planned requests");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Planned requests | {first.Count} |"));
        report.AppendLine(Universe.Inv($"| Distinct fingerprints | {distinct} |"));
        report.AppendLine(Universe.Inv($"| Already satisfied in the ledger | {suppressed.Count} |"));
        report.AppendLine(Universe.Inv($"| **Net fetches** | **{net}** |"));
        report.AppendLine(Universe.Inv($"| Deterministic across two builds with different clocks and correlation ids | {(deterministic ? "**yes**" : "**NO**")} |"));
        report.AppendLine(Universe.Inv($"| Requests carrying the wrong window | **{wrongWindow}** |"));
        report.AppendLine(Universe.Inv($"| Requests on an unexpected endpoint | **{wrongEndpoint}** |"));
        report.AppendLine(Universe.Inv($"| Endpoint and category disagreeing | **{wrongCategory}** |"));
        report.AppendLine(Universe.Inv($"| Region on every request | `{region.Code}` |"));
        report.AppendLine(Universe.Inv($"| Subject kind on every request | `{subjectKind}` |"));
        report.AppendLine();

        report.AppendLine("The six already satisfied, in full:");
        report.AppendLine();
        report.AppendLine("| Symbol | Endpoint | Category | Fingerprint |");
        report.AppendLine("| --- | --- | --- | --- |");

        foreach (var row in suppressed.OrderBy(p => p.Symbol, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv($"| `{row.Symbol}` | `{row.Source}` | {row.Category} | `{row.Fingerprint[..16]}` |"));
        }

        report.AppendLine();

        // ---- 6. gate 12, recomputed across all three namespaces -------------------------------------------

        var twelve = await GateTwelveAsync(context);

        // ---- 5b. the authorisation, against the plan it claims to cover ----------------------------------

        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        var windowFrom = DateOnly.FromDateTime(WindowStart);
        var windowTo = DateOnly.FromDateTime(WindowEnd);

        var uncovered = new List<Planned>();

        foreach (var row in first)
        {
            if (satisfied.Contains(row.Fingerprint))
            {
                // Already in the ledger. It would never leave the process, so it must not spend a
                // dispatch nobody is going to make.
                authorization.RecordSuppressed();
                continue;
            }

            if (!authorization.TryConsume(row.Source, row.Symbol, windowFrom, windowTo).Allowed)
            {
                uncovered.Add(row);
            }
        }

        report.AppendLine("## The authorisation");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        report.AppendLine(Universe.Inv($"| Declaration | `declarations/acquisition-eodhd-sample400.json` |"));
        report.AppendLine(Universe.Inv($"| Authorisation | `{authorization.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Digest, recomputed at load | `{authorization.Digest}` |"));
        report.AppendLine(Universe.Inv($"| Evidence base | `{authorization.EvidenceBaseFingerprint}` |"));
        report.AppendLine(Universe.Inv($"| Symbols named | {authorization.Symbols.Count} |"));
        report.AppendLine(Universe.Inv($"| Dispatch ceiling | **{authorization.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Suppressed by the ledger, consuming nothing | {authorization.Suppressed} |"));
        report.AppendLine(Universe.Inv($"| Consumed by the full plan | **{authorization.Consumed}** |"));
        report.AppendLine(Universe.Inv($"| Remaining after the full plan | **{authorization.Remaining}** |"));
        report.AppendLine(Universe.Inv($"| Planned requests the authorisation does not cover | **{uncovered.Count}** |"));
        report.AppendLine();
        report.AppendLine("The whole plan fits the ceiling exactly and leaves nothing spare, which is");
        report.AppendLine("the arithmetic working rather than a coincidence: the ceiling was derived");
        report.AppendLine("from this plan. What it proves is the other direction - a 705th dispatch, a");
        report.AppendLine("symbol outside the 355, a third endpoint or a wider window is refused, and a");
        report.AppendLine("request the ledger already satisfies spends none of it.");
        report.AppendLine();

        report.AppendLine("## Gate 12 - de-duplication, all three namespaces, zero tolerance");
        report.AppendLine();
        report.AppendLine("| Namespace | Rows | Distinct identities | Excess |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| financial | {twelve.FinancialRows} | {twelve.FinancialDistinct} | {ObservationDeduplication.Excess(twelve.FinancialRows, twelve.FinancialDistinct)} |"));
        report.AppendLine(Universe.Inv($"| prices | {twelve.PriceRows} | {twelve.PriceDistinct} | {ObservationDeduplication.Excess(twelve.PriceRows, twelve.PriceDistinct)} |"));
        report.AppendLine(Universe.Inv($"| splits | {twelve.SplitRows} | {twelve.SplitDistinct} | {ObservationDeduplication.Excess(twelve.SplitRows, twelve.SplitDistinct)} |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Gate 12: **{(twelve.Passes ? "PASS" : "FAIL")}**. A re-run of the acquisition cannot add a row that differs only in retrieval time, because retrieval time is not part of the identity - which is exactly why this gate is the answer to \"would a second run duplicate anything\"."));
        report.AppendLine();

        // ---- 7. gate 2, from the sealed manifest ----------------------------------------------------------

        var universeDropouts = members.Count(m => !m.SurvivesToWindowEnd);
        var panelDropouts = ready.Count(m => !m.SurvivesToWindowEnd);

        report.AppendLine("## Gate 2 - survivorship, from the sealed cohorts");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Dropouts in the sealed universe | {universeDropouts} of {members.Count} ({AcquisitionPlanning.DropoutShare(universeDropouts, members.Count):P2}) |"));
        report.AppendLine(Universe.Inv($"| Dropouts in the acquisition set | {panelDropouts} of {ready.Count} ({AcquisitionPlanning.DropoutShare(panelDropouts, ready.Count):P2}) |"));
        report.AppendLine(Universe.Inv($"| Floor | {AcquisitionPlanning.SurvivorshipFloor:P2}, unmoved |"));
        report.AppendLine(Universe.Inv($"| Gate 2 | **{(AcquisitionPlanning.ClearsSurvivorshipFloor(panelDropouts, ready.Count) ? "PASS" : "FAIL")}** |"));
        report.AppendLine();
        report.AppendLine("Membership comes from the sealed manifest's own cohort lists, not from the");
        report.AppendLine("identity table, so the measure cannot drift with a later identity correction.");
        report.AppendLine();

        // ---- 8. gates 6 and 9 ------------------------------------------------------------------------------

        var held = await SessionsAsync(context);

        var requested = ledger
            .Where(r => r.Outcome == IngestionOutcome.Succeeded &&
                r.Request.Category == DataCategory.MarketPrices &&
                r.Request.Subject.Identifier is not null)
            .Select(r => r.Request.Subject.Identifier!)
            .ToHashSet(StringComparer.Ordinal);

        var faults = 0;
        var pending = 0;

        foreach (var member in members)
        {
            var symbol = member.Ready && member.Ticker is not null
                ? AcquisitionPlanning.SymbolFor(member.Ticker)
                : null;

            List<DateOnly> sessions = symbol is not null && held.TryGetValue(symbol, out var dates)
                ? dates
                : [];

            var verdict = CoverageEvaluation.Evaluate(
                member.Ready,
                sessions,
                member.SpanFrom,
                member.SpanTo,
                wasRequested: symbol is not null && requested.Contains(symbol));

            if (verdict.IsFault)
            {
                faults++;
            }

            if (verdict.NotYetAcquired)
            {
                pending++;
            }
        }

        var passesNow = CoverageEvaluation.Passes(faults, pending, acquisitionComplete: false);
        var passesIfComplete = CoverageEvaluation.Passes(faults, pending, acquisitionComplete: true);

        report.AppendLine("## Gates 6 and 9");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Gate 6 faults | {faults} |"));
        report.AppendLine(Universe.Inv($"| Gate 6 not-yet-acquired | {pending} |"));
        report.AppendLine(Universe.Inv($"| Gate 6, acquisition not complete | **{(passesNow ? "PASS" : "FAIL")}** |"));
        report.AppendLine(Universe.Inv($"| Gate 6, if acquisition were declared complete | **{(passesIfComplete ? "PASS" : "FAIL")}** |"));
        report.AppendLine();
        report.AppendLine("Gate 9 has two halves and only one is provable before a run. **First run:** a");
        report.AppendLine("request's fingerprint is a hash of source, category, region, subject and");
        report.AppendLine("window, and excludes the request instant and the correlation id, so the same");
        report.AppendLine("request built at any time by any caller is one identity. **Second run:** an");
        report.AppendLine("identical plan re-executed must fetch nothing and change nothing, which can");
        report.AppendLine("only be observed once a first run exists. The determinism half is proved");
        report.AppendLine("above across two builds with different clocks and correlation ids; the");
        report.AppendLine("empirical half remains the first check to run after acquisition.");
        report.AppendLine();

        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "acquisition-authorization.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- and only now, the judgements ---------------------------------------------------------------

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(executionsBefore, await context.ActionExecutions.AsNoTracking().CountAsync());
        Assert.Equal(auditsBefore, await context.AuditRecords.AsNoTracking().CountAsync());

        Assert.Equal(SealedDigest, digest);
        Assert.True(manifestBytes.Length == SealedBytes, "the sealed manifest's length changed");

        Assert.True(members.Count == 400, "the universe is not four hundred members");
        Assert.True(ready.Count == 355, Universe.Inv($"{ready.Count} ready, not 355"));
        Assert.True(excluded.Count == 45, Universe.Inv($"{excluded.Count} excluded, not 45"));

        Assert.Empty(malformedTicker);
        Assert.Empty(notAuthoritative);
        Assert.Empty(nonEquity);
        Assert.Empty(duplicateTickers);

        Assert.True(first.Count == 710, Universe.Inv($"{first.Count} planned requests, not 710"));
        Assert.True(distinct == 710, Universe.Inv($"{distinct} distinct fingerprints, not 710"));
        Assert.True(suppressed.Count == 6, Universe.Inv($"{suppressed.Count} suppressed, not 6"));
        Assert.True(net == 704, Universe.Inv($"{net} net fetches, not 704"));
        Assert.True(deterministic, "the same plan built twice produced different fingerprints");

        Assert.Equal(0, wrongWindow);
        Assert.Equal(0, wrongEndpoint);
        Assert.Equal(0, wrongCategory);

        // A failure must never be able to become an observation.
        Assert.Empty(failedWithPayload);

        Assert.Empty(uncovered);
        Assert.True(
            authorization.Consumed == 704,
            Universe.Inv($"the plan consumed {authorization.Consumed} dispatches, not 704"));
        Assert.Equal(0, authorization.Remaining);
        Assert.Equal(6, authorization.Suppressed);
        Assert.False(
            authorization.TryConsume(PriceSource, "AAPL.US", windowFrom, windowTo).Allowed,
            "a dispatch was permitted past the ceiling and outside the authorised set");

        Assert.True(twelve.Passes, "gate 12 does not pass across all three namespaces");
        Assert.True(
            AcquisitionPlanning.ClearsSurvivorshipFloor(panelDropouts, ready.Count),
            "gate 2 no longer clears its floor");

        // Gate 6 passes only because nothing has been asked for, and would not if it had been.
        Assert.True(passesNow, "gate 6 does not pass before acquisition");
        Assert.False(passesIfComplete, "gate 6 would pass with members still pending, which is the bug");
        Assert.Equal(0, faults);
        Assert.True(pending > 0, "nothing is pending, so the safeguard is not being exercised");
    }

    // ---- building ------------------------------------------------------------------------------------

    private static List<Planned> Build(
        List<Member> ready,
        string subjectKind,
        Region region,
        DateTime requestedAt,
        string correlationPrefix)
    {
        var planned = new List<Planned>(ready.Count * 2);

        foreach (var member in ready)
        {
            var symbol = AcquisitionPlanning.SymbolFor(member.Ticker!);

            foreach (var (source, category) in new[]
            {
                (PriceSource, DataCategory.MarketPrices),
                (ActionsSource, DataCategory.CorporateActions),
            })
            {
                var window = DateRange.Create(WindowStart, WindowEnd);

                var request = IngestionRequest.Create(
                    SourceId.Create(source),
                    category,
                    region,
                    IngestionSubject.Create(subjectKind, symbol),
                    CorrelationId.Create(Universe.Inv($"{correlationPrefix}-{member.Ticker}-{category}")),
                    requestedAt,
                    window);

                planned.Add(new Planned(
                    symbol,
                    source,
                    category.ToString(),
                    Universe.Inv($"{window.StartUtc:yyyy-MM-dd}..{window.EndUtc:yyyy-MM-dd}"),
                    request.Fingerprint()));
            }
        }

        return planned;
    }

    private static bool WellFormedTicker(string ticker) =>
        ticker.Length is > 0 and <= 5 && ticker.All(char.IsAsciiLetterUpper);

    // ---- reading -------------------------------------------------------------------------------------

    private static async Task<List<Member>> MembersAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        var windowFrom = DateOnly.FromDateTime(WindowStart);
        var windowTo = DateOnly.FromDateTime(WindowEnd);

        var finalCut = manifest.RootElement
            .GetProperty("CohortCutDates")
            .EnumerateArray()
            .Select(c => c.GetString() ?? string.Empty)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Last();

        var cohorts = manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => m.GetProperty("Cohorts").EnumerateArray()
                    .Select(c => c.GetString() ?? string.Empty)
                    .OrderBy(c => c, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m =>
            {
                var cik = m.GetProperty("Cik").GetString() ?? string.Empty;
                List<string> list = cohorts.TryGetValue(cik, out var c) ? c : [];

                var survives = list.Count > 0 &&
                    string.Equals(list[^1], finalCut, StringComparison.Ordinal);

                var (spanFrom, spanTo) = MembershipSpan.Derive(
                    [.. list.Select(d => DateOnly.Parse(d, CultureInfo.InvariantCulture))],
                    DateOnly.Parse(finalCut, CultureInfo.InvariantCulture),
                    windowFrom,
                    windowTo);

                return new Member(
                    cik,
                    m.GetProperty("Name").GetString() ?? "(unnamed)",
                    m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True,
                    m.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null,
                    m.GetProperty("Status").GetString() ?? "unknown",
                    m.TryGetProperty("SecurityKind", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString()
                        : null,
                    survives,
                    spanFrom,
                    spanTo);
            })
            .ToList();
    }

    private static async Task<Dictionary<string, List<DateOnly>>> SessionsAsync(AppDbContext context)
    {
        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .Select(o => new { o.Subject.Identifier, o.Provenance.AsOfUtc })
            .ToListAsync();

        return rows
            .Where(r => r.Identifier is not null)
            .GroupBy(r => r.Identifier!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => DateOnly.FromDateTime(r.AsOfUtc)).Distinct().ToList(),
                StringComparer.Ordinal);
    }

    private static async Task<Twelve> GateTwelveAsync(AppDbContext context)
    {
        var financial = await Namespace(
            context.Observations.AsNoTracking()
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%")));

        var prices = await Namespace(
            context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute));

        var splits = await Namespace(
            context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute));

        return new Twelve(
            financial.Rows, financial.Distinct,
            prices.Rows, prices.Distinct,
            splits.Rows, splits.Distinct);
    }

    private static async Task<(int Rows, int Distinct)> Namespace(IQueryable<AI.Investment.Domain.Observations.Observation> query)
    {
        var rows = await query
            .Select(o => new
            {
                o.Subject.Kind,
                o.Subject.Identifier,
                o.Attribute,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
                o.Value.Canonical,
            })
            .ToListAsync();

        var keys = rows
            .Select(r => ObservationDeduplication.Key(
                r.Kind, r.Identifier, r.Attribute, r.AsOfUtc, r.PublishedAtUtc, r.Canonical))
            .Distinct(StringComparer.Ordinal)
            .Count();

        return (rows.Count, keys);
    }

    private sealed record Member(
        string Cik,
        string Name,
        bool Ready,
        string? Ticker,
        string Status,
        string? SecurityKind,
        bool SurvivesToWindowEnd,
        DateOnly SpanFrom,
        DateOnly SpanTo);

    private sealed record Planned(
        string Symbol,
        string Source,
        string Category,
        string Window,
        string Fingerprint);

    private sealed record Twelve(
        int FinancialRows,
        int FinancialDistinct,
        int PriceRows,
        int PriceDistinct,
        int SplitRows,
        int SplitDistinct)
    {
        public bool Passes =>
            ObservationDeduplication.Passes(FinancialRows, FinancialDistinct) &&
            ObservationDeduplication.Passes(PriceRows, PriceDistinct) &&
            ObservationDeduplication.Passes(SplitRows, SplitDistinct);
    }
}
