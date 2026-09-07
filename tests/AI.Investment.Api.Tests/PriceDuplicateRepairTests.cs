using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Observations;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>What the price repair is removing, as the safety seam sees it.</summary>
internal sealed record PriceDuplicateRemovalParameters(int Rows, int Groups, int Symbols)
    : IActionParameters
{
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Remove {Rows} closing-price rows across {Groups} groups over {Symbols} symbols, each of which repeats another row's subject, attribute, session close, publication instant and canonical price exactly. The earliest-retrieved row of each group is kept, so the surviving row is the one whose provenance came first. No price, session or publication instant is altered and nothing distinct is removed.");
}

/// <summary>
/// Removes the duplicate closing-price rows, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where they came from.</strong> A trading date fetched a second time normalises to the
/// same session close and the same publication instant, because both are derived from the date
/// rather than sent by the vendor. The second copy therefore carries no new information and is a
/// duplicate rather than a later publication - which is exactly the distinction the observation
/// identity encodes.
/// </para>
/// <para>
/// <strong>Why nobody noticed.</strong> Gate 12 counted the financial-statement namespace. Prices
/// are not in it, so the gate read PASS over 649,597 financial rows while 5,249 duplicate price
/// rows sat beside them. The rule was right and its coverage was not, which is the failure mode
/// that leaves no trace.
/// </para>
/// <para>
/// <strong>Which row survives.</strong> The earliest-retrieved of each group. Every row in a group
/// is identical in all five identity parts, so the choice cannot change a value - but it can
/// change which retrieval instant remains, and keeping the earliest keeps the provenance that
/// actually came first.
/// </para>
/// </remarks>
public sealed class PriceDuplicateRepairTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_PRICE_DEDUPLICATION";

    private static readonly ActionType RemoveDuplicates =
        ActionType.Create("normalization.remove-price-duplicates");

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PriceDuplicateRepairTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Duplicate_price_rows_are_removed_and_every_distinct_price_is_left_alone()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The price de-duplication is off. Set {GateVariable}=1 to run it. It makes no " +
            "provider call of any kind.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;

        var before = await MeasureAsync(root);

        // ---- the diagnosis, by the store's own identity ------------------------------------------

        List<Group> groups;

        using (var scope = root.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var rows = await context.Observations
                .AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
                .Select(o => new Row(
                    o.Id,
                    o.Subject.Kind,
                    o.Subject.Identifier,
                    o.Attribute,
                    o.Value.Canonical,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                    o.Provenance.RetrievedAtUtc))
                .ToListAsync();

            groups = rows
                .GroupBy(r => ObservationDeduplication.Key(
                    r.Kind, r.Identifier, r.Attribute, r.AsOfUtc, r.PublishedAtUtc, r.Canonical),
                    StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => new Group(
                    g.Key,

                    // Earliest retrieval survives; ties broken by id so the choice is total and
                    // reproducible rather than dependent on the order the database happened to
                    // return rows in.
                    g.OrderBy(r => r.RetrievedAtUtc)
                        .ThenBy(r => r.Id.ToString(), StringComparer.Ordinal)
                        .First(),
                    g.OrderBy(r => r.RetrievedAtUtc)
                        .ThenBy(r => r.Id.ToString(), StringComparer.Ordinal)
                        .Skip(1)
                        .ToList()))
                .ToList();
        }

        var doomed = groups.SelectMany(g => g.Doomed).Select(r => r.Id).ToList();

        var symbols = groups
            .Select(g => g.Kept.Identifier ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Count();

        // The diagnosis must agree with the count the gate arrives at independently.
        Assert.Equal(
            ObservationDeduplication.Excess(before.PriceRows, before.PriceIdentities),
            doomed.Count);

        // ---- one proposal, one window, one audit record --------------------------------------------

        var removed = 0;
        string outcomeStatus;
        string? outcomeReason;

        using (var scope = root.CreateScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<AppDbContext>();
            var gateway = services.GetRequiredService<IActionGateway>();
            var now = services.GetRequiredService<IClock>().UtcNow;

            var proposal = ActionProposal.Create(
                CorrelationId.Create(Universe.Inv($"price-dedup-{now:yyyyMMddHHmmss}")),

                // Normalisation correcting its own output. Nothing is destroyed that an identical
                // surviving row does not already carry, so this is not a retention decision.
                Capability.DataIngestion,
                RemoveDuplicates,
                ActionTarget.Create("Observation", "eodhd-close-duplicates"),
                new PriceDuplicateRemovalParameters(doomed.Count, groups.Count, symbols),

                // Reversible: every row removed is identical to one that stays.
                ActionEconomics.NoFinancialEffect(),
                ProposedBy.Service("price-duplicate-repair", "1.0"),

                // Keyed on the exact set, so it cannot replay onto a different one and a later
                // genuine repair is not suppressed by this one.
                Universe.Inv($"normalization.remove-price-duplicates:{Digest(doomed)}"),
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

        // ---- what it did ----------------------------------------------------------------------------

        var after = await MeasureAsync(root);

        var report = Compose(
            before, after, groups, doomed.Count, removed, symbols,
            outcomeStatus, outcomeReason, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "price-deduplication.md"),
            report);

        _output.WriteLine(report);

        // Exactly the duplicates went, and nothing else.
        Assert.Equal(doomed.Count, removed);
        Assert.Equal(before.PriceRows - doomed.Count, after.PriceRows);

        // NOTHING DISTINCT WAS LOST. This is the assertion the whole repair rests on: the number
        // of distinct identities, distinct prices, distinct sessions and distinct symbols is the
        // same on both sides. A repair that removed a real price would move one of these.
        Assert.Equal(before.PriceIdentities, after.PriceIdentities);
        Assert.Equal(before.DistinctPrices, after.DistinctPrices);
        Assert.Equal(before.DistinctSessions, after.DistinctSessions);
        Assert.Equal(before.Symbols, after.Symbols);

        // The financial namespace must actually contain rows. A namespace query that matches
        // nothing reports a clean PASS, which is how a de-duplication check can be green and
        // meaningless at the same time - the mistake this stage already made once.
        Assert.True(
            after.FinancialRows > 0,
            "The financial namespace query matched no rows, so its de-duplication result is "
            + "vacuous rather than clean.");

        // Zero duplicates remain, in both namespaces.
        Assert.True(ObservationDeduplication.Passes(after.PriceRows, after.PriceIdentities));
        Assert.True(ObservationDeduplication.Passes(after.FinancialRows, after.FinancialIdentities));

        // The financial namespace is untouched, and so is everything that is not a price.
        Assert.Equal(before.FinancialRows, after.FinancialRows);
        Assert.Equal(before.SplitRows, after.SplitRows);
        Assert.Equal(before.Runs, after.Runs);
        Assert.Equal(before.Opportunities, after.Opportunities);
        Assert.Equal(before.Quarantine, after.Quarantine);

        // Point-in-time invariants still hold on the surviving rows.
        Assert.Equal(0, after.PublishedBeforePeriod);
        Assert.Equal(0, after.PublishedAfterRetrieval);

        // IDEMPOTENT: running the same diagnosis against the repaired store finds nothing.
        var second = await DuplicateCountAsync(root);

        Assert.Equal(0, second);
    }

    // ---- measuring -----------------------------------------------------------------------------------

    private static async Task<Counts> MeasureAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var prices = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
            .Select(o => new
            {
                o.Subject.Kind,
                o.Subject.Identifier,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
                o.Provenance.RetrievedAtUtc,
            })
            .ToListAsync();

        var financial = await context.Observations
            .AsNoTracking()
            .Where(o => EF.Functions.Like(o.Attribute, ObservationDeduplication.FinancialPrefix + "%"))
            .Select(o => new
            {
                o.Subject.Kind,
                o.Subject.Identifier,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
            })
            .ToListAsync();

        return new Counts(
            prices.Count,
            prices
                .Select(p => ObservationDeduplication.Key(
                    p.Kind, p.Identifier, p.Attribute, p.AsOfUtc, p.PublishedAtUtc, p.Canonical))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            prices.Select(p => p.Identifier).Distinct(StringComparer.Ordinal).Count(),
            prices.Select(p => p.Canonical).Distinct(StringComparer.Ordinal).Count(),
            prices.Select(p => p.AsOfUtc).Distinct().Count(),
            prices.Count(p => p.PublishedAtUtc < p.AsOfUtc),
            prices.Count(p => p.PublishedAtUtc > p.RetrievedAtUtc),
            financial.Count,
            financial
                .Select(f => ObservationDeduplication.Key(
                    f.Kind, f.Identifier, f.Attribute, f.AsOfUtc, f.PublishedAtUtc, f.Canonical))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            await context.Observations.CountAsync(o =>
                o.Attribute == ObservationDeduplication.SplitAttribute),
            await context.IngestionRuns.CountAsync(),
            await context.Opportunities.CountAsync(),
            await context.QuarantinedPayloads.CountAsync());
    }

    /// <summary>The diagnosis, re-run from nothing. Used to prove idempotency.</summary>
    private static async Task<int> DuplicateCountAsync(IServiceProvider root)
    {
        var counts = await MeasureAsync(root);

        return ObservationDeduplication.Excess(counts.PriceRows, counts.PriceIdentities);
    }

    private static string Digest(List<ObservationId> ids)
    {
        var canonical = string.Join(
            '\n',
            ids.Select(i => i.ToString()).OrderBy(s => s, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }

    // ---- reporting ------------------------------------------------------------------------------------

    private static string Compose(
        Counts before,
        Counts after,
        List<Group> groups,
        int doomed,
        int removed,
        int symbols,
        string outcomeStatus,
        string? outcomeReason,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        report.AppendLine("# Price de-duplication - the repeated rows, removed");
        report.AppendLine();
        report.AppendLine("**No provider call.** One proposal, one authorisation window, one save,");
        report.AppendLine("one audit record. Only rows repeating another row's subject, attribute,");
        report.AppendLine("session close, publication instant and canonical price were removed, and");
        report.AppendLine("the earliest-retrieved row of each group was kept.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Seam outcome: **{outcomeStatus}** - {outcomeReason}"));
        report.AppendLine();

        report.AppendLine("## Diagnosis, by the store's own identity");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Duplicate groups | {groups.Count} |"));
        report.AppendLine(Universe.Inv($"| Symbols affected | {symbols} |"));
        report.AppendLine(Universe.Inv($"| Rows to remove | {doomed} |"));
        report.AppendLine(Universe.Inv($"| Largest group | {(groups.Count == 0 ? 0 : groups.Max(g => g.Doomed.Count + 1))} rows |"));
        report.AppendLine();

        report.AppendLine("## Before and after");
        report.AppendLine();
        report.AppendLine("| | Before | After |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| Closing-price rows | {before.PriceRows} | {after.PriceRows} |"));
        report.AppendLine(Universe.Inv($"| **Distinct price identities** | **{before.PriceIdentities}** | **{after.PriceIdentities}** |"));
        report.AppendLine(Universe.Inv($"| Distinct prices | {before.DistinctPrices} | {after.DistinctPrices} |"));
        report.AppendLine(Universe.Inv($"| Distinct sessions | {before.DistinctSessions} | {after.DistinctSessions} |"));
        report.AppendLine(Universe.Inv($"| Symbols | {before.Symbols} | {after.Symbols} |"));
        report.AppendLine(Universe.Inv($"| Financial rows | {before.FinancialRows} | {after.FinancialRows} |"));
        report.AppendLine(Universe.Inv($"| Distinct financial identities | {before.FinancialIdentities} | {after.FinancialIdentities} |"));
        report.AppendLine(Universe.Inv($"| Split rows | {before.SplitRows} | {after.SplitRows} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs | {before.Runs} | {after.Runs} |"));
        report.AppendLine(Universe.Inv($"| Opportunities | {before.Opportunities} | {after.Opportunities} |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads | {before.Quarantine} | {after.Quarantine} |"));
        report.AppendLine(Universe.Inv($"| Published before the period | {before.PublishedBeforePeriod} | {after.PublishedBeforePeriod} |"));
        report.AppendLine(Universe.Inv($"| Published after retrieval | {before.PublishedAfterRetrieval} | {after.PublishedAfterRetrieval} |"));
        report.AppendLine();

        report.AppendLine(Universe.Inv($"{removed} rows removed. **Distinct price identities are unchanged at {after.PriceIdentities}**, which is the proof that no price was lost: every removed row was identical, in all five identity parts, to one that stayed."));
        report.AppendLine();
        report.AppendLine("## Gate 12, across both namespaces");
        report.AppendLine();
        report.AppendLine("| Namespace | Rows | Distinct | Excess | Result |");
        report.AppendLine("| --- | ---: | ---: | ---: | --- |");
        report.AppendLine(Universe.Inv($"| financial | {after.FinancialRows} | {after.FinancialIdentities} | {ObservationDeduplication.Excess(after.FinancialRows, after.FinancialIdentities)} | {(ObservationDeduplication.Passes(after.FinancialRows, after.FinancialIdentities) ? "**PASS**" : "**FAIL**")} |"));
        report.AppendLine(Universe.Inv($"| market-data (`security.close`) | {after.PriceRows} | {after.PriceIdentities} | {ObservationDeduplication.Excess(after.PriceRows, after.PriceIdentities)} | {(ObservationDeduplication.Passes(after.PriceRows, after.PriceIdentities) ? "**PASS**" : "**FAIL**")} |"));
        report.AppendLine();
        report.AppendLine("Zero tolerance in both. The financial rule is unchanged; the price");
        report.AppendLine("namespace is now judged by the same one.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        return report.ToString();
    }

    private sealed record Row(
        ObservationId Id,
        string Kind,
        string? Identifier,
        string Attribute,
        string Canonical,
        DateTime AsOfUtc,
        DateTime PublishedAtUtc,
        DateTime RetrievedAtUtc);

    private sealed record Group(string Key, Row Kept, List<Row> Doomed);

    private sealed record Counts(
        int PriceRows,
        int PriceIdentities,
        int Symbols,
        int DistinctPrices,
        int DistinctSessions,
        int PublishedBeforePeriod,
        int PublishedAfterRetrieval,
        int FinancialRows,
        int FinancialIdentities,
        int SplitRows,
        int Runs,
        int Opportunities,
        int Quarantine);
}
