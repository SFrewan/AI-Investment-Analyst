using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Validation;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Autonomy;

/// <summary>The parameters of issuing a promotion warrant.</summary>
public sealed record PromotionWarrantParameters(
    Capability Capability,
    string? ActionType,
    string EnvironmentName,
    AutonomyMode ProposedMode,
    RiskTier MaxRiskTier,
    Money MaxExposure,
    TimeSpan ValidFor,
    string Justification) : IActionParameters
{
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"warrant {Capability}/{ActionType ?? "*"} @{EnvironmentName} <= {ProposedMode}, " +
            $"tier<={MaxRiskTier}, exposure<={MaxExposure}, for {ValidFor}");
}

/// <summary>What happened when promotion was asked about.</summary>
/// <param name="Assessment">What the evidence says. Always present; often a list of refusals.</param>
/// <param name="WarrantId">The warrant issued, when one was.</param>
/// <param name="Status">What the action seam answered.</param>
/// <param name="Reason">Why.</param>
public sealed record PromotionOutcome(
    PromotionAssessment Assessment,
    Guid? WarrantId,
    ActionOutcomeStatus Status,
    string Reason)
{
    public bool WarrantIssued => WarrantId is not null;
}

/// <summary>
/// Asks whether the measured evidence justifies unattended execution, and records the answer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Assessing is not promoting.</strong> <see cref="AssessAsync"/> runs the validation report
/// through <see cref="PromotionAssessment"/> and writes the result to the audit trail. It issues
/// nothing, changes nothing, and is safe to run on a schedule. Today it records
/// <em>promotion not justified</em>, with one line per unmet criterion, which is the state this
/// platform is actually in and the state it should be easy to read.
/// </para>
/// <para>
/// <strong>Issuing a warrant needs a person and the same evidence.</strong>
/// <see cref="IssueWarrantAsync"/> re-runs the assessment at the moment of issue rather than trusting
/// one handed to it, so a warrant cannot be issued from a favourable assessment computed earlier and
/// kept. It then goes through the action seam under
/// <see cref="Capability.AutonomyAdministration"/>, which refuses an AI proposer structurally - so no
/// agent, and no service, can obtain a warrant however it phrases the request.
/// </para>
/// <para>
/// There is no method here that promotes a capability. A warrant permits somebody to write a grant;
/// writing it is a separate act through <see cref="AutonomyAdministration"/>, with its own signature.
/// </para>
/// </remarks>
public sealed class PromotionService
{
    private readonly IActionGateway _gateway;
    private readonly IPromotionWarrantStore _warrants;
    private readonly ValidationService _validation;
    private readonly IValidationRequestFactory _validationRequests;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public PromotionService(
        IActionGateway gateway,
        IPromotionWarrantStore warrants,
        ValidationService validation,
        IValidationRequestFactory validationRequests,
        IUnitOfWork unitOfWork,
        IAuditSink audit,
        ICorrelationContext correlation,
        IClock clock)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _warrants = warrants ?? throw new ArgumentNullException(nameof(warrants));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _validationRequests = validationRequests ?? throw new ArgumentNullException(nameof(validationRequests));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The bar in force. Not configurable: see <see cref="PromotionCriteria"/>.</summary>
    public static PromotionCriteria Criteria => PromotionCriteria.Standard;

    /// <summary>
    /// Measures the evidence against the bar and records the answer. Promotes nothing.
    /// </summary>
    public async Task<PromotionAssessment> AssessAsync(
        Capability capability,
        AutonomyMode proposedMode,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var report = await _validation
            .RunAsync(_validationRequests.Create(), cancellationToken)
            .ConfigureAwait(false);

        var assessment = PromotionAssessment.Evaluate(capability, proposedMode, report, Criteria, now);

        await RecordAsync(assessment, "autonomy.promotion-assessment", cancellationToken)
            .ConfigureAwait(false);

        return assessment;
    }

    /// <summary>
    /// Issues a promotion warrant, on a named person's decision and on evidence assessed now.
    /// </summary>
    public async Task<PromotionOutcome> IssueWarrantAsync(
        PromotionWarrantParameters parameters,
        string issuedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuedBy);

        var now = _clock.UtcNow;

        // Re-assessed here rather than accepted from the caller. A warrant issued from an assessment
        // computed an hour ago is a warrant issued from evidence that may since have changed, and the
        // shape of that mistake is indistinguishable from the shape of doing it on purpose.
        var assessment = await AssessAsync(parameters.Capability, parameters.ProposedMode, cancellationToken)
            .ConfigureAwait(false);

        if (!assessment.IsJustified)
        {
            return new PromotionOutcome(
                assessment,
                null,
                ActionOutcomeStatus.Denied,
                "the measured evidence does not justify promotion: " + string.Join(" ", assessment.Reasons));
        }

