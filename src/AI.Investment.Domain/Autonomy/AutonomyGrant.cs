using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// A named human's decision that one capability may operate unattended, in one environment,
/// up to a stated risk tier and exposure, until a stated date.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No agent reads, writes, proposes or influences one of these.</strong> A grant is not in
/// any agent's input schema and not in any agent's output schema, and the prohibition is structural
/// rather than instructional: creating or changing a grant is an action under
/// <see cref="Capability.AutonomyAdministration"/>, which the policy engine refuses to an AI
/// proposer unconditionally and before any configurable rule is consulted. An architecture test
/// asserts that no type in the AI namespaces can even reference this one.
/// </para>
/// <para>
/// <strong>Every grant expires.</strong> There is no factory that produces one without an end date,
/// and <see cref="MaxValidity"/> caps how far out that date may be. Autonomy that never expires is
/// autonomy nobody re-examines, and the whole argument for granting it is that somebody is looking
/// at the measurements.
/// </para>
/// <para>
/// <strong>Demotion is a mutation, promotion is not.</strong> <see cref="Demote"/> lowers the
/// effective mode one level and records why; there is no <c>Promote</c>. Raising autonomy requires a
/// new grant from a human, which is a row an operator can see and a decision somebody signed. A
/// circuit breaker that can also close itself is not a circuit breaker.
/// </para>
/// <para>
/// <strong>Unattended execution needs a warrant.</strong> Phase 8 adds
/// <see cref="IssueBounded"/>, which takes a <see cref="PromotionWarrant"/> and refuses any grant
/// the warrant does not cover on every dimension. A warrant can only be built from measured evidence
/// that justified it, so an unmet promotion condition cannot become unattended execution: there is
/// no argument list that produces a bounded grant without a warrant object.
/// </para>
/// <para>
/// <see cref="Issue"/> is left able to write any mode, because Phase 6's simulated unattended
/// operation is written in terms of it and rewriting a verified phase to add a gate it predates
/// would be the wrong kind of change. The gate is on the production path instead:
/// <c>AutonomyAdministration.GrantAsync</c> refuses a mode above
/// <see cref="HighestAttendedMode"/> without a covering warrant, and an architecture test asserts
/// that no other production type calls <see cref="Issue"/> at all - so the one door is the one that
/// is guarded.
/// </para>
/// </remarks>
public sealed class AutonomyGrant
{
    /// <summary>The longest a single grant may run before a human has to look again.</summary>
    public const int MaxValidityDays = 90;

    /// <summary>Longest accepted identifier for the person who granted it.</summary>
    public const int MaxGrantedByLength = 120;

    /// <summary>Longest accepted free text on a grant or its revocation.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Longest accepted limit-set name.</summary>
    public const int MaxLimitSetLength = 60;

