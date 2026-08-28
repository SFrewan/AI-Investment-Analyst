using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Operations;

/// <summary>Builds the concurrency and firing ceilings from configuration. Fails closed.</summary>
/// <remarks>
/// Every failure path returns <see cref="AdmissionLimits.FailClosed"/>, which admits nothing. Not a
/// permissive default and not a rethrown exception a caller might catch and continue past: a
/// platform that cannot determine how much work it is already doing must not start more. The
/// consequence of getting this wrong is a market-wide event producing thousands of simultaneous
/// cycles, which is expensive before anybody notices.
/// </remarks>
public sealed partial class ConfiguredAdmissionLimitProvider : IAdmissionLimitProvider
{
    private readonly IOptionsMonitor<OperationsOptions> _options;
    private readonly ILogger<ConfiguredAdmissionLimitProvider> _logger;

    public ConfiguredAdmissionLimitProvider(
        IOptionsMonitor<OperationsOptions> options,
        ILogger<ConfiguredAdmissionLimitProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<AdmissionLimits> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var options = _options.CurrentValue;

            return Task.FromResult(AdmissionLimits.Create(
                options.MaxConcurrentCycles,
                options.MaxConcurrentCyclesPerCapability,
                options.MaxQueuedTriggers,
                options.MaxFiringsPerWatchPerWindow,
                options.FiringWindow));
        }
#pragma warning disable CA1031 // Deliberate: ANY failure to read the ceilings must admit nothing.
        catch (Exception ex)
        {
            LogLimitsUnavailable(ex);

            return Task.FromResult(AdmissionLimits.FailClosed);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Critical,
        Message = "Operations ceilings could not be read. Failing closed: no further work is admitted.")]
    private partial void LogLimitsUnavailable(Exception exception);
}

/// <summary>Builds a cycle's budget from configuration, per template.</summary>
/// <remarks>
/// An unreadable configuration produces the most restrictive budget this class can name rather than
/// an exception, for the same reason the admission provider fails closed: the alternative is a cycle
/// that runs with no ceiling because the ceiling could not be loaded.
/// </remarks>
public sealed partial class ConfiguredCycleBudgetProvider : ICycleBudgetProvider
{
    /// <summary>What a cycle gets when nothing can be read. Enough to fail, not enough to run away.</summary>
    private static readonly CycleBudget Minimal = CycleBudget.Create(
        TimeSpan.FromMinutes(1),
        Money.Zero(Currency.Usd),
        maxProviderCalls: 0,
        maxActions: 0);

    private readonly IOptionsMonitor<OperationsOptions> _options;
    private readonly ILogger<ConfiguredCycleBudgetProvider> _logger;

    public ConfiguredCycleBudgetProvider(
        IOptionsMonitor<OperationsOptions> options,
        ILogger<ConfiguredCycleBudgetProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<CycleBudget> GetAsync(string templateName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var options = _options.CurrentValue;
            var currency = Currency.Create(options.BudgetCurrency);

            var over = options.Budgets.FirstOrDefault(candidate =>
                string.Equals(candidate.Template, templateName, StringComparison.Ordinal));

            return Task.FromResult(CycleBudget.Create(
                over?.MaxWallClock ?? options.CycleMaxWallClock,
                Money.Create(over?.MaxModelSpend ?? options.CycleMaxModelSpend, currency),
                over?.MaxProviderCalls ?? options.CycleMaxProviderCalls,
                over?.MaxActions ?? options.CycleMaxActions));
        }
#pragma warning disable CA1031 // Deliberate: an unreadable budget must restrict, never release.
        catch (Exception ex)
        {
            LogBudgetUnavailable(ex, templateName);

            return Task.FromResult(Minimal);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Critical,
        Message = "The budget for cycle template {Template} could not be read. Using the minimal budget, which stops the cycle almost immediately.")]
    private partial void LogBudgetUnavailable(Exception exception, string template);
}
