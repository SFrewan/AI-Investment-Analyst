using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Autonomy;

/// <summary>The parameters of authorising a live venue. Two names, and a long justification.</summary>
public sealed record LiveVenueAuthorizationParameters(
    string VenueId,
    string EnvironmentName,
    Guid PromotionWarrantId,
    string CounterSignedBy,
    Money ExposureCeiling,
    TimeSpan ValidFor,
    string Justification) : IActionParameters
{
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"authorise live venue '{VenueId}' @{EnvironmentName} under warrant " +
            $"{PromotionWarrantId:d}, counter-signed by {CounterSignedBy}, ceiling {ExposureCeiling}, " +
            $"for {ValidFor}");
}

/// <summary>What happened when a live venue was asked about.</summary>
public sealed record LiveVenueOutcome(
    LiveVenueDecision Decision,
    Guid? AuthorizationId,
    ActionOutcomeStatus Status,
    string Reason);

/// <summary>
/// The formal gate on activating a venue that moves real money.
/// </summary>
/// <remarks>
/// <para>
/// The roadmap calls the live-venue decision "a formal, separate decision", and this is what that
/// means in code: not a switch, but a path with a shape. Two different named people, a written
/// justification, a promotion warrant underneath it, a stated ceiling on real money, an expiry
/// measured in days, and an audit record for every step including every refusal.
/// </para>
/// <para>
/// <strong>Nothing in this phase can complete that path</strong>, and the reason is not a flag - it
/// is that no promotion warrant can exist, because no assessment is justified, because no measured
/// evidence exists. <see cref="EvaluateAsync"/> therefore answers "not authorised" for every venue,
/// which is the correct answer and the one the system should give by default for as long as that
/// remains true.
/// </para>
/// <para>
/// <strong>The activation itself is deliberately absent.</strong> There is no method here that
/// registers a venue, opens a connection or hands over a credential. A gate that also performed the
/// thing it gates would be one refactor away from performing it for the wrong reason; what this class
/// produces is a decision, and something else - which does not exist yet - would have to act on it.
/// </para>
/// </remarks>
public sealed class LiveVenueService
{
    private readonly IActionGateway _gateway;
    private readonly ILiveVenueAuthorizationStore _authorizations;
    private readonly IPromotionWarrantStore _warrants;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public LiveVenueService(
        IActionGateway gateway,
        ILiveVenueAuthorizationStore authorizations,
        IPromotionWarrantStore warrants,
        IUnitOfWork unitOfWork,
        IAuditSink audit,
        ICorrelationContext correlation,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _warrants = warrants ?? throw new ArgumentNullException(nameof(warrants));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Whether a venue may be activated. Read-only, and expected to refuse.
    /// </summary>
    /// <param name="fromConfiguration">
    /// True when the caller is acting on a configuration value. Always refused, and the check is
    /// first: an installation that has somehow acquired an authorisation still cannot activate a
    /// venue by writing <c>true</c> somewhere.
    /// </param>
    public async Task<LiveVenueDecision> EvaluateAsync(
        string venueId,
        string environmentName,
        bool fromConfiguration = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        var now = _clock.UtcNow;

        var authorization = await _authorizations
            .FindForAsync(venueId, environmentName, cancellationToken)
            .ConfigureAwait(false);

        var warrant = authorization is null
            ? null
            : await _warrants.FindAsync(authorization.PromotionWarrantId, cancellationToken).ConfigureAwait(false);

        var decision = LiveVenueGate.Evaluate(
            new LiveVenueRequest(venueId, environmentName, authorization, warrant, fromConfiguration),
            now);

        await RecordAsync(decision, venueId, environmentName, "execution.live-venue-gate", cancellationToken)
            .ConfigureAwait(false);

        return decision;
    }

    /// <summary>
    /// Records a live-venue authorisation, on two named people and a warrant.
    /// </summary>
    /// <remarks>
    /// Goes through the action seam under <see cref="Capability.AutonomyAdministration"/>, so an AI
    /// proposer is refused structurally and before any configurable rule is consulted. In this phase
    /// it cannot succeed, because <see cref="LiveVenueAuthorization.Create"/> requires an active
    /// promotion warrant and none can exist.
    /// </remarks>
    public async Task<LiveVenueOutcome> AuthoriseAsync(
        LiveVenueAuthorizationParameters parameters,
        string authorisedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorisedBy);

        var now = _clock.UtcNow;

        var warrant = await _warrants
            .FindAsync(parameters.PromotionWarrantId, cancellationToken)
            .ConfigureAwait(false);

        if (warrant is null || !warrant.IsActive(now))
        {
            var refused = new LiveVenueDecision(
                LiveVenueRefusal.WarrantNoLongerValid,
                $"promotion warrant {parameters.PromotionWarrantId:d} is missing, expired or revoked, " +
                "so nothing may be authorised on the strength of it.");

            await RecordAsync(refused, parameters.VenueId, parameters.EnvironmentName, authorisedBy, cancellationToken)
                .ConfigureAwait(false);

            return new LiveVenueOutcome(refused, null, ActionOutcomeStatus.Denied, refused.Explanation);
        }

        var proposal = ActionProposal.Create(
            _correlation.Current,
            Capability.AutonomyAdministration,
            ActionType.Create("execution.authorise-live-venue"),
            ActionTarget.Create("Venue", parameters.VenueId),
            parameters,
            ActionEconomics.NoFinancialEffect(parameters.ExposureCeiling.Currency),
            ProposedBy.Human(authorisedBy),
            idempotencyKey: "live-venue-authorisation:" + Guid.NewGuid().ToString("n"),
            now);

        LiveVenueAuthorization? created = null;

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async ct =>
            {
                created = LiveVenueAuthorization.Create(
                    parameters.VenueId,
                    parameters.EnvironmentName,
                    warrant,
                    authorisedBy,
                    parameters.CounterSignedBy,
                    parameters.Justification,
                    parameters.ExposureCeiling,
                    now,
                    parameters.ValidFor);

                await _authorizations.AddAsync(created, ct).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return created.LiveVenueAuthorizationId;
            },
            cancellationToken).ConfigureAwait(false);

        var decision = created is null
            ? new LiveVenueDecision(LiveVenueRefusal.NotAuthorised, outcome.Decision.Reason)
            : LiveVenueGate.Evaluate(
                new LiveVenueRequest(
                    parameters.VenueId, parameters.EnvironmentName, created, warrant, false),
                now);

        await RecordAsync(decision, parameters.VenueId, parameters.EnvironmentName, authorisedBy, cancellationToken)
            .ConfigureAwait(false);

        return new LiveVenueOutcome(
            decision,
            outcome.WasExecuted ? created?.LiveVenueAuthorizationId : null,
            outcome.Status,
            outcome.Decision.Reason);
    }

    private Task RecordAsync(
        LiveVenueDecision decision,
        string venueId,
        string environmentName,
        string actor,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                decision.MayActivate
                    ? AuditEventType.LiveVenueAuthorised
                    : AuditEventType.LiveVenueRefused,
                actor,
                $"live venue '{venueId}' @{environmentName}: {decision.Explanation}",
                _clock.UtcNow,
                cycleId: null,
                Capability.FinancialExecution,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["venue.id"] = venueId,
                    ["venue.environment"] = environmentName,
                    ["venue.refusal"] = decision.Refusal.ToString(),
                }),
            cancellationToken);
}
