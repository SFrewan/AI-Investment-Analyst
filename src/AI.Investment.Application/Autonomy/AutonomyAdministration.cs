using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Autonomy;

/// <summary>The parameters of a change to what the platform may do unattended.</summary>
/// <remarks>
/// <see cref="Describe"/> feeds the action fingerprint, so everything that changes what the grant
/// permits is in it. A change of ceiling that produced the same fingerprint would let an approval of
/// the smaller grant authorise the larger one.
/// </remarks>
public sealed record AutonomyGrantParameters(
    Capability Capability,
    string? ActionType,
    string EnvironmentName,
    AutonomyMode Mode,
    RiskTier MaxRiskTier,
    Money MaxExposure,
    string LimitSetName,
    TimeSpan ValidFor,
    Guid? PromotionWarrantId = null) : IActionParameters
{
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"grant {Capability}/{ActionType ?? "*"} @{EnvironmentName} = {Mode}, " +
            $"tier<={MaxRiskTier}, exposure<={MaxExposure}, limits='{LimitSetName}', for {ValidFor}, " +
            $"warrant={PromotionWarrantId?.ToString("d", CultureInfo.InvariantCulture) ?? "none"}");
}

/// <summary>The parameters of withdrawing or lowering a grant.</summary>
public sealed record AutonomyChangeParameters(Guid AutonomyGrantId, string Change, string Reason)
    : IActionParameters
{
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"{Change} grant {AutonomyGrantId:d}: {Reason}");
}

/// <summary>
/// Issuing, withdrawing and automatically lowering autonomy grants - through the seam, like
/// everything else.
/// </summary>
/// <remarks>
/// <para>
/// Every method here proposes an action under <see cref="Capability.AutonomyAdministration"/>, which
/// the policy engine refuses to an AI proposer structurally and before any configurable rule is
/// consulted. That is the mechanism, not a convention: an agent cannot call these methods to any
/// effect, because the gate refuses the proposal they produce regardless of what the configuration
/// says.
/// </para>
/// <para>
/// <strong>Only demotion is automatic.</strong> <see cref="DemoteAsync"/> is proposed by a service,
/// because a circuit breaker that needs a human to trip it is not a circuit breaker.
/// <see cref="GrantAsync"/> names a person, because raising what the platform may do without anybody
/// watching is exactly the decision that must have somebody's name on it.
/// </para>
/// <para>
/// <strong>This is the only production path that writes a grant, and Phase 8 puts the promotion gate
/// on it.</strong> A request for a mode above <see cref="AutonomyGrant.HighestAttendedMode"/> is
/// refused unless it names a <see cref="PromotionWarrant"/> that is active and covers every dimension
/// of the grant. The warrant itself can only be built from measured evidence that justified it, so an
/// unmet Phase 7 promotion condition cannot arrive here as an L4 grant - and an architecture test
/// asserts that no other production type calls the grant factory at all, so this door is the only
/// one.
/// </para>
/// </remarks>
public sealed class AutonomyAdministration
{
    private readonly IActionGateway _gateway;
    private readonly IAutonomyGrantStore _grants;
    private readonly IPromotionWarrantStore _warrants;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public AutonomyAdministration(
        IActionGateway gateway,
        IAutonomyGrantStore grants,
        IPromotionWarrantStore warrants,
        IUnitOfWork unitOfWork,
        IAuditSink audit,
        ICorrelationContext correlation,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _warrants = warrants ?? throw new ArgumentNullException(nameof(warrants));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Issues a grant on behalf of a named person.</summary>
    public async Task<AutonomyOutcome> GrantAsync(
        AutonomyGrantParameters parameters,
        string grantedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantedBy);

        var now = _clock.UtcNow;

        // The promotion gate, and it is checked before a proposal is even built. A grant above the
        // attended ceiling is a grant to act while nobody is watching; the only thing that permits
        // one is a warrant, and the only thing that produces a warrant is measured evidence that
        // justified it. Refusing here rather than inside the dispatch means the refusal is recorded
        // as a denial with a reason rather than as an exception somebody has to interpret.
        PromotionWarrant? warrant = null;

        if (parameters.Mode > AutonomyGrant.HighestAttendedMode)
        {
            if (parameters.PromotionWarrantId is null)
            {
                return Outcome(
                    ActionOutcomeStatus.Denied,
                    $"a grant of {parameters.Mode} permits acting while nobody is watching, and " +
                    "requires a promotion warrant. None was named.",
                    null);
            }

            warrant = await _warrants
                .FindAsync(parameters.PromotionWarrantId.Value, cancellationToken)
                .ConfigureAwait(false);

            if (warrant is null)
            {
                return Outcome(
                    ActionOutcomeStatus.Denied,
                    $"no promotion warrant {parameters.PromotionWarrantId:d} exists.",
                    null);
            }

            var refusal = warrant.WhyItDoesNotCover(
                parameters.Capability,
                parameters.ActionType,
                parameters.EnvironmentName,
                parameters.Mode,
                parameters.MaxRiskTier,
                parameters.MaxExposure,
                now);

            if (refusal is not null)
            {
                return Outcome(ActionOutcomeStatus.Denied, refusal, null);
            }
        }

        var proposal = ActionProposal.Create(
            _correlation.Current,
            Capability.AutonomyAdministration,
            ActionType.Create("autonomy.grant"),
            ActionTarget.Create("Capability", parameters.Capability.ToString()),
            parameters,
            ActionEconomics.NoFinancialEffect(parameters.MaxExposure.Currency),
            ProposedBy.Human(grantedBy),
            idempotencyKey: "autonomy-grant:" + Guid.NewGuid().ToString("n"),
            now);

        AutonomyGrant? issued = null;

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async ct =>
            {
                issued = warrant is null
                    ? AutonomyGrant.Issue(
                        parameters.Capability,
                        parameters.ActionType,
                        parameters.EnvironmentName,
                        parameters.Mode,
                        parameters.MaxRiskTier,
                        parameters.MaxExposure,
                        parameters.LimitSetName,
                        grantedBy,
                        now,
                        parameters.ValidFor)
                    : AutonomyGrant.IssueBounded(
                        warrant,
                        parameters.ActionType,
                        parameters.EnvironmentName,
                        parameters.Mode,
                        parameters.MaxRiskTier,
                        parameters.MaxExposure,
                        parameters.LimitSetName,
                        grantedBy,
                        now,
                        parameters.ValidFor);

                await _grants.AddAsync(issued, ct).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return issued.AutonomyGrantId;
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.WasExecuted && issued is not null)
        {
            await RecordAsync(AuditEventType.AutonomyGranted, issued, grantedBy, cancellationToken)
                .ConfigureAwait(false);
        }

        return Outcome(outcome.Status, outcome.Decision.Reason, issued?.AutonomyGrantId);
    }

