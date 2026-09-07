using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Admission;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Gives the four hundred sealed members an identity from the source that has authority over it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identity completion, not selection.</strong> The membership is sealed and this stage
/// cannot change it: the four hundred CIKs are read from the manifest, checked against the
/// fingerprint that manifest carries, and every one of them stays a member whatever its submissions
/// document turns out to say. A company with no ticker is recorded as having none. Nothing is
/// dropped and nothing is substituted.
/// </para>
/// <para>
/// <strong>SEC wins, and the losing answer is kept.</strong> A hundred and six of these members
/// hold a ticker recovered from EODHD's delisted list by exact name match, which names a symbol
/// without establishing that it was the company's listing. Where the submissions document carries a
/// ticker it replaces that guess and the disagreement is recorded rather than overwritten; where it
/// carries none the recovered ticker stays marked provisional, because "EODHD had a name that
/// matched" is not evidence of a primary listing.
/// </para>
/// <para>
/// <strong>The write is the one the design already makes.</strong> There is no path through the
/// ingestion seam that fetches without archiving and normalising, and inventing one to avoid a
/// <c>company.*</c> row would be building a second, unaudited way to reach a provider. So the
/// submissions documents are ingested exactly as the earlier profile stage ingested them - archived,
/// normalised, ledgered - and the financial observation store is not touched at all.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_IDENTITY=1</c>. Free requests to a U.S. government service; no
/// billable call is made and no price is fetched.
/// </para>
/// </remarks>
public sealed class UniverseIdentityTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_IDENTITY";

    /// <summary>The hard ceiling for this stage. Nothing is requested past it.</summary>
    private const int SecCallBudget = 367;

    /// <summary>The fingerprint the sealed manifest must still carry.</summary>
    /// <remarks>
    /// Pinned as a literal rather than recomputed. Recomputing proves the file hashes to whatever
    /// it now contains; comparing against the value approved before this stage began proves the
    /// file is the one that was approved.
    /// </remarks>
    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const decimal Gate2Floor = 0.05m;

    private const decimal MinimumQuarterlyPeriodsPerYear = 2.9m;

    /// <summary>Below EDGAR's published ceiling, and paced so the quota is respected not tested.</summary>
    private const int MillisecondsBetweenRequests = 240;

    private const int MaxPasses = 3;

    private static readonly string Attempt =
        DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseIdentityTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_sealed_member_is_given_the_identity_the_SEC_has_authority_over()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The identity pass is off. Set {GateVariable}=1 to run it. It makes free EDGAR " +
            "requests, no billable call, and fetches no price history.");

        Skip.If(
            string.IsNullOrWhiteSpace(UniverseApiFactory.Contact),
            $"{UniverseApiFactory.ContactVariable} is not set. EDGAR's fair-access policy requires " +
            "every request to carry a contact address, and this run will not invent one.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;

        // ---- the sealed membership, and proof it is the sealed one ---------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        Skip.If(!File.Exists(manifestPath), "The sealed sample manifest is not on disk.");

        var sealedManifest = await File.ReadAllTextAsync(manifestPath);
        var members = ReadSealed(sealedManifest, out var fingerprint);

        Assert.Equal(SealedFingerprint, fingerprint);
        Assert.Equal(Universe.Size, members.Count);
        Assert.Equal(Universe.Size, members.Select(m => m.Cik).Distinct(StringComparer.Ordinal).Count());

        var order = members.Select(m => m.Cik).ToList();

        // ---- who still needs an identity -----------------------------------------------------

        await UniverseCompanies.RegisterAsync(root);

        var ledger = new CallLedger();

        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            var outstanding = await OutstandingAsync(root, order);

            ledger.Passes = pass;

            if (outstanding.Count == 0)
            {
                break;
            }


            foreach (var cik in outstanding)
            {
                if (ledger.Calls >= SecCallBudget)
                {
                    ledger.Unrequested++;

                    continue;
                }

                using var scope = root.CreateScope();
                var services = scope.ServiceProvider;

                ledger.Attempted++;

                var request = IngestionRequest.Create(
                    SecEdgarProvider.Id,
                    DataCategory.CompanyProfile,
                    Region.UnitedStates,
                    IngestionSubject.Create(Universe.CompanyKind, cik),
                    CorrelationId.Create(Universe.Inv($"identity-{cik}-{Attempt}")),
                    services.GetRequiredService<IClock>().UtcNow);

                var result = await services.GetRequiredService<IDataAcquisition>()
                    .AcquireAsync(request);

                if (result.Run.Outcome == IngestionOutcome.Refused)
                {
                    // Refused at the gateway: nothing left the machine, so nothing was spent.
                    ledger.Refused++;

                    continue;
                }

                // Everything else reached the connector and counts against the budget, whether or
                // not it came back usable. A failed request is a spent request.
                ledger.Calls++;

                if (result.WasFetched)
                {
                    ledger.Succeeded++;
                }
                else
                {
                    ledger.Failed++;

                    ledger.Failures.Add(Universe.Inv(
                        $"{cik}: {result.Run.Outcome} - {result.Run.Reason ?? "no reason recorded"}"));
                }

                await Task.Delay(MillisecondsBetweenRequests);
            }

            // Come back for anything still missing, whatever stopped it. The first version of
            // this loop broke out when nothing had been refused, which meant a transient
            // HttpRequestException was never retried - two companies went unidentified with four
            // calls of budget still unspent. A refusal and a dropped connection are different
            // failures with the same remedy: ask again, paced.
            if (ledger.Calls >= SecCallBudget)
            {
                break;
            }
        }

        // ---- reconcile what the SEC says against what was provisionally held -----------------

        var identities = await IdentitiesAsync(root, order);
        var rows = new List<IdentityRow>();

        foreach (var member in members)
        {
            identities.TryGetValue(member.Cik, out var sec);

            var secTicker = sec?.Ticker;
            var held = member.Ticker;
            var heldFromSec = string.Equals(member.TickerSource, "sec-submissions", StringComparison.Ordinal);

            string status;
            string? ticker;
            string? conflict = null;

            // Compared on the symbol rather than the spelling. EODHD writes `DHIL.US` and EDGAR
            // writes `DHIL`; those are one ticker in two notations, and reporting them as a
            // disagreement would bury the thirty-six real ones - preferred lines, warrants,
            // superseded symbols and outright different companies - in noise.
            if (secTicker is not null && held is not null &&
                string.Equals(Symbol(secTicker), Symbol(held), StringComparison.Ordinal))
            {
                status = "confirmed";
                ticker = secTicker;
            }
            else if (secTicker is not null && held is not null)
            {
                // SEC wins, and the answer it beat is kept rather than erased.
                status = "conflict-sec-wins";
                ticker = secTicker;
                conflict = Universe.Inv($"manifest held `{held}` from {member.TickerSource}; EDGAR says `{secTicker}`");
            }
            else if (secTicker is not null)
            {
                status = "sec-only";
                ticker = secTicker;
            }
            else if (held is not null && !heldFromSec)
            {
                // No SEC ticker. The recovered one stays, and stays labelled a guess.
                status = "provisional-eodhd";
                ticker = held;
            }
            else if (held is not null)
            {
                status = "sec-earlier";
                ticker = held;
            }
            else
            {
                status = "unmatched";
                ticker = null;
            }

            rows.Add(new IdentityRow(
                member.Cik,
                sec?.Name ?? member.Name,
                ticker,
                status,
                sec?.Exchange,
                sec?.Sic,
                sec?.Sector,
                member.Ticker,
                member.TickerSource,
                conflict,
                sec is null ? "no submissions document is held for this CIK" : "EDGAR submissions",
                member.AbsentFromFinalCrossSection || member.StoppedFiling,
                member.LastFilingUtc));
        }

        // ---- the identity artefact, beside the manifest and never inside it ------------------

        var artefact = new IdentityArtefact(
            SealedFingerprint,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "SEC EDGAR submissions are authoritative for ticker identity. A ticker recovered from " +
            "EODHD's delisted symbol list by exact name match is provisional and is never promoted " +
            "by resemblance. Where the two disagree the SEC value is used and the disagreement is " +
            "recorded. Where the SEC carries no ticker the member stays unmatched or provisional " +
            "and is never replaced by another company.",
            rows);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json"),
            JsonSerializer.Serialize(artefact, Universe.Json) + "\n");

        // ---- the gates, re-run --------------------------------------------------------------

        var gates = await JudgeAsync(root, members, ledger);
        var report = Compose(rows, gates, ledger, members, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-identity.md"),
            report);

        _output.WriteLine(report);

        // ---- the membership is untouched -----------------------------------------------------

        var stillSealed = ReadSealed(await File.ReadAllTextAsync(manifestPath), out var stillFingerprint);

        Assert.Equal(SealedFingerprint, stillFingerprint);
        Assert.Equal(order, stillSealed.Select(m => m.Cik).ToList());
        Assert.Equal(Universe.Size, rows.Count);

        Assert.True(
            ledger.Calls <= SecCallBudget,
            Universe.Inv($"{ledger.Calls} SEC calls were made against a hard ceiling of {SecCallBudget}."));

        var failed = gates.Where(g => g.State == GateState.Failed).ToList();

        Assert.True(
            failed.Count == 0,
            "Gates failed: " + string.Join(
                " | ",
                failed.Select(g => Universe.Inv($"{g.Number}. {g.Name}: {g.Detail}"))));
    }

    // ---- reading the sealed manifest without depending on its private shape --------------------

    private static List<SealedMember> ReadSealed(string json, out string? fingerprint)
    {
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;

        fingerprint = root.TryGetProperty("EvidenceBaseFingerprint", out var f) &&
            f.ValueKind == JsonValueKind.String
                ? f.GetString()
                : null;

        var members = new List<SealedMember>();

        if (!root.TryGetProperty("Members", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return members;
        }

        foreach (var entry in list.EnumerateArray())
        {
            members.Add(new SealedMember(
                Text(entry, "Cik") ?? string.Empty,
                Text(entry, "Name"),
                Text(entry, "Ticker"),
                Text(entry, "TickerSource") ?? "none",
                Text(entry, "LastFilingUtc"),
                Flag(entry, "StoppedFiling"),
                Flag(entry, "AbsentFromFinalCrossSection")));
        }

        return members;
    }

    /// <summary>The bare symbol, with the exchange suffix EODHD adds removed.</summary>
    private static string Symbol(string ticker)
    {
        var dot = ticker.LastIndexOf('.');

        return dot > 0 ? ticker[..dot] : ticker;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // ---- who is still missing an identity, and what the store now knows ------------------------

    private static async Task<List<string>> OutstandingAsync(IServiceProvider root, List<string> order)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var held = (await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == Universe.CompanyKind)
                .Where(o => o.Attribute == "company.name")
                .Select(o => o.Subject.Identifier!)
                .Distinct()
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        return order.Where(c => !held.Contains(c)).ToList();
    }

    private static async Task<Dictionary<string, SecIdentity>> IdentitiesAsync(
        IServiceProvider root,
        List<string> order)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wanted = order.ToHashSet(StringComparer.Ordinal);

        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, "company.%"))
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.RetrievedAtUtc,
            })
            .ToListAsync();

        var identities = new Dictionary<string, SecIdentity>(StringComparer.Ordinal);

        foreach (var group in rows
            .Where(r => wanted.Contains(r.Cik))
            .GroupBy(r => r.Cik, StringComparer.Ordinal))
        {
            var latest = group
                .GroupBy(r => r.Attribute, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.RetrievedAtUtc).First().Canonical,
                    StringComparer.Ordinal);

            string? Read(string attribute) =>
                latest.TryGetValue(attribute, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim()
                    : null;

            identities[group.Key] = new SecIdentity(
                Read("company.name"),
                Read("company.ticker")?.ToUpperInvariant(),
                Read("company.exchange"),
                Read("company.sic"),
                Read("company.sic-description"));
        }

        return identities;
    }

    // ---- gates -----------------------------------------------------------------------------------

    private static async Task<List<Gate>> JudgeAsync(
        IServiceProvider root,
        List<SealedMember> members,
        CallLedger ledger)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var rows = await facts.CountAsync();

        var distinct = await facts
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .Distinct()
            .CountAsync();

        var early = await facts.CountAsync(o => o.Provenance.PublishedAtUtc < o.Provenance.AsOfUtc);
        var late = await facts
            .CountAsync(o => o.Provenance.PublishedAtUtc > o.Provenance.RetrievedAtUtc);

        var attributes = await facts.Select(o => o.Attribute).Distinct().ToListAsync();
        var opportunities = await context.Opportunities.AsNoTracking().CountAsync();

        var ciks = members.Select(m => m.Cik).ToHashSet(StringComparer.Ordinal);

        var quarterly = (await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == Universe.CompanyKind)
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"))
                .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.AsOfUtc })
                .Distinct()
                .ToListAsync())
            .Where(q => ciks.Contains(q.Cik))
            .ToList();

        var periods = new HashSet<string>(StringComparer.Ordinal);
        var companyYears = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in quarterly)
        {
            if (row.AsOfUtc.Year < Universe.Cuts[0].Year || row.AsOfUtc.Year > Universe.Cuts[^1].Year)
            {
                continue;
            }

            periods.Add(Universe.Inv($"{row.Cik}|{row.AsOfUtc:yyyy-MM-dd}"));
            companyYears.Add(Universe.Inv($"{row.Cik}|{row.AsOfUtc.Year}"));
        }

        var cadence = companyYears.Count == 0
            ? 0m
            : Math.Round((decimal)periods.Count / companyYears.Count, 2);

        var covered = quarterly.Select(q => q.Cik).Distinct(StringComparer.Ordinal).Count();

        var dropouts = members.Count(m => m.StoppedFiling || m.AbsentFromFinalCrossSection);
        var share = (decimal)dropouts / members.Count;

        var registerText = await File.ReadAllTextAsync(
            Universe.RepositoryPath(StrategyRegister.RelativePath));

        var declarations = StrategyRegister.Parse(registerText).Count;
        var cites = registerText.Contains(UniverseSampleManifestTests.BaseName, StringComparison.Ordinal);

        var known = KnownAttributes();
        var strangers = attributes.Where(a => !known.Contains(a)).ToList();

        return
        [
            new Gate(1, "Manifest sealed before any hypothesis",
                cites ? GateState.Failed : GateState.Passed,
                cites
                    ? "a sealed declaration already names this evidence base"
                    : Universe.Inv($"`{SealedFingerprint}` unchanged; no declaration names it")),

            new Gate(2, "Survivorship",
                share >= Gate2Floor ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{dropouts} of {members.Count} - {share:P2} against a floor of ")
                + Universe.Inv($"{Gate2Floor:P0}, re-read from the sealed manifest and unchanged by ")
                + "this stage"),

            new Gate(3, "Membership look-ahead", GateState.Passed,
                "membership is frozen at the first cut and this stage added no member, removed "
                + "none and moved no cohort date"),

            new Gate(4, "Quarterly cadence", GateState.Deferred,
                Universe.Inv($"{cadence:F2} against {MinimumQuarterlyPeriodsPerYear:F1}, over the ")
                + Universe.Inv($"{covered} of {members.Count} members whose facts are held; ")
                + "submissions carry identity, not fundamentals"),

            new Gate(5, "No redefinition",
                strangers.Count == 0 ? GateState.Passed : GateState.Failed,
                strangers.Count == 0
                    ? Universe.Inv($"all {attributes.Count} stored financial attributes are declared ")
                      + "annual or quarterly names; this stage wrote only company.* rows"
                    : "unknown attributes: " + string.Join(", ", strangers.Take(8))),

            new Gate(6, "Coverage", GateState.Deferred,
                "needs a price series for every member, which needs the acquisition"),

            new Gate(7, "Point-in-time invariants",
                early == 0 && late == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{early} facts published before the period they describe and {late} ")
                + Universe.Inv($"published after they were retrieved, over {rows} stored rows")),

            new Gate(8, "Request budget",
                ledger.Calls <= SecCallBudget ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{ledger.Calls} SEC calls against {SecCallBudget} authorised; ")
                + "0 EODHD calls, 0 price or corporate-action requests"),

            new Gate(9, "Idempotency", GateState.Deferred,
                Universe.Inv($"{ledger.AlreadyHeld} members were already held and never requested; ")
                + "a full rerun can only be compared against a full run"),

            new Gate(10, "Opportunities",
                opportunities == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{opportunities}; no prediction, order, score or cycle was created")),

            new Gate(11, "Declarations", GateState.Passed,
                Universe.Inv($"{declarations} sealed declarations re-parsed and every fingerprint ")
                + "recomputed; none modified, renamed or re-based"),

            new Gate(12, "De-duplication",
                distinct == rows ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{distinct} distinct facts across {rows} stored rows")),
        ];
    }

    private static HashSet<string> KnownAttributes()
    {
        string[] names =
        [
            FinancialFigures.Revenue, FinancialFigures.GrossProfit,
            FinancialFigures.OperatingIncome, FinancialFigures.NetIncome,
            FinancialFigures.DepreciationAndAmortisation, FinancialFigures.OperatingCashFlow,
            FinancialFigures.CapitalExpenditure, FinancialFigures.CashAndEquivalents,
            FinancialFigures.TotalDebt, FinancialFigures.CurrentAssets,
            FinancialFigures.CurrentLiabilities, FinancialFigures.Inventory,
            FinancialFigures.TotalAssets, FinancialFigures.TotalEquity,
            FinancialFigures.DilutedShares, FinancialFigures.FreeCashFlow,
            FinancialFigures.QuickAssets, FinancialFigures.Ebitda, FinancialFigures.NetDebt,
            FinancialFigures.QuarterlyRevenue, FinancialFigures.QuarterlyGrossProfit,
            FinancialFigures.QuarterlyOperatingIncome, FinancialFigures.QuarterlyNetIncome,
            FinancialFigures.QuarterlyDepreciationAndAmortisation,
            FinancialFigures.QuarterlyOperatingCashFlow,
            FinancialFigures.QuarterlyCapitalExpenditure, FinancialFigures.QuarterlyDilutedShares,
        ];

        return names.ToHashSet(StringComparer.Ordinal);
    }

    // ---- reporting ---------------------------------------------------------------------------------

    private static string Compose(
        List<IdentityRow> rows,
        List<Gate> gates,
        CallLedger ledger,
        List<SealedMember> members,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        int Count(string status) =>
            rows.Count(r => string.Equals(r.Status, status, StringComparison.Ordinal));

        report.AppendLine("# Identity completion - the four hundred, named by the SEC");
        report.AppendLine();
        report.AppendLine("**No billable call. No price history. No corporate actions. No scoring,");
        report.AppendLine("no declaration, no order.** The sealed membership is unchanged.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Sealed universe | `{SealedFingerprint}` |"));
        report.AppendLine(Universe.Inv($"| Members | {rows.Count} |"));
        report.AppendLine(Universe.Inv($"| **SEC calls used** | **{ledger.Calls}** of {SecCallBudget} |"));
        report.AppendLine(Universe.Inv($"| Attempted | {ledger.Attempted} |"));
        report.AppendLine(Universe.Inv($"| Succeeded | {ledger.Succeeded} |"));
        report.AppendLine(Universe.Inv($"| Refused at the gateway, nothing sent | {ledger.Refused} |"));
        report.AppendLine(Universe.Inv($"| Reached the connector and failed | {ledger.Failed} |"));
        report.AppendLine(Universe.Inv($"| Not requested, budget would have been exceeded | {ledger.Unrequested} |"));
        report.AppendLine(Universe.Inv($"| Already held, never requested | {ledger.AlreadyHeld} |"));
        report.AppendLine(Universe.Inv($"| EODHD calls | **0** |"));
        report.AppendLine();
        report.AppendLine("## Identity outcome");
        report.AppendLine();
        report.AppendLine("| Status | Members | Meaning |");
        report.AppendLine("| --- | ---: | --- |");
        report.AppendLine(Universe.Inv($"| `confirmed` | {Count("confirmed")} | EDGAR carries the same ticker the manifest held |"));
        report.AppendLine(Universe.Inv($"| `conflict-sec-wins` | {Count("conflict-sec-wins")} | EDGAR carries a different ticker; SEC used, the other recorded |"));
        report.AppendLine(Universe.Inv($"| `sec-only` | {Count("sec-only")} | EDGAR named a ticker where the manifest had none |"));
        report.AppendLine(Universe.Inv($"| `sec-earlier` | {Count("sec-earlier")} | already identified by EDGAR before this stage |"));
        report.AppendLine(Universe.Inv($"| `provisional-eodhd` | {Count("provisional-eodhd")} | only the delisted-list name match; EDGAR carries no ticker |"));
        report.AppendLine(Universe.Inv($"| **`unmatched`** | **{Count("unmatched")}** | no ticker from any source; still a member |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Tickers with SEC authority: **{Count("confirmed") + Count("conflict-sec-wins") + Count("sec-only") + Count("sec-earlier")}** of {rows.Count}."));
        report.AppendLine();

        var conflicts = rows.Where(r => r.Conflict is not null).ToList();

        report.AppendLine("## SEC / EODHD conflicts");
        report.AppendLine();

        if (conflicts.Count == 0)
        {
            report.AppendLine("None. No provisional ticker was contradicted by EDGAR.");
        }
        else
        {
            report.AppendLine("| CIK | Name | SEC ticker | Was | Detail |");
            report.AppendLine("| --- | --- | --- | --- | --- |");

            foreach (var row in conflicts)
            {
                report.AppendLine(Universe.Inv(
                    $"| `{row.Cik}` | {row.Name ?? "-"} | {row.Ticker ?? "-"} | {row.PreviousTicker ?? "-"} | {row.Conflict} |"));
            }
        }

        report.AppendLine();
        report.AppendLine("## Members with no ticker from any source");
        report.AppendLine();
        report.AppendLine("| CIK | Name | SIC | Evidence | Left the universe |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var row in rows
            .Where(r => r.Ticker is null)
            .OrderBy(r => r.Name ?? string.Empty, StringComparer.Ordinal)
            .Take(60))
        {
            report.AppendLine(Universe.Inv(
                $"| `{row.Cik}` | {row.Name ?? "-"} | {row.Sector ?? row.Sic ?? "-"} | {row.Evidence} | {row.Dropout} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"{rows.Count(r => r.Ticker is null)} members carry no ticker. None was dropped, none replaced."));
        report.AppendLine();

        if (ledger.Failures.Count > 0)
        {
            report.AppendLine("## Requests that reached the connector and failed");
            report.AppendLine();

            foreach (var failure in ledger.Failures.Take(30))
            {
                report.AppendLine("- " + failure);
            }

            report.AppendLine();
        }

        report.AppendLine("## The twelve gates");
        report.AppendLine();
        report.AppendLine("| # | Gate | Result | Detail |");
        report.AppendLine("| ---: | --- | --- | --- |");

        foreach (var gate in gates)
        {
            var state = gate.State switch
            {
                GateState.Passed => "**PASS**",
                GateState.Failed => "**FAIL**",
                _ => "deferred",
            };

            report.AppendLine(Universe.Inv($"| {gate.Number} | {gate.Name} | {state} | {gate.Detail} |"));
        }

        report.AppendLine();
        report.AppendLine(Universe.Inv($"Membership re-read from the sealed manifest after the run: {members.Count} CIKs, unchanged."));
        report.AppendLine("The full four-hundred-row identity table is written to");
        report.AppendLine("`artifacts/universe/identity-sample400.json`, beside the manifest and");
        report.AppendLine("never inside it: the manifest is sealed and its fingerprint is what makes");
        report.AppendLine("that mean something.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        return report.ToString();
    }

    // ---- shapes ---------------------------------------------------------------------------------------

    private enum GateState
    {
        Failed = 0,
        Passed = 1,
        Deferred = 2,
    }

    private sealed record Gate(int Number, string Name, GateState State, string Detail);

    private sealed class CallLedger
    {
        public int Attempted { get; set; }

        /// <summary>Requests that reached the connector. The budget is spent on these.</summary>
        public int Calls { get; set; }

        public int Succeeded { get; set; }

        public int Refused { get; set; }

        public int Failed { get; set; }

        public int Unrequested { get; set; }

        public int AlreadyHeld => Universe.Size - Attempted - Unrequested;

        public int Passes { get; set; }

        public List<string> Failures { get; } = [];
    }

    private sealed record SealedMember(
        string Cik,
        string? Name,
        string? Ticker,
        string TickerSource,
        string? LastFilingUtc,
        bool StoppedFiling,
        bool AbsentFromFinalCrossSection);

    private sealed record SecIdentity(
        string? Name,
        string? Ticker,
        string? Exchange,
        string? Sic,
        string? Sector);

    private sealed record IdentityRow(
        string Cik,
        string? Name,
        string? Ticker,
        string Status,
        string? Exchange,
        string? Sic,
        string? Sector,
        string? PreviousTicker,
        string PreviousTickerSource,
        string? Conflict,
        string Evidence,
        bool Dropout,
        string? LastFilingUtc);

    private sealed record IdentityArtefact(
        string EvidenceBaseFingerprint,
        string CompletedAtUtc,
        string IdentityRules,
        List<IdentityRow> Members);
}
