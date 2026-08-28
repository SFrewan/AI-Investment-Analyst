using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// The single artefact that permits a grant of unattended execution, and the only one.
/// </summary>
/// <remarks>
/// <para>
/// A warrant is what stands between "the measurements look good" and "the platform may now act while
/// nobody is watching". It exists because those are different claims and the second one needs a
/// person's name on it.
/// </para>
/// <para>
/// <strong>It cannot be built from an unjustified assessment.</strong> <see cref="Issue"/> takes a
/// <see cref="PromotionAssessment"/> and refuses one that is not justified, so there is no path from
/// an empty or unfavourable validation report to a warrant - not through configuration, not through
/// a flag, not through a caller who decided the refusals were minor. The refusal is a structural one
/// in the constructor of the only type a grant will accept.
/// </para>
/// <para>
/// <strong>It is per capability, per environment, per action type, and it expires.</strong> There is
/// no warrant that covers "the platform"; autonomy is granted to one named thing at a time, and
/// widening it means going back for another warrant with another name on it. A warrant that never
/// expired would be a decision nobody revisits, which is the thing the whole mechanism exists to
/// prevent.
/// </para>
/// <para>
/// <strong>Nothing here promotes.</strong> A warrant permits a human to issue a grant; it does not
/// issue one. No service, agent or scheduled job can produce a warrant, because producing one
/// requires a justified assessment and a named person, and the assessment is a pure function of a
/// report nobody in the platform can write.
/// </para>
/// </remarks>
public sealed class PromotionWarrant
{
    public const string UnjustifiedRule = "PromotionWarrant.EvidenceDoesNotJustifyPromotion";

    public const string BeyondAssessmentRule = "PromotionWarrant.BeyondWhatWasAssessed";

    /// <summary>The longest a warrant may run before the evidence has to be re-argued.</summary>
    public const int MaxValidityDays = 30;

    public const int MaxIssuedByLength = 120;

    public const int MaxJustificationLength = 1000;

    public const int MaxFingerprintLength = 64;

