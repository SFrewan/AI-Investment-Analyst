using System.Diagnostics;
using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>What the re-stamp is correcting, as the safety seam sees it.</summary>
internal sealed record SessionRestampParameters(int Rows, int Symbols, int Refused)
    : IActionParameters
{
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Correct the session-close timestamp on {Rows} closing-price rows across {Symbols} symbols, each stamped from the daylight-saving close on a date the US market was on standard time. The trading date, the price, the subject and the retrieval instant are copied unchanged; only the session close and the publication instant move, by exactly one hour, later. {Refused} rows were left alone because correcting them would put publication after retrieval.");
}

/// <summary>
/// Re-stamps the stored closing prices that were written from a single static session close.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What was wrong.</strong> Configuration states one US session close and the normaliser
/// used it for every date. 450 of the 1,304 weekday sessions in the sealed window are on standard
/// time, so about a third of the stored history was stamped an hour early - and
/// <c>PublishedAtUtc</c> is the field every point-in-time judgement reads.
/// </para>
/// <para>
/// <strong>Why this corrects rather than re-normalises from the archive.</strong> The correction
/// is a function of the stored row alone: same subject, same attribute, same trading date, same
/// price, same retrieval instant, and a session close that is either right already or exactly an
/// hour late. Deriving it from the row makes it impossible for this remediation to change a price,
/// which re-reading a payload could. Raw-close semantics are preserved by construction, because no
/// price is recomputed - each one is copied.
/// </para>
/// <para>
/// <strong>Why rows are replaced rather than edited.</strong> An observation is identified by its
/// subject, attribute, period end, publication instant and canonical value. Moving the publication
/// instant makes it a different observation, so the corrected row is written and the mis-stamped
/// one removed, in one authorisation window with one audit record. Rows already correct are not
/// touched at all - they keep their identity, and the test proves it.
/// </para>
/// <para>
/// <strong>Where it refuses.</strong> A row whose corrected publication instant would fall after
/// the moment the document was retrieved cannot be written: the ordering rules that make a fact a
/// fact would refuse it, and rightly. Those rows are left exactly as they are and counted, because
/// a remediation that silently dropped them would be worse than the defect.
/// </para>
/// </remarks>
public sealed class PriceSessionRemediationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_PRICE_SESSION_REMEDIATION";

    private static readonly ActionType Restamp =
        ActionType.Create("normalization.restamp-session-close");

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public PriceSessionRemediationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_stored_close_is_stamped_from_the_session_that_actually_closed()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The session re-stamp is off. Set {GateVariable}=1 to run it. It makes no provider " +
            "call of any kind.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;

        var before = await MeasureAsync(root);

        Skip.If(before.Rows == 0, "No closing prices are stored, so there is nothing to re-stamp.");

        // ---- what is wrong, decided from the rows themselves -----------------------------------

        List<Correction> corrections;
        List<Correction> refused;
        ExchangeSessionOptions session;

        using (var scope = root.CreateScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<AppDbContext>();
            var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;

            session = options.Session(UsEquitySessionCalendar.UnitedStates)
                ?? new ExchangeSessionOptions
                {
                    Code = UsEquitySessionCalendar.UnitedStates,
                    SessionCloseUtc = TimeSpan.FromHours(20),
                    PublicationDelay = TimeSpan.FromHours(4),
                };

            var rows = await context.Observations
                .AsNoTracking()
                .Where(o => o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute)
                .Select(o => new StoredRow(
                    o.Id,
                    o.Subject.Kind,
                    o.Subject.Identifier!,
                    o.Value.Canonical,
                    o.Provenance.SourceId,
                    o.Provenance.SourceRecordId,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                    o.Provenance.RetrievedAtUtc))
                .ToListAsync();

            var all = new List<Correction>();

            foreach (var row in rows)
            {
                var tradingDate = row.AsOfUtc.Date;
                var correct = UsEquitySessionCalendar.CloseOn(session, tradingDate);

                if (row.AsOfUtc.TimeOfDay == correct)
                {
                    continue;
                }

                var correctedAsOf = DateTime.SpecifyKind(tradingDate + correct, DateTimeKind.Utc);
                var delay = row.PublishedAtUtc - row.AsOfUtc;

                all.Add(new Correction(row, correctedAsOf, correctedAsOf + delay));
            }

            // A corrected publication instant after the retrieval instant cannot be written, and
            // the row is better left an hour early than removed.
            refused = all.Where(c => c.PublishedAtUtc > c.Row.RetrievedAtUtc).ToList();
            corrections = all.Where(c => c.PublishedAtUtc <= c.Row.RetrievedAtUtc).ToList();
        }

        var untouched = before.Rows - corrections.Count - refused.Count;

        // ---- one proposal, one window, one audit record ------------------------------------------

        var removed = 0;
        var added = 0;
        var failures = new List<string>();
        string outcomeStatus;
        string? outcomeReason;

        using (var scope = root.CreateScope())
        {
            var services = scope.ServiceProvider;
            var context = services.GetRequiredService<AppDbContext>();
            var gateway = services.GetRequiredService<IActionGateway>();
            var now = services.GetRequiredService<IClock>().UtcNow;

            var symbols = corrections
                .Select(c => c.Row.Identifier)
                .Distinct(StringComparer.Ordinal)
                .Count();

            var proposal = ActionProposal.Create(
                CorrelationId.Create(Universe.Inv($"session-restamp-{now:yyyyMMddHHmmss}")),

                // The normalisation pipeline correcting its own output. Nothing is destroyed that
                // the archive could not reproduce, and no retention decision is being taken.
                Capability.DataIngestion,
                Restamp,
                ActionTarget.Create("Observation", "eodhd-close-session"),
                new SessionRestampParameters(corrections.Count, symbols, refused.Count),

                // Reversible: the correction is arithmetic on the row, and the inverse is the
                // same arithmetic.
                ActionEconomics.NoFinancialEffect(),
                ProposedBy.Service("price-session-remediation", "1.0"),

                // Keyed on the exact rows, so it cannot replay onto a different set and a later
                // genuine correction is not suppressed by this one.
                Universe.Inv($"normalization.restamp-session-close:{Digest(corrections)}"),
                now);

            var outcome = await gateway.DispatchAsync(
                proposal,
                async token =>
                {
                    var ids = corrections.Select(c => c.Row.Id).ToHashSet();

                    var doomed = await context.Observations
                        .Where(o => ids.Contains(o.Id))
                        .ToListAsync(token);

                    var written = new List<Observation>(doomed.Count);
                    var replaced = new HashSet<ObservationId>();

                    foreach (var entity in doomed)
                    {
                        var correction = corrections.First(c => c.Row.Id == entity.Id);

                        // A FRESH value, never the tracked instance. An owned value belongs to
                        // exactly one owner by reference, and handing the removed row's instance to
                        // its replacement is how the subject columns came out null the last time
                        // this trap was met. The canonical text is parsed, not recomputed, so the
                        // price cannot change.
                        if (!decimal.TryParse(
                                entity.Value.Canonical,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture,
                                out var price))
                        {
                            failures.Add(Universe.Inv(
                                $"{correction.Row.Identifier} {correction.Row.AsOfUtc:yyyy-MM-dd}: the stored close '{entity.Value.Canonical}' is not a number this correction can copy."));

                            continue;
                        }

                        try
                        {
                            written.Add(Observation.RecordFact(
                                entity.Subject,
                                entity.Attribute,
                                ObservationValue.Number(price),
                                Provenance.Create(
                                    entity.Provenance.SourceId,
                                    correction.AsOfUtc,
                                    correction.PublishedAtUtc,
                                    entity.Provenance.RetrievedAtUtc,
                                    entity.Provenance.SourceRecordId,
                                    entity.Provenance.SourceUrl),
                                entity.Caveats));

                            replaced.Add(entity.Id);
                        }
                        catch (DomainValidationException exception)
                        {
                            failures.Add(Universe.Inv(
                                $"{correction.Row.Identifier} {correction.Row.AsOfUtc:yyyy-MM-dd}: {exception.Message}"));
                        }
                    }

                    // Only rows whose replacement was actually built are removed. A row whose
                    // correction the domain refused keeps the stamp it has rather than vanishing,
                    // which is why the set is collected as the replacements are made rather than
                    // reconstructed afterwards from values that could collide.
                    var removable = doomed.Where(d => replaced.Contains(d.Id)).ToList();

                    context.Observations.RemoveRange(removable);
                    await context.Observations.AddRangeAsync(written, token);
                    await context.SaveChangesAsync(token);

                    removed = removable.Count;
                    added = written.Count;

                    return written.Count;
                });

            outcomeStatus = outcome.Status.ToString();
            outcomeReason = outcome.Reason;
        }

        // ---- what it did -------------------------------------------------------------------------

        var after = await MeasureAsync(root);

        var report = Compose(
            session, before, after, corrections, refused, removed, added, untouched,
            failures, outcomeStatus, outcomeReason, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "price-session-remediation.md"),
            report);

        _output.WriteLine(report);

        // Nothing gained, nothing lost: one row out for one row in.
        Assert.Equal(removed, added);
        Assert.Equal(before.Rows, after.Rows);

        // The prices themselves are untouched: same distinct values, same distinct trading dates.
        Assert.Equal(before.DistinctValues, after.DistinctValues);
        Assert.Equal(before.DistinctSessionDates, after.DistinctSessionDates);
        Assert.Equal(before.Symbols, after.Symbols);

        // Nothing outside the price attribute moved.
        Assert.Equal(before.OtherRows, after.OtherRows);
        Assert.Equal(before.Runs, after.Runs);
        Assert.Equal(before.Opportunities, after.Opportunities);
        Assert.Equal(before.Quarantine, after.Quarantine);
        Assert.Equal(before.SplitRows, after.SplitRows);

        // Point-in-time invariants, re-proved on the corrected store.
        Assert.Equal(0, after.PublishedBeforePeriod);
        Assert.Equal(0, after.PublishedAfterRetrieval);

        // And the defect is gone: every stored close now agrees with the session that closed.
        Assert.Equal(refused.Count, after.MisStamped);

        // No duplicate identities were CREATED by the replacement. The store already held
        // repeated publications of the same session - the same trading date re-fetched and
        // re-normalised produces the same two instants, so the second copy is identical rather
        // than a later publication - and correcting a timestamp maps each row to exactly one
        // corrected row, so a pre-existing pair stays a pair. What must not happen is the count
        // going up, which is what this asserts.
        Assert.True(
            after.Rows - after.DistinctIdentities <= before.Rows - before.DistinctIdentities,
            Universe.Inv($"Duplicate closing-price rows rose from {before.Rows - before.DistinctIdentities} to {after.Rows - after.DistinctIdentities}."));
    }

    // ---- measuring -----------------------------------------------------------------------------

    private static async Task<Counts> MeasureAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var options = services.GetRequiredService<IOptions<EodhdOptions>>().Value;

        var session = options.Session(UsEquitySessionCalendar.UnitedStates)
            ?? new ExchangeSessionOptions
            {
                Code = UsEquitySessionCalendar.UnitedStates,
                SessionCloseUtc = TimeSpan.FromHours(20),
                PublicationDelay = TimeSpan.FromHours(4),
            };

        var prices = await context.Observations
            .AsNoTracking()
            .Where(o => o.Attribute == EodhdDailyPriceNormalizer.CloseAttribute)
            .Select(o => new
            {
                o.Subject.Identifier,
                o.Value.Canonical,
                o.Provenance.AsOfUtc,
                o.Provenance.PublishedAtUtc,
                o.Provenance.RetrievedAtUtc,
            })
            .ToListAsync();

        var misStamped = prices.Count(p =>
            p.AsOfUtc.TimeOfDay != UsEquitySessionCalendar.CloseOn(session, p.AsOfUtc.Date));

        return new Counts(
            prices.Count,
            prices.Select(p => p.Identifier).Distinct(StringComparer.Ordinal).Count(),
            prices.Select(p => p.Canonical).Distinct(StringComparer.Ordinal).Count(),
            prices.Select(p => p.AsOfUtc.Date).Distinct().Count(),
            prices
                .Select(p => string.Join(
                    '|',
                    p.Identifier,
                    p.Canonical,
                    p.AsOfUtc.ToString("O", CultureInfo.InvariantCulture),
                    p.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture)))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            misStamped,
            prices.Count(p => p.PublishedAtUtc < p.AsOfUtc),
            prices.Count(p => p.PublishedAtUtc > p.RetrievedAtUtc),
            await context.Observations.CountAsync(o =>
                o.Attribute != EodhdDailyPriceNormalizer.CloseAttribute),
            await context.Observations.CountAsync(o => o.Attribute == "security.split-ratio"),
            await context.IngestionRuns.CountAsync(),
            await context.Opportunities.CountAsync(),
            await context.QuarantinedPayloads.CountAsync());
    }

    private static string Digest(List<Correction> corrections)
    {
        var canonical = string.Join(
            '\n',
            corrections
                .Select(c => c.Row.Id.ToString())
                .OrderBy(s => s, StringComparer.Ordinal));

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }

    // ---- reporting -----------------------------------------------------------------------------

    private static string Compose(
        ExchangeSessionOptions session,
        Counts before,
        Counts after,
        List<Correction> corrections,
        List<Correction> refused,
        int removed,
        int added,
        int untouched,
        List<string> failures,
        string outcomeStatus,
        string? outcomeReason,
        Stopwatch watch)
    {
        var report = new StringBuilder();

        report.AppendLine("# Session-close remediation - the stored closes, re-stamped");
        report.AppendLine();
        report.AppendLine("**No provider call of any kind.** One proposal, one authorisation window,");
        report.AppendLine("one save, one audit record. No price was recomputed: each corrected row");
        report.AppendLine("copies its price, subject, trading date and retrieval instant, and moves");
        report.AppendLine("only the session close and the publication instant.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Seam outcome: **{outcomeStatus}** - {outcomeReason}"));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"Configured US close {session.SessionCloseUtc:hh\\:mm} UTC on daylight time, an hour later on standard time, publication delay {session.PublicationDelay:g}."));
        report.AppendLine();

        report.AppendLine("| | Before | After |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| Closing-price rows | {before.Rows} | {after.Rows} |"));
        report.AppendLine(Universe.Inv($"| Symbols | {before.Symbols} | {after.Symbols} |"));
        report.AppendLine(Universe.Inv($"| Distinct trading dates | {before.DistinctSessionDates} | {after.DistinctSessionDates} |"));
        report.AppendLine(Universe.Inv($"| Distinct prices | {before.DistinctValues} | {after.DistinctValues} |"));
        report.AppendLine(Universe.Inv($"| Distinct row identities | {before.DistinctIdentities} | {after.DistinctIdentities} |"));
        report.AppendLine(Universe.Inv($"| **Repeated publications already present** | **{before.Rows - before.DistinctIdentities}** | **{after.Rows - after.DistinctIdentities}** |"));
        report.AppendLine(Universe.Inv($"| **Mis-stamped rows** | **{before.MisStamped}** | **{after.MisStamped}** |"));
        report.AppendLine(Universe.Inv($"| Published before the period | {before.PublishedBeforePeriod} | {after.PublishedBeforePeriod} |"));
        report.AppendLine(Universe.Inv($"| Published after retrieval | {before.PublishedAfterRetrieval} | {after.PublishedAfterRetrieval} |"));
        report.AppendLine(Universe.Inv($"| Non-price observations | {before.OtherRows} | {after.OtherRows} |"));
        report.AppendLine(Universe.Inv($"| Split rows | {before.SplitRows} | {after.SplitRows} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs | {before.Runs} | {after.Runs} |"));
        report.AppendLine(Universe.Inv($"| Opportunities | {before.Opportunities} | {after.Opportunities} |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads | {before.Quarantine} | {after.Quarantine} |"));
        report.AppendLine();

        report.AppendLine("## What moved");
        report.AppendLine();
        report.AppendLine("| | Rows |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Already correct, untouched | {untouched} |"));
        report.AppendLine(Universe.Inv($"| Corrected | {corrections.Count} |"));
        report.AppendLine(Universe.Inv($"| Removed / written | {removed} / {added} |"));
        report.AppendLine(Universe.Inv($"| Refused - correction would publish after retrieval | {refused.Count} |"));
        report.AppendLine(Universe.Inv($"| Refused by the domain when written | {failures.Count} |"));
        report.AppendLine();

        if (refused.Count > 0)
        {
            report.AppendLine("Refused rows keep the stamp they have. They are an hour early and are");
            report.AppendLine("recorded here rather than removed, because a remediation that deleted");
            report.AppendLine("what it could not fix would be worse than the defect.");
            report.AppendLine();
        }

        foreach (var failure in failures.Take(10))
        {
            report.AppendLine(Universe.Inv($"- {failure}"));
        }

        report.AppendLine();
        if (before.Rows - before.DistinctIdentities > 0)
        {
            report.AppendLine(Universe.Inv(
                $"**Found while measuring, and not caused by this remediation:** {before.Rows - before.DistinctIdentities} closing-price rows repeat another row's subject, price, session close and publication instant exactly. A trading date re-fetched and re-normalised yields the same two instants, so the second copy is a duplicate rather than a later publication. Gate 12 does not see these: it counts the financial-statement namespace, and prices are not in it. This remediation neither created them nor removed them - correcting a timestamp maps each row to exactly one corrected row, so a pair stays a pair - and it is reported here because nothing else was looking."));
            report.AppendLine();
        }

        report.AppendLine("Raw-close semantics are preserved by construction: no price is read from a");
        report.AppendLine("payload or recomputed here, so this remediation cannot change one.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        return report.ToString();
    }

    private sealed record StoredRow(
        ObservationId Id,
        string Kind,
        string Identifier,
        string Canonical,
        SourceId SourceId,
        string? SourceRecordId,
        DateTime AsOfUtc,
        DateTime PublishedAtUtc,
        DateTime RetrievedAtUtc);

    private sealed record Correction(StoredRow Row, DateTime AsOfUtc, DateTime PublishedAtUtc);

    private sealed record Counts(
        int Rows,
        int Symbols,
        int DistinctValues,
        int DistinctSessionDates,
        int DistinctIdentities,
        int MisStamped,
        int PublishedBeforePeriod,
        int PublishedAfterRetrieval,
        int OtherRows,
        int SplitRows,
        int Runs,
        int Opportunities,
        int Quarantine);
}