        var proposal = ActionProposal.Create(
            _correlation.Current,
            Capability.AutonomyAdministration,
            ActionType.Create("autonomy.promotion-warrant"),
            ActionTarget.Create("Capability", parameters.Capability.ToString()),
            parameters,
            ActionEconomics.NoFinancialEffect(parameters.MaxExposure.Currency),
            ProposedBy.Human(issuedBy),
            idempotencyKey: "promotion-warrant:" + Guid.NewGuid().ToString("n"),
            now);

        PromotionWarrant? issued = null;

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async ct =>
            {
                issued = PromotionWarrant.Issue(
                    assessment,
                    parameters.ActionType,
                    parameters.EnvironmentName,
                    parameters.MaxRiskTier,
                    parameters.MaxExposure,
                    issuedBy,
                    parameters.Justification,
                    now,
                    parameters.ValidFor);

                await _warrants.AddAsync(issued, ct).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return issued.PromotionWarrantId;
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.WasExecuted && issued is not null)
        {
            await RecordWarrantAsync(AuditEventType.PromotionWarrantIssued, issued, issuedBy, cancellationToken)
                .ConfigureAwait(false);
        }

        return new PromotionOutcome(
            assessment,
            outcome.WasExecuted ? issued?.PromotionWarrantId : null,
            outcome.Status,
            outcome.Decision.Reason);
    }

    /// <summary>Withdraws a warrant. Grants under it stop being covered on the next check.</summary>
    public async Task<PromotionOutcome> RevokeWarrantAsync(
        Guid promotionWarrantId,
        string reason,
        string revokedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedBy);

        var now = _clock.UtcNow;
        var warrant = await _warrants.FindAsync(promotionWarrantId, cancellationToken).ConfigureAwait(false);

        var assessment = PromotionAssessment.Evaluate(
            warrant?.Capability ?? Capability.SimulatedExecution,
            warrant?.MaxMode ?? AutonomyMode.PrepareForApproval,
            null,
            Criteria,
            now);

        if (warrant is null)
        {
            return new PromotionOutcome(
                assessment, null, ActionOutcomeStatus.Denied,
                $"no promotion warrant {promotionWarrantId:d} exists.");
        }

        var proposal = ActionProposal.Create(
            _correlation.Current,
            Capability.AutonomyAdministration,
            ActionType.Create("autonomy.promotion-warrant-revoke"),
            ActionTarget.Create("PromotionWarrant", promotionWarrantId.ToString("d", CultureInfo.InvariantCulture)),
            new AutonomyChangeParameters(promotionWarrantId, "revoke-warrant", reason),
            ActionEconomics.NoFinancialEffect(warrant.MaxExposure.Currency),
            ProposedBy.Human(revokedBy),
            idempotencyKey: $"promotion-warrant-revoke:{promotionWarrantId:n}:{now:O}",
            now);

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async ct =>
            {
                warrant.Revoke(reason, now);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.WasExecuted)
        {
            await RecordWarrantAsync(AuditEventType.PromotionWarrantRevoked, warrant, revokedBy, cancellationToken)
                .ConfigureAwait(false);
        }

        return new PromotionOutcome(assessment, promotionWarrantId, outcome.Status, outcome.Decision.Reason);
    }

    private Task RecordAsync(PromotionAssessment assessment, string actor, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["promotion.capability"] = assessment.Capability.ToString(),
            ["promotion.proposedMode"] = assessment.ProposedMode.ToString(),
            ["promotion.justified"] = assessment.IsJustified
                ? "true"
                : "false",
            ["promotion.refusals"] = assessment.Refusals.Count.ToString(CultureInfo.InvariantCulture),
            ["promotion.validationRunId"] = assessment.ValidationRunId?.ToString("d", CultureInfo.InvariantCulture)
                ?? "none",
        };

        for (var index = 0; index < assessment.Reasons.Count; index++)
        {
            details[string.Create(CultureInfo.InvariantCulture, $"promotion.reason.{index}")] =
                assessment.Reasons[index];
        }

        return _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                AuditEventType.PromotionAssessed,
                actor,
                assessment.ToString(),
                _clock.UtcNow,
                cycleId: null,
                assessment.Capability,
                details),
            cancellationToken);
    }

    private Task RecordWarrantAsync(
        AuditEventType eventType,
        PromotionWarrant warrant,
        string actor,
        CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                eventType,
                actor,
                $"{eventType}: {warrant}",
                _clock.UtcNow,
                cycleId: null,
                warrant.Capability,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["warrant.id"] = warrant.PromotionWarrantId.ToString("d", CultureInfo.InvariantCulture),
                    ["warrant.maxMode"] = warrant.MaxMode.ToString(),
                    ["warrant.environment"] = warrant.EnvironmentName,
                    ["warrant.validationRunId"] = warrant.ValidationRunId.ToString("d", CultureInfo.InvariantCulture),
                    ["warrant.benchmark"] = warrant.BenchmarkFingerprint,
                    ["warrant.expiresAtUtc"] = warrant.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
                }),
            cancellationToken);
}