    private PromotionWarrant(
        Guid promotionWarrantId,
        Capability capability,
        string? actionType,
        string environmentName,
        AutonomyMode maxMode,
        RiskTier maxRiskTier,
        Money maxExposure,
        Guid validationRunId,
        string benchmarkFingerprint,
        string issuedBy,
        string justification,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        PromotionWarrantId = promotionWarrantId;
        Capability = capability;
        ActionType = actionType;
        EnvironmentName = environmentName;
        MaxMode = maxMode;
        MaxRiskTier = maxRiskTier;
        MaxExposure = maxExposure;
        ValidationRunId = validationRunId;
        BenchmarkFingerprint = benchmarkFingerprint;
        IssuedBy = issuedBy;
        Justification = justification;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private PromotionWarrant()
    {
        EnvironmentName = string.Empty;
        MaxExposure = null!;
        BenchmarkFingerprint = string.Empty;
        IssuedBy = string.Empty;
        Justification = string.Empty;
    }

    public Guid PromotionWarrantId { get; private set; }

    public Capability Capability { get; private set; }

    /// <summary>The one action type this warrant covers, or null for every type in the capability.</summary>
    public string? ActionType { get; private set; }

    public string EnvironmentName { get; private set; }

    /// <summary>The highest mode a grant under this warrant may name.</summary>
    public AutonomyMode MaxMode { get; private set; }

    public RiskTier MaxRiskTier { get; private set; }

    public Money MaxExposure { get; private set; }

    /// <summary>The validation run this warrant was argued from.</summary>
    public Guid ValidationRunId { get; private set; }

    /// <summary>The benchmark that run used, so the argument can be checked against the same one.</summary>
    public string BenchmarkFingerprint { get; private set; }

    /// <summary>The person who issued it. A person, never a service and never an agent.</summary>
    public string IssuedBy { get; private set; }

    /// <summary>What they said when they issued it, in their words.</summary>
    public string Justification { get; private set; }

    public DateTime IssuedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public string? RevocationReason { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool HasExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool IsActive(DateTime nowUtc) => !IsRevoked && !HasExpired(nowUtc);

    /// <summary>
    /// Issues a warrant on the strength of a justified assessment and a named person's decision.
    /// </summary>
    public static PromotionWarrant Issue(
        PromotionAssessment assessment,
        string? actionType,
        string environmentName,
        RiskTier maxRiskTier,
        Money maxExposure,
        string issuedBy,
        string justification,
        DateTime nowUtc,
        TimeSpan validFor)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(maxExposure);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        // The gate, and the first thing checked. Everything below is detail about a warrant that
        // must not exist unless this passes.
        if (!assessment.IsJustified)
        {
            throw new DomainRuleViolationException(
                UnjustifiedRule,
                $"the measured evidence does not justify promoting {assessment.Capability} to " +
                $"{assessment.ProposedMode}. " + string.Join(" ", assessment.Reasons));
        }

        if (assessment.ValidationRunId is null || string.IsNullOrWhiteSpace(assessment.BenchmarkFingerprint))
        {
            throw new DomainRuleViolationException(
                UnjustifiedRule,
                "a justified assessment must name the validation run and benchmark it was argued " +
                "from. Without them the warrant cannot be checked against the evidence afterwards.");
        }

        if (assessment.ProposedMode > PromotionAssessment.MaximumPromotableMode)
        {
            throw new DomainRuleViolationException(
                BeyondAssessmentRule,
                $"no warrant may name {assessment.ProposedMode}.");
        }

        if (maxExposure.IsNegative)
        {
            throw new DomainValidationException(
                nameof(maxExposure),
                "An exposure ceiling may not be negative.");
        }

        // The canonical scope for bounded autonomy is the lowest-risk, reversible action classes, and
        // this is where that sentence binds. A warrant is the widest any grant under it may be, so
        // capping the warrant caps everything downstream without the rule having to be re-checked at
        // every point where a grant is written.
        var classRefusal = BoundedExecutionRule.Admits(
            assessment.Capability, BoundedExecutionRule.RequiredReversibility, maxRiskTier,
            assessment.ProposedMode);

        if (classRefusal != BoundedExecutionRefusal.None)
        {
            throw new DomainRuleViolationException(
                BeyondAssessmentRule,
                $"a warrant for {assessment.Capability} at {assessment.ProposedMode} up to risk tier " +
                $"{maxRiskTier} is outside the class that may run unattended: " +
                BoundedExecutionRule.Explain(classRefusal));
        }

        if (validFor <= TimeSpan.Zero || validFor > TimeSpan.FromDays(MaxValidityDays))
        {
            throw new DomainValidationException(
                nameof(validFor),
                $"A warrant must expire, and may not run longer than {MaxValidityDays} days. The " +
                "evidence behind it ages faster than that.");
        }

        return new PromotionWarrant(
            Guid.NewGuid(),
            assessment.Capability,
            NormaliseActionType(actionType),
            Text(environmentName, nameof(environmentName), 60,
                "A warrant applies to one named environment. Evidence gathered where the venue is " +
                "simulated says nothing about where it is not."),
            assessment.ProposedMode,
            maxRiskTier,
            maxExposure,
            assessment.ValidationRunId.Value,
            Text(assessment.BenchmarkFingerprint, nameof(assessment), MaxFingerprintLength,
                "A warrant records the benchmark its evidence was measured against."),
            Text(issuedBy, nameof(issuedBy), MaxIssuedByLength,
                "A warrant must name the person who issued it. Unattended execution is exactly the " +
                "decision that has to have somebody's name on it."),
            Text(justification, nameof(justification), MaxJustificationLength,
                "A warrant must record why, in the words of the person issuing it. A warrant whose " +
                "only justification is that the numbers passed is a warrant nobody thought about."),
            nowUtc,
            nowUtc.Add(validFor));
    }

    /// <summary>Withdraws the warrant. Grants issued under it stop resolving on the next pass.</summary>
    public void Revoke(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = nowUtc;
        RevocationReason = Text(reason, nameof(reason), AutonomyGrant.MaxReasonLength,
            "A revocation must state a reason.");
    }

    /// <summary>
    /// Whether this warrant covers a proposed grant. Every dimension must be within it.
    /// </summary>
    /// <remarks>
    /// Deliberately a total function returning a reason rather than a bool, so the refusal that
    /// stops a grant can be recorded in the words of the dimension that failed.
    /// </remarks>
    public string? WhyItDoesNotCover(
        Capability capability,
        string? actionType,
        string environmentName,
        AutonomyMode mode,
        RiskTier maxRiskTier,
        Money maxExposure,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(maxExposure);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!IsActive(nowUtc))
        {
            return IsRevoked
                ? $"warrant {PromotionWarrantId:d} was revoked: {RevocationReason}"
                : $"warrant {PromotionWarrantId:d} expired at {ExpiresAtUtc:O}";
        }

        if (capability != Capability)
        {
            return $"warrant {PromotionWarrantId:d} covers {Capability}, not {capability}. Autonomy " +
                "is granted to one named capability at a time.";
        }

        if (!string.Equals(environmentName?.Trim(), EnvironmentName, StringComparison.OrdinalIgnoreCase))
        {
            return $"warrant {PromotionWarrantId:d} covers environment '{EnvironmentName}', not " +
                $"'{environmentName}'.";
        }

        // A warrant for one action type does not cover a grant for every type in the capability; a
        // warrant for every type covers any one of them.
        if (ActionType is not null &&
            !string.Equals(NormaliseActionType(actionType), ActionType, StringComparison.Ordinal))
        {
            return $"warrant {PromotionWarrantId:d} covers action type '{ActionType}', not " +
                $"'{actionType ?? "*"}'.";
        }

        if (mode > MaxMode)
        {
            return $"warrant {PromotionWarrantId:d} permits at most {MaxMode}, and the grant names {mode}.";
        }

        if (maxRiskTier > MaxRiskTier)
        {
            return $"warrant {PromotionWarrantId:d} permits at most risk tier {MaxRiskTier}, and the " +
                $"grant names {maxRiskTier}.";
        }

        if (maxExposure.Currency != MaxExposure.Currency)
        {
            return $"warrant {PromotionWarrantId:d} is denominated in {MaxExposure.Currency} and the " +
                $"grant in {maxExposure.Currency}. Two ceilings that cannot be compared have not been.";
        }

        if (maxExposure.IsGreaterThan(MaxExposure))
        {
            return $"warrant {PromotionWarrantId:d} permits at most {MaxExposure}, and the grant " +
                $"names {maxExposure}.";
        }

        return null;
    }

    public override string ToString() =>
        $"warrant {PromotionWarrantId} {Capability}{(ActionType is null ? string.Empty : "/" + ActionType)}" +
        $" @{EnvironmentName} <= {MaxMode}, expires {ExpiresAtUtc:O}";

    private static string? NormaliseActionType(string? actionType) =>
        string.IsNullOrWhiteSpace(actionType) ? null : actionType.Trim();

    private static string Text(string? value, string parameterName, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, message);
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"'{parameterName}' may not exceed {maxLength} characters.");
        }

        return trimmed;
    }
}
