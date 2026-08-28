using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// Resolves an autonomy mode from the grants that exist. Pure, total and fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>PolicyEngine</c> and for the same reason: no I/O, no clock of its own, no
/// state. Given the same request, the same grants and the same instant it returns the same answer,
/// which is the only basis on which a control like this can be believed.
/// </para>
/// <para><strong>The properties that must never be lost:</strong></para>
/// <list type="number">
/// <item><strong>Total.</strong> Every request resolves. Nothing found, everything expired, two
/// grants disagreeing - each has a defined answer, and each of those answers denies.</item>
/// <item><strong>Narrowing only.</strong> Every rule below can lower the resolved mode. None can
/// raise it. The result is the minimum of the grant and every ceiling that applies.</item>
/// <item><strong>Ambiguity denies.</strong> Two grants of equal specificity are refused rather than
/// resolved by ordering. Which one won would depend on retrieval order nobody controls, and the
/// answer would be "sometimes more autonomous".</item>
/// <item><strong>The narrower grant wins.</strong> A grant naming an action type beats one covering
/// the whole capability, because the narrower statement is the more deliberate one.</item>
/// </list>
/// </remarks>
public static class AutonomyResolver
{
    /// <summary>Resolution identifiers, recorded so a resolution can be explained after the fact.</summary>
    public const string NoGrantRule = "autonomy.no-grant@1";

    public const string AmbiguousGrantRule = "autonomy.ambiguous-grant@1";

    public const string ExposureIncomparableRule = "autonomy.exposure-incomparable@1";

    public const string ExposureAboveCeilingRule = "autonomy.exposure-above-ceiling@1";

    public const string RiskTierAboveCeilingRule = "autonomy.risk-tier-above-ceiling@1";

    public const string GrantResolvedRule = "autonomy.grant-resolved@1";

    /// <summary>
    /// The most any ceiling breach can leave in force. Above a ceiling the action does not stop
    /// being possible - it stops being possible <em>unattended</em>, which is an escalation.
    /// </summary>
    private const AutonomyMode CeilingBreachCap = AutonomyMode.PrepareForApproval;

    public static AutonomyResolution Resolve(
        AutonomyRequest request,
        IEnumerable<AutonomyGrant> grants,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(grants);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var applicable = grants
            .Where(grant => grant is not null)
            .Where(grant => grant.IsActive(nowUtc))
            .Where(grant => grant.Capability == request.Capability)
            .Where(grant => string.Equals(
                grant.EnvironmentName,
                request.EnvironmentName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The narrower statement is the more deliberate one, so an action-type grant is considered
        // before the capability-wide one and the two never combine.
        var exact = applicable
            .Where(grant => grant.ActionType is not null &&
                string.Equals(grant.ActionType, request.ActionType, StringComparison.Ordinal))
            .ToList();

        var wildcard = applicable.Where(grant => grant.ActionType is null).ToList();

        var candidates = exact.Count > 0 ? exact : wildcard;

        if (candidates.Count == 0)
        {
            return AutonomyResolution.Create(
                AutonomyMode.Unknown,
                ExposureBand.Unknown,
                autonomyGrantId: null,
                $"{NoGrantRule}: no active autonomy grant covers {request}. An ungranted capability " +
                "acts only with a human, which is the state every capability starts in.");
        }

        if (candidates.Count > 1)
        {
            return AutonomyResolution.Create(
                AutonomyMode.Off,
                ExposureBand.Unknown,
                autonomyGrantId: null,
                $"{AmbiguousGrantRule}: {candidates.Count} equally specific grants cover {request}. " +
                "Which one binds would depend on ordering nobody controls, so the request is refused " +
                "rather than resolved arbitrarily.");
        }

        var grant = candidates[0];
        var band = BandFor(request.Exposure, grant.MaxExposure);

        if (band == ExposureBand.Incomparable)
        {
            return AutonomyResolution.Create(
                AutonomyMode.Off,
                band,
                grant.AutonomyGrantId,
                $"{ExposureIncomparableRule}: the grant's ceiling is in " +
                $"{grant.MaxExposure.Currency} and the action is in {request.Exposure.Currency}. " +
                "A ceiling that cannot be compared has not been shown to hold.");
        }

        var mode = grant.EffectiveMode;
        var rule = GrantResolvedRule;
        var detail = $"grant {grant.AutonomyGrantId} resolves {request} to {mode}.";

        if (band == ExposureBand.Above && mode > CeilingBreachCap)
        {
            mode = CeilingBreachCap;
            rule = ExposureAboveCeilingRule;
            detail =
                $"exposure {request.Exposure} is above the grant's ceiling of {grant.MaxExposure}, " +
                $"so unattended execution is withdrawn and the action escalates instead.";
        }

        if (request.RiskTier > grant.MaxRiskTier && mode > CeilingBreachCap)
        {
            mode = CeilingBreachCap;
            rule = RiskTierAboveCeilingRule;
            detail =
                $"risk tier {request.RiskTier} is above the grant's ceiling of {grant.MaxRiskTier}, " +
                $"so unattended execution is withdrawn and the action escalates instead.";
        }

        return AutonomyResolution.Create(mode, band, grant.AutonomyGrantId, $"{rule}: {detail}");
    }

    /// <summary>
    /// Where an exposure sits relative to a ceiling, without ever converting between currencies.
    /// </summary>
    private static ExposureBand BandFor(Money exposure, Money ceiling)
    {
        if (exposure.IsZero)
        {
            // Zero is zero in any currency, so this is the one comparison that needs no rate.
            return ExposureBand.None;
        }

        if (exposure.Currency != ceiling.Currency)
        {
            return ExposureBand.Incomparable;
        }

        return exposure.IsGreaterThan(ceiling) ? ExposureBand.Above : ExposureBand.Within;
    }
}
