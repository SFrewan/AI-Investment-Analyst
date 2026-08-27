using AI.Investment.Api.Configuration;
using AI.Investment.Application.Retention;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Api.HostedServices;

/// <summary>
/// Runs the retention sweep on a timer.
/// </summary>
/// <remarks>
/// <para>
/// The recurring caller <see cref="IRetentionSweep"/> was built for. Until this existed, retention
/// was a rule that could be evaluated and never was: the enforcer decided about a payload only when
/// something asked, and nothing did.
/// </para>
/// <para>
/// <strong>Off by default, and single-instance by assumption.</strong> This is the only activity in
/// the platform that destroys evidence, so it does not begin because a host happened to start.
/// There is no distributed lock: two instances sweeping the same archive cannot double-delete - the
/// seam deduplicates on the payload's content hash - but they would burn approval slots and audit
/// rows discovering that. Enable it on one.
/// </para>
/// <para>
/// <strong>A sweep that finds more work does not immediately run again.</strong> It waits for the
/// next interval like any other. A sweep loop that chased its own backlog would turn a large
/// archive into a continuous stream of deletion proposals, which is exactly the shape of thing an
/// operator should be able to watch rather than discover.
/// </para>
/// <para>
/// <strong>A failed sweep does not stop the timer.</strong> Retention is an obligation that
/// outlives one bad night, and a background service that exited on the first exception would leave
/// it unmet silently until somebody noticed the process was quiet.
/// </para>
/// </remarks>
public sealed class RetentionSweepHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DataPlaneOptions> _options;
    private readonly ILogger<RetentionSweepHostedService> _logger;

    public RetentionSweepHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<DataPlaneOptions> options,
        ILogger<RetentionSweepHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.RunRetentionSweep)
        {
            SweepLog.Disabled(_logger);

            return;
        }

        SweepLog.Enabled(
            _logger,
            options.RetentionSweepDelay,
            options.RetentionSweepInterval,
            options.RetentionSweepBatchSize);

        try
        {
            await Task.Delay(options.RetentionSweepDelay, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(options.RetentionSweepInterval);

            do
            {
                await SweepAsync(options.RetentionSweepBatchSize, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Not a failure, and the next start continues where this left off -
            // the archive is the state, so nothing is lost by stopping mid-sweep.
            SweepLog.Stopping(_logger);
        }
    }

    private async Task SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            // Scoped: the sweep reaches the registry, the reference index and the marker store,
            // all of which share the request-scoped DbContext.
            using var scope = _scopeFactory.CreateScope();

            var sweep = scope.ServiceProvider.GetRequiredService<IRetentionSweep>();
            var summary = await sweep.SweepAsync(batchSize, cancellationToken).ConfigureAwait(false);

            SweepLog.Finished(
                _logger,
                summary.Examined,
                summary.Retained,
                summary.Deleted,
                summary.DeletionsRefused,
                summary.Failed,
                summary.Reached);

            if (summary.Outstanding > 0)
            {
                // Payloads a licence says must go which are still on disk. Expected on an
                // installation that requires human approval for irreversible actions - which is
                // the default - and a real compliance exposure on one that does not. Either way it
                // is a number somebody should be looking at rather than one buried in a debug log.
                SweepLog.Outstanding(
                    _logger,
                    summary.Outstanding,
                    summary.DeletionsRefused,
                    summary.Failed);
            }

            if (summary.HasMore)
            {
                // The batch size stopped it, not the end of the archive. Said out loud so that a
                // permanently-behind sweep is visible rather than looking like a clean one.
                SweepLog.StoppedAtBatchSize(_logger, batchSize);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliberate: retention is an obligation that outlives one bad
                              // night. Exiting the loop would leave it unmet in silence.
        catch (Exception ex)
        {
            SweepLog.SweepFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }
}

/// <summary>Source-generated log messages for <see cref="RetentionSweepHostedService"/>.</summary>
/// <remarks>
/// <para>
/// CA1848. The generator emits one cached <c>LoggerMessage</c> delegate per message, so a message
/// whose level is disabled costs a level check rather than an allocation and a template parse. On
/// a background sweep that is mostly noise-suppression, but the rule is enforced repository-wide
/// and satisfying it honestly is cheaper than arguing with it once per call site.
/// </para>
/// <para>
/// Written as static methods taking an explicit <see cref="ILogger"/> - the form the generator has
/// supported since .NET 6 - rather than as instance methods on the service. Instance logging
/// methods work only when the generator can find an <c>ILogger</c> field, which is a newer and
/// more fragile contract to depend on.
/// </para>
/// <para>
/// <see cref="Finished"/> takes the summary's counts individually rather than the record itself.
/// A structured sink can then filter on <c>Failed</c> or <c>Deleted</c> directly, which is the
/// whole point of structured logging; passing the record would have produced one opaque string.
/// </para>
/// </remarks>
internal static partial class SweepLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "The retention sweep is disabled on this instance. Archived payloads past their " +
                  "licensed limit will not be deleted until an instance runs it.")]
    internal static partial void Disabled(ILogger logger);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Retention sweep enabled: first sweep in {Delay}, then every {Interval}, " +
                  "{BatchSize} payloads per sweep.")]
    internal static partial void Enabled(
        ILogger logger,
        TimeSpan delay,
        TimeSpan interval,
        int batchSize);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Retention sweep stopping with the host.")]
    internal static partial void Stopping(ILogger logger);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Information,
        Message = "Retention sweep finished: examined {Examined}, retained {Retained}, " +
                  "deleted {Deleted}, refused {Refused}, failed {Failed}, complete {Complete}.")]
    internal static partial void Finished(
        ILogger logger,
        int examined,
        int retained,
        int deleted,
        int refused,
        int failed,
        bool complete);

    /// <summary>Payloads a licence says must go which are still on disk.</summary>
    /// <remarks>
    /// A warning rather than information, deliberately. Expected on an installation that requires
    /// human approval for irreversible actions - which is the default - and a real compliance
    /// exposure on one that does not. Either way it is a number somebody should be looking at.
    /// </remarks>
    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Warning,
        Message = "{Outstanding} payloads require deletion and remain archived ({Refused} refused " +
                  "by policy, {Failed} failed). Retention deletion is irreversible and requires " +
                  "approval unless this installation has granted automatic execution for the " +
                  "data-retention capability.")]
    internal static partial void Outstanding(
        ILogger logger,
        int outstanding,
        int refused,
        int failed);

    [LoggerMessage(
        EventId = 2105,
        Level = LogLevel.Information,
        Message = "The sweep stopped at its batch size of {BatchSize} with more of the archive " +
                  "unexamined. The next sweep continues.")]
    internal static partial void StoppedAtBatchSize(ILogger logger, int batchSize);

    [LoggerMessage(
        EventId = 2106,
        Level = LogLevel.Error,
        Message = "A retention sweep failed. The timer continues; the next sweep will re-examine " +
                  "everything this one did not reach.")]
    internal static partial void SweepFailed(ILogger logger, Exception exception);
}
