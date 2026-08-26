using AI.Investment.Api.Configuration;
using AI.Investment.Application.Retention;
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
            _logger.LogInformation(
                "The retention sweep is disabled on this instance. Archived payloads past their " +
                "licensed limit will not be deleted until an instance runs it.");

            return;
        }

        _logger.LogInformation(
            "Retention sweep enabled: first sweep in {Delay}, then every {Interval}, " +
            "{BatchSize} payloads per sweep.",
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
            _logger.LogInformation("Retention sweep stopping with the host.");
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

            _logger.LogInformation("Retention sweep finished: {Summary}", summary);

            if (summary.Outstanding > 0)
            {
                // Payloads a licence says must go which are still on disk. Expected on an
                // installation that requires human approval for irreversible actions - which is
                // the default - and a real compliance exposure on one that does not. Either way it
                // is a number somebody should be looking at rather than one buried in a debug log.
                _logger.LogWarning(
                    "{Outstanding} payloads require deletion and remain archived " +
                    "({Refused} refused by policy, {Failed} failed). Retention deletion is " +
                    "irreversible and requires approval unless this installation has granted " +
                    "automatic execution for the data-retention capability.",
                    summary.Outstanding,
                    summary.DeletionsRefused,
                    summary.Failed);
            }

            if (summary.HasMore)
            {
                // The batch size stopped it, not the end of the archive. Said out loud so that a
                // permanently-behind sweep is visible rather than looking like a clean one.
                _logger.LogInformation(
                    "The sweep stopped at its batch size of {BatchSize} with more of the archive " +
                    "unexamined. The next sweep continues.",
                    batchSize);
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
            _logger.LogError(
                ex,
                "A retention sweep failed. The timer continues; the next sweep will re-examine " +
                "everything this one did not reach.");
        }
#pragma warning restore CA1031
    }
}
