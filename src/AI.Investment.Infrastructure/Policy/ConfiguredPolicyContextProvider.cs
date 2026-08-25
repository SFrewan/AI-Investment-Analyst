using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Policy;

/// <summary>
/// Builds the policy context from configuration and the environment. Fails closed.
/// </summary>
/// <remarks>
/// <para>
/// Every failure path in this class returns <see cref="PolicyContext.FailClosed"/>, which denies
/// everything. Not a permissive default, not a rethrown exception a caller might catch and
/// continue past. A system that cannot determine whether it is allowed to act must not act.
/// </para>
/// <para>
/// Configuration is the Phase 1 source of policy, deliberately not the long-term one. Autonomy
/// grants become database records with expiry, measured quality metrics and automatic demotion.
/// Because the policy engine consumes a <see cref="PolicyContext"/> rather than reading
/// configuration itself, that change replaces this one class and nothing else.
/// </para>
/// </remarks>
public sealed partial class ConfiguredPolicyContextProvider : IPolicyContextProvider
{
    private readonly IOptionsMonitor<SafetyOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ConfiguredPolicyContextProvider> _logger;

    public ConfiguredPolicyContextProvider(
        IOptionsMonitor<SafetyOptions> options,
        IHostEnvironment environment,
        ILogger<ConfiguredPolicyContextProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var environmentName = _environment.EnvironmentName;

        SafetyOptions options;

        try
        {
            options = _options.CurrentValue;
        }
#pragma warning disable CA1031 // Deliberate: ANY failure to read policy must deny, not propagate.
                              // A caller that catches an exception and continues is precisely
                              // the failure mode fail-closed exists to prevent.
        catch (Exception ex)
        {
            LogPolicyUnavailable(ex);
            return Task.FromResult(PolicyContext.FailClosed(environmentName));
        }
#pragma warning restore CA1031

        var killSwitch = ResolveKillSwitch(options);

        var capabilities = options.Capabilities
            .Select(c => CapabilityPolicy.Create(
                c.Capability,
                c.Enabled,
                c.MaxAutoExecuteRiskTier,
                c.AllowIrreversibleAutoExecute,
                c.AllowAiProposers))
            .ToList();

        if (capabilities.Count == 0)
        {
            // Not an error - it is a completely locked-down system - but it is almost always a
            // misconfiguration, and silently denying everything without saying so wastes an
            // operator's afternoon.
            LogNoCapabilitiesConfigured(environmentName);
        }

        return Task.FromResult(PolicyContext.Create(environmentName, killSwitch, capabilities));
    }

    /// <summary>
    /// Resolves the kill switch: environment variable first, then configuration.
    /// </summary>
    /// <remarks>
    /// The environment variable wins so that an operator can stop a running system without a
    /// deployment. Any value other than "0" or "false" engages it - a typo must stop the system
    /// rather than start it. An absent setting is <see cref="KillSwitchState.Unknown"/>, which
    /// the policy engine treats exactly like engaged.
    /// </remarks>
    private KillSwitchState ResolveKillSwitch(SafetyOptions options)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(
            SafetyOptions.KillSwitchEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var disengaged =
                string.Equals(fromEnvironment.Trim(), "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fromEnvironment.Trim(), "false", StringComparison.OrdinalIgnoreCase);

            if (!disengaged)
            {
                LogKillSwitchEngagedByEnvironment(SafetyOptions.KillSwitchEnvironmentVariable);
                return KillSwitchState.Engaged;
            }
        }

        return options.KillSwitchEngaged switch
        {
            true => KillSwitchState.Engaged,
            false => KillSwitchState.Disengaged,

            // Absent. Unknown denies - a missing setting must never read as permission.
            _ => KillSwitchState.Unknown,
        };
    }

    [LoggerMessage(
        EventId = 6000,
        Level = LogLevel.Critical,
        Message = "Safety policy could not be read. Failing closed: every action will be denied.")]
    private partial void LogPolicyUnavailable(Exception exception);

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Warning,
        Message = "No capability policies are configured for environment {EnvironmentName}. Every action will be denied.")]
    private partial void LogNoCapabilitiesConfigured(string environmentName);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Critical,
        Message = "The kill switch is engaged by environment variable {VariableName}. No action will execute.")]
    private partial void LogKillSwitchEngagedByEnvironment(string variableName);
}