    private AutonomyGrant(
        Guid autonomyGrantId,
        Capability capability,
        string? actionType,
        string environmentName,
        AutonomyMode grantedMode,
        RiskTier maxRiskTier,
        Money maxExposure,
        string limitSetName,
        string grantedBy,
        DateTime grantedAtUtc,
        DateTime expiresAtUtc)
    {
        AutonomyGrantId = autonomyGrantId;
        Capability = capability;
        ActionType = actionType;
        EnvironmentName = environmentName;
        GrantedMode = grantedMode;
        DemotedMode = null;
        MaxRiskTier = maxRiskTier;
        MaxExposure = maxExposure;
        LimitSetName = limitSetName;
        GrantedBy = grantedBy;
        GrantedAtUtc = grantedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private AutonomyGrant()
    {
        EnvironmentName = string.Empty;
        MaxExposure = null!;
        LimitSetName = string.Empty;
        GrantedBy = string.Empty;
    }

    public Guid AutonomyGrantId { get; private set; }

    public Capability Capability { get; private set; }

    /// <summary>
    /// The one action type this grant covers, or null when it covers every type in the capability.
    /// </summary>
    /// <remarks>
    /// Stored as a string rather than <c>ActionType</c> so that a grant can be written for a type
    /// this build does not know about - a grant is configuration a human writes, and refusing to
    /// store one because the deployed binary has not heard of the type yet would make grants
    /// undeployable ahead of the code they govern.
    /// </remarks>
    public string? ActionType { get; private set; }

    /// <summary>
    /// The environment this grant applies to. Part of the key, never an ambient assumption: a grant
    /// in Development, where the venue is simulated, carries no weight in Production.
    /// </summary>
    public string EnvironmentName { get; private set; }

    /// <summary>What a human granted.</summary>
    public AutonomyMode GrantedMode { get; private set; }

    /// <summary>What automatic demotion has since reduced it to, when that has happened.</summary>
    public AutonomyMode? DemotedMode { get; private set; }

    /// <summary>The highest computed risk tier this grant covers. Above it, the action escalates.</summary>
    public RiskTier MaxRiskTier { get; private set; }

    /// <summary>The most this grant covers in one action. Above it, the action escalates.</summary>
    public Money MaxExposure { get; private set; }

    /// <summary>The named limit set an unattended execution must also satisfy.</summary>
    public string LimitSetName { get; private set; }

    /// <summary>The person who granted it. A person, never a service and never an agent.</summary>
    public string GrantedBy { get; private set; }

    public DateTime GrantedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public string? RevocationReason { get; private set; }

    public DateTime? DemotedAtUtc { get; private set; }

    public string? DemotionReason { get; private set; }

    public int DemotionCount { get; private set; }

    /// <summary>
    /// The promotion warrant that permitted this grant, when it named a mode above
    /// <see cref="AutonomyMode.PrepareForApproval"/>. Null for every attended grant.
    /// </summary>
    /// <remarks>
    /// Kept on the grant rather than inferred, so that the automatic demotion path can ask whether
    /// the evidence behind a running grant is still good without having to guess which warrant it
    /// came from.
    /// </remarks>
    public Guid? PromotionWarrantId { get; private set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool HasExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>The mode in force now: the granted one, unless demotion has lowered it.</summary>
    public AutonomyMode EffectiveMode => DemotedMode ?? GrantedMode;

    /// <summary>True when this grant can contribute anything at all to a resolution.</summary>
    public bool IsActive(DateTime nowUtc) => !IsRevoked && !HasExpired(nowUtc);

    /// <summary>The rule that refused a grant the warrant it names does not cover.</summary>
    public const string BeyondWarrantRule = "AutonomyGrant.BeyondItsWarrant";

    /// <summary>
    /// The highest mode that may be granted without a promotion warrant. Above it, somebody is no
    /// longer looking, and the evidence for that has to have been argued.
    /// </summary>
    public static AutonomyMode HighestAttendedMode => AutonomyMode.PrepareForApproval;

    /// <summary>
    /// Issues a grant. The caller is expected to have routed this through the action seam under
    /// <see cref="Capability.AutonomyAdministration"/>, which refuses an AI proposer structurally.
    /// </summary>
    /// <remarks>
    /// This factory does not itself require a promotion warrant, because Phase 6's simulated
    /// unattended operation predates the warrant and is written in terms of it. What requires a
    /// warrant is the production path: <c>AutonomyAdministration.GrantAsync</c> refuses a mode above
    /// <see cref="HighestAttendedMode"/> unless a warrant covering it is supplied, and an
    /// architecture test asserts that no other production type calls this method at all.
    /// </remarks>
    public static AutonomyGrant Issue(
        Capability capability,
        string? actionType,
        string environmentName,
        AutonomyMode mode,
        RiskTier maxRiskTier,
        Money maxExposure,
        string limitSetName,
        string grantedBy,
        DateTime nowUtc,
        TimeSpan validFor)
    {
        ArgumentNullException.ThrowIfNull(maxExposure);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enum.IsDefined(capability))
        {
            throw new DomainValidationException(nameof(capability), $"Unrecognised capability '{capability}'.");
        }

        if (!Enum.IsDefined(maxRiskTier))
        {
            throw new DomainValidationException(nameof(maxRiskTier), $"Unrecognised risk tier '{maxRiskTier}'.");
        }

        if (!Enum.IsDefined(mode) || mode == AutonomyMode.Unknown)
        {
            throw new DomainValidationException(
                nameof(mode),
                "A grant must name a mode. Unknown is the value resolution produces when it finds " +
                "nothing, and storing it would make a deliberate grant indistinguishable from an " +
                "absent one.");
        }

        // Structural, and not configurable: nothing grants unattended movement of real money. The
        // policy engine refuses that capability unconditionally, and a grant purporting to permit it
        // would be a row implying a permission the system does not have.
        if (capability == Capability.FinancialExecution)
        {
            throw new DomainRuleViolationException(
                "AutonomyGrant.NoFinancialExecution",
                "Financial execution has no execution plane and is refused structurally. A grant " +
                "cannot create an authority the system does not implement.");
        }

        // Equally structural: a grant that let something administer the safety system unattended
        // would be a grant that can widen itself on the next pass.
        if (IsSafetyAdministration(capability) && mode > AutonomyMode.PrepareForApproval)
        {
            throw new DomainRuleViolationException(
                "AutonomyGrant.NoUnattendedSafetyAdministration",
                $"Capability '{capability}' administers the safety system and may never run above " +
                "PrepareForApproval. A grant that can change grants is a grant that can widen itself.");
        }

        var environment = Text(environmentName, nameof(environmentName), PolicyContextEnvironmentLimit,
            "A grant applies to one named environment. A permission granted where the venue is " +
            "simulated carries no weight where it is not.");

        var granter = Text(grantedBy, nameof(grantedBy), MaxGrantedByLength,
            "A grant must name the person who gave it. An unattributed grant cannot be questioned " +
            "afterwards, which is most of what a grant is for.");

        var limitSet = Text(limitSetName, nameof(limitSetName), MaxLimitSetLength,
            "A grant must name the limit set an unattended action must also satisfy. Autonomy " +
            "'within a named limit set' with no set named is autonomy within nothing.");

        if (maxExposure.IsNegative)
        {
            throw new DomainValidationException(
                nameof(maxExposure),
                "An exposure ceiling may not be negative. Every exposure would exceed it, so the " +
                "grant would refuse the actions it was written to permit.");
        }

        if (validFor <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(validFor),
                "A grant must expire. Autonomy that never expires is autonomy nobody re-examines.");
        }

        if (validFor > TimeSpan.FromDays(MaxValidityDays))
        {
            throw new DomainValidationException(
                nameof(validFor),
                $"A grant may not run longer than {MaxValidityDays} days without being renewed.");
        }

        return new AutonomyGrant(
            Guid.NewGuid(),
            capability,
            NormaliseActionType(actionType),
            environment,
            mode,
            maxRiskTier,
            maxExposure,
            limitSet,
            granter,
            nowUtc,
            nowUtc.Add(validFor));
    }

