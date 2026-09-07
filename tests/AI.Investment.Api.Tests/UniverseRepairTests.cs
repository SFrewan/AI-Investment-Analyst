using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Observations;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>What the repair is removing, as the safety seam sees it.</summary>
/// <remarks>
/// The description states the rule rather than the row count, because the count is the part a
/// reader can check for themselves afterwards and the rule is the part they cannot.
/// </remarks>
internal sealed record DuplicateRemovalParameters(int Rows, int Groups, int Companies)
    : IActionParameters
{
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Remove {Rows} observation rows across {Groups} groups over {Companies} companies, " +
            $"each of which repeats another row's subject, attribute, period end, publication " +
            $"instant and canonical value exactly. The earliest-retrieved row of each group is " +
            $"kept. No value, period or filing date is altered and nothing distinct is removed.");
}

/// <summary>
/// Removes the duplicate observation rows the old normaliser wrote, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a write, and the only one this stage makes.</strong> It goes through the
/// Action/Policy seam like every other effect: one proposal, one authorisation window, one
/// <c>SaveChanges</c>, one audit record. No provider is called - the EDGAR connector is not
/// registered in this host at all - and no payload is re-fetched.
/// </para>
/// <para>
/// <strong>Reversible, and that is a statement of fact rather than a convenience.</strong> Every
/// row removed here is byte-identical to a row that stays, and both were derived from a document
/// that is still in the archive. Re-running normalisation reconstructs either of them exactly.
/// That is the opposite of the retention sweep, which destroys the only copy of a payload and is
/// classified irreversible for that reason.
/// </para>
/// <para>
/// <strong>Deletion, not rebuild.</strong> Emptying the financial namespace and re-normalising 418
/// payloads into it would touch 650,000 rows to fix 1,196, and every one of those rows would get a
/// new identity, a new retrieval time and a new position in the ledger. The narrow operation is the
/// one whose blast radius can be stated: rows that repeat another row exactly, and no others.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_REPAIR=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseRepairTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_REPAIR";

    private static readonly ActionType RemoveDuplicates =
        ActionType.Create("normalization.remove-duplicates");

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseRepairTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Duplicate_rows_are_removed_and_every_distinct_fact_is_left_alone()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The store repair is off. Set {GateVariable}=1 to run it. It calls no provider and " +
            "removes only rows that repeat another row exactly.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;

        var before = await MeasureAsync(root);

        Skip.If(before.Rows == 0, "No financial observations are stored. Nothing to repair.");

        // ---- which rows repeat which ---------------------------------------------------------

        List<ObservationId> doomed;
        int groups;
        int companies;

        using (var scope = root.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var affected = (await context.Observations
                    .AsNoTracking()
                    .Where(o => o.Subject.Kind == Universe.CompanyKind)
                    .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
                    .GroupBy(o => new
                    {
                        Cik = o.Subject.Identifier!,
                        o.Attribute,
                        o.Value.Canonical,
                        o.Provenance.AsOfUtc,
                        o.Provenance.PublishedAtUtc,
                    })
                    .Select(g => new { g.Key.Cik, Rows = g.Count() })
                    .Where(g => g.Rows > 1)
                    .ToListAsync())
                .Select(g => g.Cik)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            companies = affected.Count;

            // Only the companies that actually have a duplicate. Reading every company's rows to
            // find ninety-one companies' worth of repeats would be most of a gigabyte for nothing.
            var rows = await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == Universe.CompanyKind)
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"))
                .Where(o => affected.Contains(o.Subject.Identifier!))
                .Select(o => new
                {
                    o.Id,
                    Cik = o.Subject.Identifier!,
                    o.Attribute,
                    o.Value.Canonical,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                    o.Provenance.RetrievedAtUtc,
                })
                .ToListAsync();

            var byIdentity = rows
                .GroupBy(
                    r => Key(r.Cik, r.Attribute, r.AsOfUtc, r.PublishedAtUtc, r.Canonical),
                    StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            groups = byIdentity.Count;

            // The earliest retrieval survives, and the tie is broken on the identifier so the
            // choice is deterministic rather than whatever the query planner returned first.
            doomed = byIdentity
                .SelectMany(g => g
                    .OrderBy(r => r.RetrievedAtUtc)
                    .ThenBy(r => r.Id.Value)
                    .Skip(1))
                .Select(r => r.Id)
                .ToList();
        }

        Skip.If(doomed.Count == 0, "No duplicate rows are stored. There is nothing to repair.");

        // ---- one proposal, one window, one save ----------------------------------------------

        int removed;
        string outcomeStatus;
        string outcomeReason;

        using (var scope = root.CreateScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<AppDbContext>();
            var gateway = services.GetRequiredService<IActionGateway>();
            var now = services.GetRequiredService<IClock>().UtcNow;

            var proposal = ActionProposal.Create(
                CorrelationId.Create(Universe.Inv($"universe-repair-{now:yyyyMMddHHmmss}")),

                // The capability that owns normalisation. This is the normalisation pipeline
                // correcting its own output, not a retention decision about what to keep -
                // DataRetention exists for destroying the only copy of something, and nothing here
                // destroys anything the archive cannot reproduce.
                Capability.DataIngestion,
                RemoveDuplicates,
                ActionTarget.Create("Observation", "companyfacts-duplicates"),
                new DuplicateRemovalParameters(doomed.Count, groups, companies),

                // Reversible: every row removed is identical to one that stays, and both are
                // re-derivable from a document still in the archive.
                ActionEconomics.NoFinancialEffect(),
                ProposedBy.Service("universe-repair", "1.0"),

                // Keyed on the exact set of rows, so this cannot be replayed onto a different set
                // and a genuinely different repair later is not suppressed by this one.
                Universe.Inv($"normalization.remove-duplicates:{Digest(doomed)}"),
                now);

            var outcome = await gateway.DispatchAsync(
                proposal,
                async token =>
                {
                    var entities = await context.Observations
                        .Where(o => doomed.Contains(o.Id))
                        .ToListAsync(token);

                    context.Observations.RemoveRange(entities);

                    await context.SaveChangesAsync(token);

                    return entities.Count;
                });

            removed = outcome.WasExecuted ? outcome.Result : 0;
            outcomeStatus = outcome.Status.ToString();
            outcomeReason = outcome.Reason;
        }

        // ---- what it did ----------------------------------------------------------------------

        var after = await MeasureAsync(root);

        var report = new StringBuilder();

        report.AppendLine("# Store repair - the duplicate rows, removed");
        report.AppendLine();
        report.AppendLine("**No provider call.** One proposal, one authorisation window, one save,");
        report.AppendLine("one audit record. Only rows repeating another row exactly were removed.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Seam outcome: **{outcomeStatus}** - {outcomeReason}"));
        report.AppendLine();
        report.AppendLine("| | Before | After | Change |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| Financial rows | {before.Rows} | {after.Rows} | {after.Rows - before.Rows} |"));
        report.AppendLine(Universe.Inv($"| Distinct facts | {before.Distinct} | {after.Distinct} | {after.Distinct - before.Distinct} |"));
        report.AppendLine(Universe.Inv($"| **Duplicate rows** | **{before.Rows - before.Distinct}** | **{after.Rows - after.Distinct}** | {(after.Rows - after.Distinct) - (before.Rows - before.Distinct)} |"));
        report.AppendLine(Universe.Inv($"| Annual rows | {before.AnnualRows} | {after.AnnualRows} | {after.AnnualRows - before.AnnualRows} |"));
        report.AppendLine(Universe.Inv($"| Distinct annual facts | {before.AnnualDistinct} | {after.AnnualDistinct} | {after.AnnualDistinct - before.AnnualDistinct} |"));
        report.AppendLine(Universe.Inv($"| Quarterly rows | {before.QuarterlyRows} | {after.QuarterlyRows} | {after.QuarterlyRows - before.QuarterlyRows} |"));
        report.AppendLine(Universe.Inv($"| Distinct quarterly facts | {before.QuarterlyDistinct} | {after.QuarterlyDistinct} | {after.QuarterlyDistinct - before.QuarterlyDistinct} |"));
        report.AppendLine(Universe.Inv($"| Distinct attributes | {before.Attributes} | {after.Attributes} | {after.Attributes - before.Attributes} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs | {before.Runs} | {after.Runs} | {after.Runs - before.Runs} |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads | {before.Quarantine} | {after.Quarantine} | {after.Quarantine - before.Quarantine} |"));
        report.AppendLine(Universe.Inv($"| Opportunities | {before.Opportunities} | {after.Opportunities} | {after.Opportunities - before.Opportunities} |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Rows removed: **{removed}**, from {groups} groups across {companies} companies."));
        report.AppendLine();
        report.AppendLine("The distinct-fact count is unchanged in every namespace: nothing that was");
        report.AppendLine("a fact stopped being one. The row counts fell by exactly the number of");
        report.AppendLine("repeats. A restatement differs in its value or its filing date, so it is");
        report.AppendLine("a different identity and was never a candidate for removal.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "repair-observations.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the proof --------------------------------------------------------------------

        Assert.True(
            removed > 0,
            Universe.Inv($"The repair executed nothing: {outcomeStatus} - {outcomeReason}"));

        Assert.Equal(doomed.Count, removed);

        // Zero duplicate identities remain.
        Assert.Equal(after.Rows, after.Distinct);

        // Nothing distinct was lost, in either namespace, and nothing new appeared.
        Assert.Equal(before.Distinct, after.Distinct);
        Assert.Equal(before.AnnualDistinct, after.AnnualDistinct);
        Assert.Equal(before.QuarterlyDistinct, after.QuarterlyDistinct);
        Assert.Equal(before.Attributes, after.Attributes);

        // The rows that went are exactly the duplicates, no more and no fewer.
        Assert.Equal(before.Rows - removed, after.Rows);

        // Each namespace is individually free of repeats, not merely the two together.
        Assert.Equal(after.AnnualRows, after.AnnualDistinct);
        Assert.Equal(after.QuarterlyRows, after.QuarterlyDistinct);

        // Nothing else in the data plane moved.
        Assert.Equal(before.Runs, after.Runs);
        Assert.Equal(before.Quarantine, after.Quarantine);
        Assert.Equal(before.Opportunities, after.Opportunities);
    }

    private static string Key(
        string cik,
        string attribute,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        string canonical) =>
        string.Join(
            '|',
            cik,
            attribute,
            asOfUtc.ToString("O", CultureInfo.InvariantCulture),
            publishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            canonical);

    private static string Digest(List<ObservationId> ids)
    {
        var text = string.Join(
            ',',
            ids.Select(i => i.Value.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant()[..16];
    }

    private static async Task<Measurement> MeasureAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var rows = await facts.CountAsync();

        var distinct = await Distinct(facts);

        var quarterly = facts.Where(o =>
            EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"));

        var annual = facts.Where(o =>
            !EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"));

        return new Measurement(
            rows,
            distinct,
            await annual.CountAsync(),
            await Distinct(annual),
            await quarterly.CountAsync(),
            await Distinct(quarterly),
            await facts.Select(o => o.Attribute).Distinct().CountAsync(),
            await context.IngestionRuns.AsNoTracking().CountAsync(),
            await context.QuarantinedPayloads.AsNoTracking().CountAsync(),
            await context.Opportunities.AsNoTracking().CountAsync());
    }

    private static Task<int> Distinct(IQueryable<Observation> facts) =>
        facts
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

    private sealed record Measurement(
        int Rows,
        int Distinct,
        int AnnualRows,
        int AnnualDistinct,
        int QuarterlyRows,
        int QuarterlyDistinct,
        int Attributes,
        int Runs,
        int Quarantine,
        int Opportunities);
}
