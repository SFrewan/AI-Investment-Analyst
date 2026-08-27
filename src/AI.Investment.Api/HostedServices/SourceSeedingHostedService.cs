using AI.Investment.Api.Configuration;
using AI.Investment.Application.Sources.RegisterKnownSources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AI.Investment.Api.HostedServices;

/// <summary>
/// Puts the source definitions this build ships with into the registry, once, at start-up.
/// </summary>
/// <remarks>
/// <para>
/// Closes the gap that made the whole data plane inert: until something called
/// <see cref="RegisterKnownSourcesHandler"/> the registry started empty, and every ingestion run
/// refused with <c>ingestion.source-registered@1</c> - correct, and useless.
/// </para>
/// <para>
/// <strong>Sources are registered inactive.</strong> Shipping a connector is not deciding to use
/// it; activation is a separate, deliberate act, because from that moment the source's content
/// becomes things the platform believes. Seeding fills gaps and never reconciles - an entry an
/// operator has re-licensed or deactivated is left exactly as it is.
/// </para>
/// <para>
/// <strong>A seeding failure must not stop the host.</strong> The API's other work - serving
/// reference data, reporting health, answering what it already knows - does not depend on the
/// registry being complete, and refusing to start would turn a database hiccup into an outage.
/// The failure is logged at error level and the instance comes up with an incomplete registry,
/// which the freshness report then shows as sources that have never been ingested.
/// </para>
/// </remarks>
public sealed class SourceSeedingHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DataPlaneOptions> _options;
    private readonly ILogger<SourceSeedingHostedService> _logger;

    public SourceSeedingHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<DataPlaneOptions> options,
        ILogger<SourceSeedingHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.SeedSourcesOnStartup)
        {
            SeedingLog.Disabled(_logger);

            return;
        }

        try
        {
            // The handler is scoped - it uses the DbContext and the action gateway - so it cannot
            // be injected into a singleton hosted service directly.
            using var scope = _scopeFactory.CreateScope();

            var handler = scope.ServiceProvider.GetRequiredService<RegisterKnownSourcesHandler>();
            var results = await handler.HandleAsync(stoppingToken).ConfigureAwait(false);

            var registered = results.Count(r => r.Outcome == SourceRegistrationOutcome.Registered);
            var present = results.Count(r => r.Outcome == SourceRegistrationOutcome.AlreadyRegistered);
            var refused = results.Count(r => r.Outcome == SourceRegistrationOutcome.Refused);

            SeedingLog.Complete(_logger, registered, present, refused);

            // Policy declined to register something this build shipped. Not an error, but not
            // silent either: it means a source an operator may be expecting is absent, and the
            // symptom - every run for it refusing - would otherwise look like a provider problem.
            foreach (var result in results.Where(r => r.Outcome == SourceRegistrationOutcome.Refused))
            {
                SeedingLog.SourceRefused(_logger, result.SourceId, result.Reason);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down before seeding finished. Not a failure.
        }
#pragma warning disable CA1031 // Deliberate: an incomplete registry is a degraded instance, not a
                              // reason to refuse to serve anything at all.
        catch (Exception ex)
        {
            SeedingLog.SeedingFailed(_logger, ex);
        }
#pragma warning restore CA1031
    }
}

/// <summary>Source-generated log messages for <see cref="SourceSeedingHostedService"/>.</summary>
/// <remarks>
/// See <see cref="SweepLog"/> for why these are static methods taking an explicit
/// <see cref="ILogger"/> rather than instance methods on the service.
/// </remarks>
internal static partial class SeedingLog
{
    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Source seeding is disabled. The registry will contain only what an operator " +
                  "has registered, and ingestion will refuse for anything else.")]
    internal static partial void Disabled(ILogger logger);

    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Source seeding complete: {Registered} registered, {AlreadyPresent} already " +
                  "present, {Refused} refused.")]
    internal static partial void Complete(
        ILogger logger,
        int registered,
        int alreadyPresent,
        int refused);

    /// <summary>A source this build ships was not registered.</summary>
    /// <remarks>
    /// Named individually rather than counted, because the symptom an operator will actually meet
    /// is every run for that source refusing - which looks like a provider problem until this line
    /// says otherwise.
    /// </remarks>
    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Warning,
        Message = "Source '{SourceId}' was not registered: {Reason} Ingestion for it will refuse " +
                  "until it is registered.")]
    internal static partial void SourceRefused(ILogger logger, string sourceId, string reason);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Error,
        Message = "Source seeding failed. The instance is starting with an incomplete registry; " +
                  "ingestion will refuse for any source that is missing.")]
    internal static partial void SeedingFailed(ILogger logger, Exception exception);
}
