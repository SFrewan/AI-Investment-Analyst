using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>
/// Builds the exposure snapshot the limit engine is evaluated against, from the ledger.
/// </summary>
/// <remarks>
/// <para>
/// Every figure here is a projection of immutable entries. Nothing is read from a stored balance,
/// because no stored balance exists - which means the numbers the ceilings are compared against
/// cannot drift from the postings behind them.
/// </para>
/// <para>
/// <strong>Peak equity is computed over the entries rather than remembered.</strong> A stored
/// high-water mark is a number that can only be corrected by editing history, and drawdown measured
/// from a wrong peak is wrong in the direction that permits more.
/// </para>
/// <para>
/// Action counts come from the audit trail, which is the only record of what was actually done -
/// as opposed to what was proposed or intended.
/// </para>
/// </remarks>
public sealed class LedgerExposureProvider : IExposureProvider
{
    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;

    public LedgerExposureProvider(AppDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ExposureSnapshot> GetAsync(
        Currency currency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currency);

        var nowUtc = _clock.UtcNow;
        var startOfDay = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);

        var entries = (await _dbContext.LedgerEntries
                .AsNoTracking()
                .OrderBy(entry => entry.OccurredAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(entry => entry.Amount.Currency == currency)
            .ToList();

        var positions = CapitalLedger.Balance(LedgerAccount.Positions, entries, currency);
        var equity = Equity(entries, currency);
        var peak = PeakEquity(entries, currency);

        var lossesToday = entries
            .Where(entry => entry.OccurredAtUtc >= startOfDay)
            .Aggregate(
                Money.Zero(currency),
                (running, entry) => running.Add(entry.EffectOn(LedgerAccount.RealisedLosses)));

        var lastLoss = entries
            .Where(entry => entry.Debit == LedgerAccount.RealisedLosses)
            .Select(entry => (DateTime?)entry.OccurredAtUtc)
            .LastOrDefault();

        var actionsToday = await ActionsTodayAsync(startOfDay, cancellationToken).ConfigureAwait(false);

        var byInstrument = await ExposureByInstrumentAsync(currency, cancellationToken)
            .ConfigureAwait(false);

        return ExposureSnapshot.Create(
            currency,
            positions.IsNegative ? Money.Zero(currency) : positions,
            peak,
            equity,
            lossesToday.IsNegative ? Money.Zero(currency) : lossesToday,

            // Zero, and correctly so: what the operating cycle in hand has spent is a property of
            // that cycle, and this class is repository-scoped and has never been told which cycle
            // it is serving. OperatingCycleRunner supplies the real figure through
            // ExposureSnapshot.WithCycleCost before the limit engine sees the snapshot. A caller
            // that evaluates limits without a cycle genuinely has no cycle cost, and zero is the
            // truthful answer for it.
            Money.Zero(currency),
            actionsToday,
            byInstrument,
            lastRealisedLossAtUtc: lastLoss);
    }

    /// <summary>
    /// What is held in each instrument, at cost, replayed from the position events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This map was <c>null</c> until Block 3, which meant <c>ExposureTo</c> answered zero for every
    /// instrument and the concentration ceiling could never bind. It now answers from the same
    /// events the portfolio read model replays, so an operator and the limit engine see one number.
    /// </para>
    /// <para>
    /// <strong>At cost, not at market value, and deliberately.</strong> The total these figures are
    /// compared against is the ledger's <c>Positions</c> balance, which is at cost; mixing the two
    /// bases would produce a ratio of two different things. It also means the ceiling does not
    /// depend on a price being observable - a concentration limit that silently loosened whenever a
    /// price feed went quiet would be worse than one that could not be computed at all.
    /// </para>
    /// <para>
    /// Positions closed to zero are omitted rather than reported as zero exposure: an instrument
    /// nothing is held in has no exposure to compare, and an entry of zero would be a claim.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, Money>> ExposureByInstrumentAsync(
        Currency currency,
        CancellationToken cancellationToken)
    {
        var events = await _dbContext.PositionEvents
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byInstrument = new Dictionary<string, Money>(StringComparer.OrdinalIgnoreCase);

        foreach (var position in PositionCalculator.Replay(events))
        {
            if (!position.IsOpen || position.CostBasis.Currency != currency)
            {
                continue;
            }

            byInstrument[position.Instrument] = position.CostBasis;
        }

        return byInstrument;
    }

    /// <summary>Cash plus positions, less what has been spent on fees and lost.</summary>
    private static Money Equity(List<LedgerEntry> entries, Currency currency) =>
        CapitalLedger.Balance(LedgerAccount.Cash, entries, currency)
            .Add(CapitalLedger.Balance(LedgerAccount.Positions, entries, currency));

    /// <summary>
    /// The highest equity the books have ever shown, replayed from the entries in order.
    /// </summary>
    private static Money PeakEquity(List<LedgerEntry> entries, Currency currency)
    {
        var running = Money.Zero(currency);
        var peak = Money.Zero(currency);

        foreach (var entry in entries)
        {
            running = running
                .Add(entry.EffectOn(LedgerAccount.Cash))
                .Add(entry.EffectOn(LedgerAccount.Positions));

            if (running.IsGreaterThan(peak))
            {
                peak = running;
            }
        }

        return peak;
    }

    /// <summary>
    /// How many actions each capability has executed since midnight, from the audit trail.
    /// </summary>
    /// <remarks>
    /// Only executions count. A denied or approval-pending proposal did nothing, and counting it
    /// against a daily ceiling would let a run of refusals lock out the actions that would have
    /// succeeded.
    /// </remarks>
    private async Task<Dictionary<Capability, int>> ActionsTodayAsync(
        DateTime startOfDay,
        CancellationToken cancellationToken)
    {
        var counts = await _dbContext.AuditRecords
            .AsNoTracking()
            .Where(record =>
                record.OccurredAtUtc >= startOfDay &&
                record.EventType == AuditEventType.ActionExecuted &&
                record.Capability != null)
            .GroupBy(record => record.Capability!.Value)
            .Select(group => new { Capability = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return counts.ToDictionary(entry => entry.Capability, entry => entry.Count);
    }
}