    /// <summary>
    /// Issues a grant of unattended execution, on the strength of a promotion warrant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every dimension of the grant must sit inside the warrant: the capability, the environment, the
    /// action type, the mode, the risk ceiling and the exposure ceiling. A grant that exceeded its
    /// warrant on any one of them would be a permission nobody argued for, wearing the authority of
    /// one that was.
    /// </para>
    /// <para>
    /// The grant may be narrower than its warrant, and usually should be. A warrant is what the
    /// evidence supports; a grant is what somebody is willing to do about it.
    /// </para>
    /// </remarks>
    public static AutonomyGrant IssueBounded(
        PromotionWarrant warrant,
        string? actionType,
        string environmentName,
        AutonomyMode mode,
        RiskTier maxRiskTier,
        Money maxExposure,
        string limitSetName,
        string grantedBy,
        DateTime nowUtc,
        TimeSpan validFor)
    {
        ArgumentNullException.ThrowIfNull(warrant);
        ArgumentNullException.ThrowIfNull(maxExposure);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var refusal = warrant.WhyItDoesNotCover(
            warrant.Capability, actionType, environmentName, mode, maxRiskTier, maxExposure, nowUtc);

        if (refusal is not null)
        {
            throw new DomainRuleViolationException(BeyondWarrantRule, refusal);
        }

        // The action class this grant could ever cover has to be one that may run unattended at all.
        // Checked here as well as at the warrant, because the grant is where the risk ceiling is
        // finally chosen and a narrower warrant does not make a wider grant safe.
        var classRefusal = BoundedExecutionRule.Admits(
            warrant.Capability, ReversibilityClass.Reversible, maxRiskTier, mode);

        if (classRefusal != BoundedExecutionRefusal.None)
        {
            throw new DomainRuleViolationException(
                BeyondWarrantRule,
                $"a grant of {mode} for {warrant.Capability} up to risk tier {maxRiskTier} is outside " +
                $"the class that may run unattended: {BoundedExecutionRule.Explain(classRefusal)}");
        }

        // Everything the attended factory validates still applies, so it is reused rather than
        // restated - with the mode lowered past its own gate and raised again afterwards. Two copies
        // of "what makes a grant well formed" would be two places for it to drift.
        var grant = Issue(
            warrant.Capability,
            actionType,
            environmentName,
            HighestAttendedMode,
            maxRiskTier,
            maxExposure,
            limitSetName,
            grantedBy,
            nowUtc,
            validFor);

        grant.GrantedMode = mode;
        grant.PromotionWarrantId = warrant.PromotionWarrantId;

        return grant;
    }

