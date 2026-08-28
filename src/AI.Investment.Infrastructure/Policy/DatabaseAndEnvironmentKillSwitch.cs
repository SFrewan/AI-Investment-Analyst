using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Policy;

/// <summary>
/// The kill switch, read from an environment variable and a database flag. Either engages it.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms because they fail differently and are reachable by different people. The variable
/// stops a process that is already misbehaving without needing the database to answer; the flag
/// survives a restart and can be set from anywhere with database access. Requiring both to agree
/// would mean either one failing leaves the switch off, which is the wrong direction for a control
/// whose entire job is to stop things.
/// </para>
/// <para>
/// <strong>Any failure to read returns <see cref="KillSwitchState.Unknown"/></strong>, which the
/// policy engine already treats exactly like engaged. A switch nobody can read is a switch of
/// unknown state, and a switch of unknown state is on.
/// </para>
/// </remarks>
public sealed partial class DatabaseAndEnvironmentKillSwitch : IKillSwitch
{
    private readonly AppDbContext _dbContext;
    private readonly IOptionsMonitor<SafetyOptions> _options;
    private readonly ILogger<DatabaseAndEnvironmentKillSwitch> _logger;

    public DatabaseAndEnvironmentKillSwitch(
        AppDbContext dbContext,
        IOptionsMonitor<SafetyOptions> options,
        ILogger<DatabaseAndEnvironmentKillSwitch> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<KillSwitchState> ReadAsync(
        Capability? capability = null,
        CancellationToken cancellationToken = default)
    {
        if (ReadEnvironment() is { } fromEnvironment && fromEnvironment != KillSwitchState.Disengaged)
        {
            return fromEnvironment;
        }

        try
        {
            var engaged = await _dbContext.KillSwitchFlags
                .AsNoTracking()
                .AnyAsync(
                    flag => flag.Engaged && (flag.Capability == null || flag.Capability == capability),
                    cancellationToken)
                .ConfigureAwait(false);

            return engaged ? KillSwitchState.Engaged : KillSwitchState.Disengaged;
        }
#pragma warning disable CA1031 // Deliberate: ANY failure to read the switch must read as engaged.
                              // Propagating would let a caller catch it and carry on, which is the
                              // one behaviour a kill switch must make impossible.
        catch (Exception exception)
        {
            LogKillSwitchUnreadable(exception);

            return KillSwitchState.Unknown;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The environment half. Absent means "this mechanism says nothing", not "disengaged".
    /// </summary>
    /// <remarks>
    /// A value that is present but not understood returns <see cref="KillSwitchState.Unknown"/>
    /// rather than being ignored. Somebody set it for a reason, and the safe reading of a variable
    /// nobody can parse is that they meant to stop something.
    /// </remarks>
    private KillSwitchState? ReadEnvironment()
    {
        string? raw;

        try
        {
            raw = Environment.GetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable);
        }
#pragma warning disable CA1031 // Same reasoning: an unreadable environment is an unknown switch.
        catch (Exception exception)
        {
            LogKillSwitchUnreadable(exception);

            return KillSwitchState.Unknown;
        }
#pragma warning restore CA1031

        if (string.IsNullOrWhiteSpace(raw))
        {
            return _options.CurrentValue.KillSwitchEngaged == true
                ? KillSwitchState.Engaged
                : null;
        }

        var trimmed = raw.Trim();

        if (bool.TryParse(trimmed, out var parsed))
        {
            return parsed ? KillSwitchState.Engaged : KillSwitchState.Disengaged;
        }

        if (string.Equals(trimmed, "1", StringComparison.Ordinal) ||
            string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "engaged", StringComparison.OrdinalIgnoreCase))
        {
            return KillSwitchState.Engaged;
        }

        if (string.Equals(trimmed, "0", StringComparison.Ordinal) ||
            string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "disengaged", StringComparison.OrdinalIgnoreCase))
        {
            return KillSwitchState.Disengaged;
        }

        LogUnrecognisedKillSwitchValue(trimmed);

        return KillSwitchState.Unknown;
    }

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Error,
        Message = "The kill switch could not be read. Treating it as engaged.")]
    private partial void LogKillSwitchUnreadable(Exception exception);

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Error,
        Message = "The kill switch variable holds an unrecognised value '{Value}'. Treating it as engaged.")]
    private partial void LogUnrecognisedKillSwitchValue(string value);
}
