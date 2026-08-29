using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Portfolio;

/// <summary>
/// Builds the portfolio read model from the position events, the capital ledger and the
/// observation store.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three existing sources, no fourth.</strong> Holdings are replayed from position events;
/// cash is the capital ledger's own balance; prices come through
/// <see cref="PriceSeriesReader"/>, which is the same point-in-time read the discoverer uses. This
/// class stores nothing, caches nothing and calls no provider - a price it cannot find in the
/// observation store is a price the platform does not have.
/// </para>
/// <para>
/// <strong>The price read is point-in-time and restatement-aware</strong> because
/// <see cref="PriceSeriesReader"/> is. A valuation and the screen that produced an opportunity
/// therefore see the same series, which is what makes a shadow measurement comparable to the
/// portfolio it would have affected.
/// </para>
/// </remarks>
public sealed class PortfolioReader
{
    /// <summary>The subject kind an instrument is observed under.</summary>
    public const string SecurityKind = "Security";

    private readonly IPositionEventStore _events;
    private readonly ILedgerStore _ledger;
    private readonly PriceSeriesReader _prices;
    private readonly DiscoverySettings _settings;
    private readonly IClock _clock;

    public PortfolioReader(
        IPositionEventStore events,
        ILedgerStore ledger,
        PriceSeriesReader prices,
        DiscoverySettings settings,
        IClock clock)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<PortfolioSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var currency = Currency.Create(_settings.CurrencyCode);
        var nowUtc = _clock.UtcNow;

        var events = await _events.ListAsync(cancellationToken).ConfigureAwait(false);
        var positions = PositionCalculator.Replay(events);

        var entries = await _ledger.ListAsync(cancellationToken).ConfigureAwait(false);
        var cash = CapitalLedger.Balance(LedgerAccount.Cash, entries, currency);

        var views = new List<PositionView>(positions.Count);

        foreach (var position in positions)
        {
            views.Add(await ValueAsync(position, currency, nowUtc, cancellationToken)
                .ConfigureAwait(false));
        }

        return Summarise(views, currency, nowUtc, cash);
    }

    /// <summary>The positions alone, valued the same way.</summary>
    public async Task<IReadOnlyList<PositionView>> ReadPositionsAsync(
        CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken).ConfigureAwait(false)).Positions;

    /// <summary>
    /// Values one position, or states why it could not be.
    /// </summary>
    /// <remarks>
    /// A closed position is not "missing a price"; it needs none. Separating the two keeps a
    /// portfolio of settled trades from looking like a portfolio with a broken feed.
    /// </remarks>
    private async Task<PositionView> ValueAsync(
        Position position,
        Currency currency,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var exposure = position.CostBasis;

        if (!position.IsOpen)
        {
            return new PositionView(
                position.Instrument,
                position.Quantity,
                position.AverageCost,
                position.CostBasis,
                exposure,
                position.RealisedPnL,
                PriceAvailability.NotHeld,
                CurrentPrice: null,
                PriceAsOfUtc: null,
                PricePublishedAtUtc: null,
                MarketValue: null,
                UnrealisedPnL: null);
        }

        var series = await _prices.ReadAsync(
            IngestionSubject.Create(SecurityKind, position.Instrument),
            _settings.PriceAttribute,
            maxSessions: 1,
            asAtUtc: nowUtc,
            cancellationToken).ConfigureAwait(false);

        if (series.Count == 0)
        {
            // No fabricated price, no fallback to cost, no last-known value carried forward.
            return new PositionView(
                position.Instrument,
                position.Quantity,
                position.AverageCost,
                position.CostBasis,
                exposure,
                position.RealisedPnL,
                PriceAvailability.NoObservedPrice,
                CurrentPrice: null,
                PriceAsOfUtc: null,
                PricePublishedAtUtc: null,
                MarketValue: null,
                UnrealisedPnL: null);
        }

        var latest = series[^1];
        var marketValue = Money.Create(latest.Close * position.Quantity, currency);

        return new PositionView(
            position.Instrument,
            position.Quantity,
            position.AverageCost,
            position.CostBasis,
            exposure,
            position.RealisedPnL,
            PriceAvailability.Available,
            latest.Close,
            latest.SessionCloseUtc,
            latest.Provenance.PublishedAtUtc,
            marketValue,
            marketValue.Subtract(position.CostBasis));
    }

    /// <summary>
    /// Totals what can be totalled, and reports what could not.
    /// </summary>
    /// <remarks>
    /// The market value and the total are null unless every open position was valued. A total that
    /// silently skipped the unpriced positions would be a smaller number that still looked like an
    /// answer, and somebody would compare it against a limit.
    /// </remarks>
    private static PortfolioSnapshot Summarise(
        List<PositionView> views,
        Currency currency,
        DateTime nowUtc,
        Money cash)
    {
        var costBasis = Money.Zero(currency);
        var realised = Money.Zero(currency);
        var marketValue = Money.Zero(currency);
        var open = 0;
        var valued = 0;
        var unvalued = 0;

        foreach (var view in views)
        {
            costBasis = costBasis.Add(view.CostBasis);
            realised = realised.Add(view.RealisedPnL);

            if (!view.IsOpen)
            {
                continue;
            }

            open++;

            if (view.MarketValue is { } value)
            {
                valued++;
                marketValue = marketValue.Add(value);
            }
            else
            {
                unvalued++;
            }
        }

        var complete = unvalued == 0;

        return new PortfolioSnapshot(
            currency,
            nowUtc,
            cash,
            costBasis,
            realised,
            complete ? marketValue.Subtract(costBasis) : null,
            complete ? marketValue : null,
            complete ? cash.Add(marketValue) : null,
            open,
            valued,
            unvalued,
            views);
    }
}
