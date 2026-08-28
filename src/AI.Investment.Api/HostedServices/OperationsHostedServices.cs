using AI.Investment.Api.Configuration;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Api.HostedServices;

/// <summary>Advances operating cycles on a timer.</summary>
/// <remarks>
/// <para>
/// The recurring caller the cycle state machine was built for. A cycle is a persisted state machine
/// rather than a long-running method precisely so that this service can be stopped, restarted,
/// scaled out or killed without losing work: it picks up whatever is runnable, moves it, and writes
/// the result down.
/// </para>
/// <para>
/// <strong>Off by default.</strong> This is the loop that proposes actions, and it does not begin
/// because a host started.
/// </para>
/// <para>
/// <strong>A failed pass does not stop the timer.</strong> Unattended operation that exited on the
/// first exception would be unattended in the least useful sense - nobody watching, and nothing
/// running either.
/// </para>
/// </remarks>
public sealed class OperatingCycleHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OperationsHostOptions> _options;
    private readonly ILogger<OperatingCycleHostedService> _logger;

    public OperatingCycleHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<OperationsHostOptions> options,
        ILogger<OperatingCycleHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.RunCycles)
        {
            OperationsLog.CyclesDisabled(_logger);

            return;
        }

        OperationsLog.CyclesEnabled(_logger, options.StartupDelay, options.CycleInterval, options.CycleBatchSize);

        try
        {
            await Task.Delay(options.StartupDelay, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(options.CycleInterval);

            do
            {
                await PassAsync(options.CycleBatchSize, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Every cycle's stage is persisted, so the next start continues rather
            // than restarting - and a lease that dies with this process expires on its own.
            OperationsLog.CyclesStopping(_logger);
        }
    }

    private async Task PassAsync(int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var provider = scope.ServiceProvider;
            var store = provider.GetRequiredService<ICycleStore>();
            var runner = provider.GetRequiredService<OperatingCycleRunner>();
            var clock = provider.GetRequiredService<IClock>();
            var worker = Environment.MachineName + ":" + Environment.ProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

            var runnable = await store
                .GetRunnableAsync(batchSize, clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            foreach (var cycle in runnable)
            {
                var result = await runner
                    .RunAsync(cycle.CycleId, worker, cancellationToken)
                    .ConfigureAwait(false);

                OperationsLog.CycleAdvanced(
                    _logger,
                    result.CycleId,
                    result.Status.ToString(),
                    result.Stage.ToString(),
                    result.Summary);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliberate: one bad pass must not end unattended operation.
        catch (Exception ex)
        {
            OperationsLog.CyclePassFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }
}

/// <summary>Delivers queued messages on a timer.</summary>
/// <remarks>
/// Separately switchable from the cycle runner, because an installation may reasonably want several
/// instances delivering messages and exactly one advancing cycles. Delivery is safe to run
/// everywhere: the lease and the row's concurrency token settle which instance takes a message, and
/// handlers are required to tolerate the repeat that an at-least-once queue can produce.
/// </remarks>
public sealed class OutboxDispatchHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OperationsHostOptions> _options;
    private readonly ILogger<OutboxDispatchHostedService> _logger;

    public OutboxDispatchHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<OperationsHostOptions> options,
        ILogger<OutboxDispatchHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.RunOutboxDispatcher)
        {
            OperationsLog.OutboxDisabled(_logger);

            return;
        }

        OperationsLog.OutboxEnabled(_logger, options.StartupDelay, options.OutboxInterval, options.OutboxBatchSize);

        try
        {
            await Task.Delay(options.StartupDelay, stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(options.OutboxInterval);

            do
            {
                await PassAsync(options.OutboxBatchSize, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            OperationsLog.OutboxStopping(_logger);
        }
    }

    private async Task PassAsync(int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
            var summary = await dispatcher.DispatchAsync(batchSize, cancellationToken).ConfigureAwait(false);

            if (!summary.WasIdle)
            {
                OperationsLog.OutboxPass(
                    _logger,
                    summary.Claimed,
                    summary.Dispatched,
                    summary.Failed,
                    summary.Abandoned,
                    summary.Unhandled);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliberate: the queue outlives one bad pass. Leases expire and the
                              // next pass re-reads whatever this one did not finish.
        catch (Exception ex)
        {
            OperationsLog.OutboxPassFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }
}

/// <summary>Source-generated log messages for the operations hosted services.</summary>
/// <remarks>
/// CA1848. One cached delegate per message, so a message whose level is disabled costs a level check
/// rather than an allocation and a template parse - which matters on a loop that runs every fifteen
/// seconds for weeks.
/// </remarks>
internal static partial class OperationsLog
{
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Operating cycles are not advanced on this instance. Cycles started by watches " +
                  "will wait until an instance runs them.")]
    internal static partial void CyclesDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Operating cycles enabled: first pass in {Delay}, then every {Interval}, " +
                  "{BatchSize} cycles per pass.")]
    internal static partial void CyclesEnabled(ILogger logger, TimeSpan delay, TimeSpan interval, int batchSize);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Operating cycle runner stopping with the host. Every cycle's stage is persisted.")]
    internal static partial void CyclesStopping(ILogger logger);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Information,
        Message = "Cycle {CycleId} is {Status} at {Stage}: {Summary}")]
    internal static partial void CycleAdvanced(
        ILogger logger,
        Guid cycleId,
        string status,
        string stage,
        string summary);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Error,
        Message = "An operating-cycle pass failed. The timer continues; leases expire and the next " +
                  "pass picks up whatever this one did not finish.")]
    internal static partial void CyclePassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2210,
        Level = LogLevel.Information,
        Message = "The outbox is not dispatched on this instance. Queued messages will wait.")]
    internal static partial void OutboxDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 2211,
        Level = LogLevel.Information,
        Message = "Outbox dispatch enabled: first pass in {Delay}, then every {Interval}, " +
                  "{BatchSize} messages per pass.")]
    internal static partial void OutboxEnabled(ILogger logger, TimeSpan delay, TimeSpan interval, int batchSize);

    [LoggerMessage(
        EventId = 2212,
        Level = LogLevel.Information,
        Message = "Outbox dispatcher stopping with the host. Leases expire and undelivered messages remain queued.")]
    internal static partial void OutboxStopping(ILogger logger);

    [LoggerMessage(
        EventId = 2213,
        Level = LogLevel.Information,
        Message = "Outbox pass: claimed {Claimed}, dispatched {Dispatched}, failed {Failed}, " +
                  "abandoned {Abandoned}, unhandled {Unhandled}.")]
    internal static partial void OutboxPass(
        ILogger logger,
        int claimed,
        int dispatched,
        int failed,
        int abandoned,
        int unhandled);

    [LoggerMessage(
        EventId = 2214,
        Level = LogLevel.Error,
        Message = "An outbox dispatch pass failed. The timer continues; nothing is lost, because a " +
                  "message stays queued until it is delivered or abandoned.")]
    internal static partial void OutboxPassFailed(ILogger logger, Exception exception);
}
