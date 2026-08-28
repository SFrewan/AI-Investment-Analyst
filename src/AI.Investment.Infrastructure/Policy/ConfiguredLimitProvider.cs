using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Policy;

/// <summary>Builds the limit set from configuration. Fails closed.</summary>
/// <remarks>
/// Every failure path returns <see cref="LimitSet.FailClosed"/>, which refuses everything - never
/// <see cref="LimitSet.Empty"/>, which permits everything. The two differ by one word in the code
/// and by the entire safety posture of the system, so the distinction is stated here rather than
/// left to whoever edits this next.
/// </remarks>
public sealed partial class ConfiguredLimitProvider : ILimitProvider
{
    private readonly IOptionsMonitor<LimitOptions> _options;
    private readonly ILogger<ConfiguredLimitProvider> _logger;

    public ConfiguredLimitProvider(
        IOptionsMonitor<LimitOptions> options,
        ILogger<ConfiguredLimitProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<LimitSet> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var options = _options.CurrentValue;
            var currency = Currency.Create(options.CurrencyCode);
            var limits = new List<Limit>();

            Add(limits, LimitKind.MaxPositionSize, options.MaxPositionSize, currency);
            Add(limits, LimitKind.MaxTotalExposure, options.MaxTotalExposure, currency);
            Add(limits, LimitKind.MaxDailyLoss, options.MaxDailyLoss, currency);
            Add(limits, LimitKind.MaxDrawdown, options.MaxDrawdown, currency);
            Add(limits, LimitKind.MaxCostPerCycle, options.MaxCostPerCycle, currency);

            if (options.MaxActionsPerCapabilityPerDay is { } actions)
            {
                limits.Add(Limit.OfCount(LimitKind.MaxActionsPerCapabilityPerDay, actions));
            }

            if (options.MaxConcentration is { } concentration)
            {
                limits.Add(Limit.OfRatio(LimitKind.MaxConcentration, Percentage.FromRatio(concentration)));
            }

            if (options.CooldownAfterLossMinutes is { } cooldown)
            {
                limits.Add(Limit.OfDuration(LimitKind.CooldownAfterLoss, TimeSpan.FromMinutes(cooldown)));
            }

            return Task.FromResult(LimitSet.Create(limits, options.AllowedInstruments));
        }
#pragma warning disable CA1031 // Deliberate: ANY failure to read the limits must refuse, not
                              // propagate. A caller that catches and continues would be acting
                              // with no ceilings at all.
        catch (Exception exception)
        {
            LogLimitsUnavailable(exception);

            return Task.FromResult(LimitSet.FailClosed);
        }
#pragma warning restore CA1031
    }

    private static void Add(List<Limit> limits, LimitKind kind, decimal? amount, Currency currency)
    {
        if (amount is { } value)
        {
            limits.Add(Limit.OfMoney(kind, Money.Create(value, currency)));
        }
    }

    [LoggerMessage(
        EventId = 5110,
        Level = LogLevel.Error,
        Message = "The configured limits could not be read. Refusing every action until they can be.")]
    private partial void LogLimitsUnavailable(Exception exception);
}