    /// <summary>Withdraws the grant. Takes effect on the next resolution.</summary>
    public void Revoke(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (IsRevoked)
        {
            // Idempotent rather than an error. A revocation arriving twice - a retry, two operators,
            // the automatic breaker and a human at once - must not fail; the grant is already off,
            // which is the state the caller wanted.
            return;
        }

        RevokedAtUtc = nowUtc;
        RevocationReason = Text(reason, nameof(reason), MaxReasonLength,
            "A revocation must state a reason.");
    }

    /// <summary>
    /// Lowers the effective mode by one level and records why. The circuit breaker on autonomy.
    /// </summary>
    /// <remarks>
    /// Deterministic and one level at a time, so that a metric crossing its threshold degrades the
    /// system rather than switching it off - and so that repeated crossings walk it down to
    /// <see cref="AutonomyMode.Off"/> without anybody having to be watching. Demoting a grant that
    /// is already at Off does nothing and reports so; there is no lower level to reach.
    /// </remarks>
    public bool Demote(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var trimmed = Text(reason, nameof(reason), MaxReasonLength,
            "A demotion must state which measurement crossed its threshold.");

        var current = EffectiveMode;

        if (current <= AutonomyMode.Off)
        {
            return false;
        }

        DemotedMode = current - 1;
        DemotedAtUtc = nowUtc;
        DemotionReason = trimmed;
        DemotionCount++;

        return true;
    }

    public override string ToString() =>
        $"grant {AutonomyGrantId} {Capability}{(ActionType is null ? string.Empty : "/" + ActionType)}" +
        $" @{EnvironmentName} = {EffectiveMode}, expires {ExpiresAtUtc:O}";

    /// <summary>Capabilities that govern the safety system itself.</summary>
    internal static bool IsSafetyAdministration(Capability capability) =>
        capability is Capability.PolicyAdministration
            or Capability.AutonomyAdministration
            or Capability.ApprovalAdministration;

    /// <summary>
    /// Mirrors <c>PolicyContext.MaxEnvironmentNameLength</c>. Duplicated as a constant rather than
    /// referenced so that this type does not depend on the policy namespace for a number.
    /// </summary>
    private const int PolicyContextEnvironmentLimit = 60;

    private static string? NormaliseActionType(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return null;
        }

        var trimmed = actionType.Trim();

        if (trimmed.Length > 100)
        {
            throw new DomainValidationException(
                nameof(actionType),
                "An action type may not exceed 100 characters.");
        }

        return trimmed;
    }

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
