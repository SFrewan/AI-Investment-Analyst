using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Sources.RegisterKnownSources;

/// <summary>
/// Puts the source definitions this installation ships into the registry.
/// </summary>
/// <remarks>
/// <para>
/// Without this the registry starts empty, and every ingestion run refuses with
/// <c>ingestion.source-registered@1</c> - correctly, but uselessly. It exists so the platform can
/// seed itself from what its connectors already know, rather than an operator re-typing a
/// regulator's authority, licensing and cadence and getting one of them subtly wrong.
/// </para>
/// <para>
/// <strong>Registration goes through the seam, one proposal per source.</strong> Admitting a source
/// is the decision that governs everything the platform will later believe from it, so it is
/// audited like any other side effect and can be denied. One proposal each rather than one for the
/// batch, so a refusal of one source does not silently take the others with it - and so the audit
/// trail records which source was admitted, not that "seeding ran".
/// </para>
/// <para>
/// <strong>Existing sources are left exactly as they are.</strong> A source already in the registry
/// may have been re-licensed, deactivated or re-scored by an operator; overwriting it with the
/// shipped definition on every start-up would quietly undo that. Seeding fills gaps, it does not
/// reconcile.
/// </para>
/// <para>
/// Sources are registered <em>inactive</em>. Activation is a separate deliberate act - see
/// <see cref="ActivateSource.ActivateSourceHandler"/> - because shipping a connector is not the
/// same as deciding to use it.
/// </para>
/// </remarks>
public sealed class RegisterKnownSourcesHandler
{
    public const string ServiceId = "application.sources.register-known-sources";
    public const string ServiceVersion = "1.0";

    private static readonly ActionType RegisterActionType = ActionType.Create("source.register");

    private readonly IEnumerable<ISourceDefinition> _definitions;
    private readonly ISourceRegistry _registry;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IActionGateway _gateway;
    private readonly IClock _clock;

    public RegisterKnownSourcesHandler(
        IEnumerable<ISourceDefinition> definitions,
        ISourceRegistry registry,
        IUnitOfWork unitOfWork,
        IActionGateway gateway,
        IClock clock)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IReadOnlyList<SourceRegistrationResult>> HandleAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<SourceRegistrationResult>();

        foreach (var definition in _definitions)
        {
            results.Add(await RegisterAsync(definition, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<SourceRegistrationResult> RegisterAsync(
        ISourceDefinition definition,
        CancellationToken cancellationToken)
    {
        if (await _registry.ExistsAsync(definition.SourceId, cancellationToken).ConfigureAwait(false))
        {
            return new SourceRegistrationResult(
                definition.SourceId.Value,
                SourceRegistrationOutcome.AlreadyRegistered,
                "Already in the registry; left as it is, in case an operator has changed it.");
        }

        var now = _clock.UtcNow;
        var source = definition.Definition(now);

        var proposal = ActionProposal.Create(
            // Its own correlation: seeding runs outside any request, and borrowing a correlation
            // it does not belong to would make the audit trail harder to read, not easier.
            CorrelationId.New(),
            Capability.ReferenceDataManagement,
            RegisterActionType,
            ActionTarget.Create("DataSource", source.Id.Value),
            new RegisterSourceParameters(source),

            // Adding an inactive registry row spends nothing and can be undone. The consequential
            // decision is activation, not registration.
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service(ServiceId, ServiceVersion),

            // Keyed on the source, so repeated start-ups do not re-propose it.
            $"source.register:{source.Id.Value}",
            now);

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async token =>
            {
                _registry.Add(source);

                // Refused by the persistence guard unless the gateway opened an authorisation
                // window. A source registry is domain state, not seam bookkeeping.
                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

                return source;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            ActionOutcomeStatus.Executed => new SourceRegistrationResult(
                source.Id.Value,
                SourceRegistrationOutcome.Registered,
                outcome.Reason),

            ActionOutcomeStatus.DuplicateSuppressed => new SourceRegistrationResult(
                source.Id.Value,
                SourceRegistrationOutcome.AlreadyRegistered,
                outcome.Reason),

            // Denied, awaiting approval, and any future status: not registered. Fail closed,
            // including in a switch.
            _ => new SourceRegistrationResult(
                source.Id.Value,
                SourceRegistrationOutcome.Refused,
                outcome.Reason),
        };
    }
}