    /// <summary>Withdraws a grant on behalf of a named person.</summary>
    public Task<AutonomyOutcome> RevokeAsync(
        Guid autonomyGrantId,
        string reason,
        string revokedBy,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            autonomyGrantId,
            "revoke",
            reason,
            ProposedBy.Human(revokedBy),
            AuditEventType.AutonomyRevoked,
            (grant, now) =>
            {
                grant.Revoke(reason, now);

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Lowers a grant one level because a measured threshold was crossed. Deterministic and automatic.
    /// </summary>
    public Task<AutonomyOutcome> DemoteAsync(
        Guid autonomyGrantId,
        string reason,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            autonomyGrantId,
            "demote",
            reason,
            ProposedBy.Service("autonomy.circuit-breaker", "1.0"),
            AuditEventType.AutonomyDemoted,
            (grant, now) => grant.Demote(reason, now),
            cancellationToken);

    private async Task<AutonomyOutcome> ChangeAsync(
        Guid autonomyGrantId,
        string change,
        string reason,
        ProposedBy proposedBy,
        AuditEventType eventType,
        Func<AutonomyGrant, DateTime, bool> apply,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = _clock.UtcNow;
        var grant = await _grants.FindAsync(autonomyGrantId, cancellationToken).ConfigureAwait(false);

        if (grant is null)
        {
            return Outcome(ActionOutcomeStatus.Denied, $"no grant {autonomyGrantId:d} exists.", null);
        }

        var proposal = ActionProposal.Create(
            _correlation.Current,
            Capability.AutonomyAdministration,
            ActionType.Create("autonomy." + change),
            ActionTarget.Create("AutonomyGrant", autonomyGrantId.ToString("d", CultureInfo.InvariantCulture)),
            new AutonomyChangeParameters(autonomyGrantId, change, reason),
            ActionEconomics.NoFinancialEffect(grant.MaxExposure.Currency),
            proposedBy,
            idempotencyKey: $"autonomy-{change}:{autonomyGrantId:n}:{now:O}",
            now);

        var applied = false;

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async ct =>
            {
                applied = apply(grant, now);

                if (applied)
                {
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                return applied;
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.WasExecuted && applied)
        {
            await RecordAsync(eventType, grant, proposedBy.Id, cancellationToken).ConfigureAwait(false);
        }

        return Outcome(outcome.Status, outcome.Decision.Reason, autonomyGrantId);
    }

    private Task RecordAsync(
        AuditEventType eventType,
        AutonomyGrant grant,
        string actor,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                eventType,
                actor,
                $"{eventType} for {grant.Capability}: {grant}",
                _clock.UtcNow,
                cycleId: null,
                grant.Capability,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant.id"] = grant.AutonomyGrantId.ToString("d", CultureInfo.InvariantCulture),
                    ["grant.mode"] = grant.EffectiveMode.ToString(),
                    ["grant.grantedMode"] = grant.GrantedMode.ToString(),
                    ["grant.environment"] = grant.EnvironmentName,
                    ["grant.expiresAtUtc"] = grant.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    ["grant.demotions"] = grant.DemotionCount.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken);

    private static AutonomyOutcome Outcome(ActionOutcomeStatus status, string reason, Guid? grantId) =>
        new(status, reason, grantId);
}

/// <summary>What happened to a grant request.</summary>
public sealed record AutonomyOutcome(ActionOutcomeStatus Status, string Reason, Guid? AutonomyGrantId)
{
    public bool Succeeded => Status == ActionOutcomeStatus.Executed;
}
