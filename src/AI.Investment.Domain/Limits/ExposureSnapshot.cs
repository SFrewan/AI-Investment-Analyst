using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Limits;

/// <summary>
/// What the platform currently has at stake, as the limit engine needs to see it.
/// </summary>
/// <remarks>
/// <para>
/// A value object assembled by the application layer from the capital ledger and the audit trail,
/// rather than something the engine queries for itself. The engine is then a pure function of
/// (proposal, snapshot, limits), which is what makes it exhaustively testable - and testable is the
/// bar these particular checks are held to, because they are the ones standing between a defect and
/// a loss.
/// </para>
/// <para>
/// Every amount is in one currency. A snapshot mixing currencies would make "total exposure" a
/// number that is not a quantity of anything.
/// </para>
/// </remarks>
public sealed record ExposureSnapshot
{
    private readonly Dictionary<Capability, int> _actionsToday;
    private readonly Dictionary<string, Money> _exposureByInstrument;

    private ExposureSnapshot(
        Currency currency,
        Money totalExposure,
        Money peakEquity,
        Money currentEquity,
        Money realisedLossToday,
        Money cycleCost,
        Dictionary<Capability, int> actionsToday,
        Dictionary<string, Money> exposureByInstrument,
        DateTime? lastRealisedLossAtUtc)
    {
        Currency = currency;
        TotalExposure = totalExposure;
        PeakEquity = peakEquity;
        CurrentEquity = currentEquity;
        RealisedLossToday = realisedLossToday;
        CycleCost = cycleCost;
        _actionsToday = actionsToday;
        _exposureByInstrument = exposureByInstrument;
        LastRealisedLossAtUtc = lastRealisedLossAtUtc;
    }

    public Currency Currency { get; }

    public Money TotalExposure { get; }

    public Money PeakEquity { get; }

    public Money CurrentEquity { get; }

    /// <summary>Losses realised since midnight UTC, as a positive amount.</summary>
    public Money RealisedLossToday { get; }

    /// <summary>What the current operating cycle has spent on providers and models.</summary>
    public Money CycleCost { get; }

    public DateTime? LastRealisedLossAtUtc { get; }

    /// <summary>The fall from peak equity, never negative.</summary>
    public Money Drawdown =>
        PeakEquity.IsGreaterThan(CurrentEquity)
            ? PeakEquity.Subtract(CurrentEquity)
            : Money.Zero(Currency);

    public int ActionsToday(Capability capability) =>
        _actionsToday.TryGetValue(capability, out var count) ? count : 0;

    public Money ExposureTo(string? instrument) =>
        instrument is not null && _exposureByInstrument.TryGetValue(instrument, out var exposure)
            ? exposure
            : Money.Zero(Currency);

    /// <summary>Nothing at stake, nothing spent, nothing lost. The starting state.</summary>
    public static ExposureSnapshot Flat(Currency currency, Money equity)
    {
        ArgumentNullException.ThrowIfNull(equity);

        return Create(currency, Money.Zero(currency), equity, equity, Money.Zero(currency), Money.Zero(currency));
    }

    public static ExposureSnapshot Create(
        Currency currency,
        Money totalExposure,
        Money peakEquity,
        Money currentEquity,
        Money realisedLossToday,
        Money cycleCost,
        IReadOnlyDictionary<Capability, int>? actionsToday = null,
        IReadOnlyDictionary<string, Money>? exposureByInstrument = null,
        DateTime? lastRealisedLossAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(totalExposure);
        ArgumentNullException.ThrowIfNull(peakEquity);
        ArgumentNullException.ThrowIfNull(currentEquity);
        ArgumentNullException.ThrowIfNull(realisedLossToday);
        ArgumentNullException.ThrowIfNull(cycleCost);

        EnsureCurrency(currency, totalExposure, nameof(totalExposure));
        EnsureCurrency(currency, peakEquity, nameof(peakEquity));
        EnsureCurrency(currency, currentEquity, nameof(currentEquity));
        EnsureCurrency(currency, realisedLossToday, nameof(realisedLossToday));
        EnsureCurrency(currency, cycleCost, nameof(cycleCost));

        if (realisedLossToday.IsNegative)
        {
            throw new DomainValidationException(
                nameof(realisedLossToday),
                "A realised loss is recorded as a positive amount. A negative one would compare the " +
                "wrong way against a ceiling and never bind.");
        }

        if (lastRealisedLossAtUtc is { } lastLoss)
        {
            DateRange.EnsureUtc(lastLoss, nameof(lastRealisedLossAtUtc));
        }

        var byInstrument = new Dictionary<string, Money>(StringComparer.OrdinalIgnoreCase);

        if (exposureByInstrument is not null)
        {
            foreach (var (instrument, exposure) in exposureByInstrument)
            {
                EnsureCurrency(currency, exposure, nameof(exposureByInstrument));
                byInstrument[instrument] = exposure;
            }
        }

        return new ExposureSnapshot(
            currency,
            totalExposure,
            peakEquity,
            currentEquity,
            realisedLossToday,
            cycleCost,
            actionsToday is null
                ? []
                : new Dictionary<Capability, int>(actionsToday),
            byInstrument,
            lastRealisedLossAtUtc);
    }

    public override string ToString() =>
        $"exposure {TotalExposure}, equity {CurrentEquity} (peak {PeakEquity}), " +
        $"loss today {RealisedLossToday}, cycle cost {CycleCost}";

    private static void EnsureCurrency(Currency currency, Money amount, string parameterName)
    {
        if (amount.Currency != currency)
        {
            throw new DomainValidationException(
                parameterName,
                $"Every amount in a snapshot must be in {currency}; received {amount.Currency}. " +
                "A total across currencies is not a quantity of anything.");
        }
    }
}
