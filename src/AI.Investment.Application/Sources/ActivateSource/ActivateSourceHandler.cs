using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Sources.ActivateSource;

/// <summary>
/// Switches a registered source on.
/// </summary>
/// <remarks>
/// <para>
/// The consequential decision in the data plane. Registration records that a source has been
/// assessed; activation is what permits ingestion to draw from it, and from that moment its
/// content starts becoming things the platform believes. It is therefore a separate, deliberate,
/// audited act rather than a flag set during seeding.
/// </para>
/// <para>
/// The domain does the refusing. <see cref="DataSource.Activate"/> rejects a source whose terms
/// permit neither storage nor automated processing, so a licensing failure surfaces as a domain
/// rule violation rather than as something this handler has to remember to check - and it would
/// still refuse if some future caller bypassed this handler entirely.
/// </para>
/// </remarks>
public sealed class ActivateSourceHandler
{
    public const string ServiceId = "application.sources.activate-source";
    public const string ServiceVersion = "1.0";

    private static readonly ActionType ActivateActionType = ActionType.Create("source.activate");

    private readonly ISourceRegistry _registry;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IActionGateway _gateway;
    private readonly IClock _clock;

    public ActivateSourceHandler(
        ISourceRegistry registry,
        IUnitOfWork unitOfWork,
        IActionGateway gateway,
        IClock clock)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ActivateSourceResult> HandleAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        var source = await _registry.GetByIdAsync(sourceId, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            return new ActivateSourceResult(
                sourceId.Value,
                ActivateSourceStatus.NotFound,
                $"Source '{sourceId}' is not registered. A source must be assessed before it can " +
                "be used.");
        }

        if (source.IsActive)
        {
            return new ActivateSourceResult(
                sourceId.Value,
                ActivateSourceStatus.AlreadyActive,
                $"Source '{sourceId}' is already active.");
        }

        var now = _clock.UtcNow;

        var proposal = ActionProposal.Create(
            CorrelationId.New(),
            Capability.ReferenceDataManagement,
            ActivateActionType,
            ActionTarget.Create("DataSource", sourceId.Value),
            new ActivateSourceParameters(source),

            // Reversible: deactivating is one call away, and nothing already ingested is undone
            // by switching a source off.
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service(ServiceId, ServiceVersion),
            $"source.activate:{sourceId.Value}",
            now);

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async token =>
            {
                // Throws if the recorded terms permit neither storage nor automated processing.
                source.Activate(now);

                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

                return source;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            ActionOutcomeStatus.Executed => new ActivateSourceResult(
                sourceId.Value,
                ActivateSourceStatus.Activated,
                outcome.Reason),

            ActionOutcomeStatus.ApprovalRequired => new ActivateSourceResult(
                sourceId.Value,
                ActivateSourceStatus.ApprovalRequired,
                outcome.Reason),

            _ => new ActivateSourceResult(
                sourceId.Value,
                ActivateSourceStatus.Denied,
                outcome.Reason),
        };
    }
}

/// <summary>What became of an activation request.</summary>
public enum ActivateSourceStatus
{
    Activated = 0,
    AlreadyActive = 1,
    NotFound = 2,
    ApprovalRequired = 3,
    Denied = 4,
}

/// <param name="SourceId">Which source.</param>
/// <param name="Status">What happened.</param>
/// <param name="Reason">Why.</param>
public sealed record ActivateSourceResult(string SourceId, ActivateSourceStatus Status, string Reason);
