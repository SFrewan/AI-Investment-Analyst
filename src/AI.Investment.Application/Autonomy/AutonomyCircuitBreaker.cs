using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Autonomy;

/// <summary>What one pass of the breaker did.</summary>
/// <param name="Examined">Grants it looked at.</param>
/// <param name="Demoted">Grants it lowered.</param>
/// <param name="Triggers">Why, one per demotion.</param>
public sealed record CircuitBreakerSweep(
    int Examined,
    int Demoted,
    IReadOnlyList<DemotionTrigger> Triggers)
{
    public bool AnythingChanged => Demoted > 0;
}

/// <summary>
/// Lowers autonomy automatically when the conditions it was granted under stop holding.
/// </summary>
/// <remarks>
/// <para>
/// §K.6's automatic demotion, and the half of bounded autonomy that matters most. Granting autonomy
/// is a decision somebody makes once, in good conditions, with the evidence in front of them.
/// Withdrawing it has to happen at four in the morning when the conditions have changed and nobody
/// is there, which is why it cannot require a person and why every "cannot tell" answer lowers the
/// level rather than leaving it.
/// </para>
/// <para>
/// <strong>One level at a time, not an off switch.</strong> A breach demotes a grant from unattended
/// execution to preparing for approval - the platform keeps working and starts asking. Repeated
/// breaches walk it down to Off without anybody intervening. That gradient is deliberate: an
/// all-or-nothing breaker makes operators reluctant to arm it.
/// </para>
/// <para>
/// <strong>It only ever lowers.</strong> There is no path here that raises a grant, renews one, or
/// clears a demotion. Recovering autonomy means a person issuing a new grant against fresh evidence,
/// which is a row somebody signed. A breaker that can close itself is not a breaker.
/// </para>
/// </remarks>
public sealed class AutonomyCircuitBreaker
{
    private readonly IAutonomyGrantStore _grants;
    private readonly IPromotionWarrantStore _warrants;
    private readonly IEscalationStore _escalations;
    private readonly IKillSwitch _killSwitch;
    private readonly AutonomyAdministration _administration;
    private readonly IClock _clock;

    public AutonomyCircuitBreaker(
        IAutonomyGrantStore grants,
        IPromotionWarrantStore warrants,
        IEscalationStore escalations,
        IKillSwitch killSwitch,
        AutonomyAdministration administration,
        IClock clock)
    {
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _warrants = warrants ?? throw new ArgumentNullException(nameof(warrants));
        _escalations = escalations ?? throw new ArgumentNullException(nameof(escalations));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The thresholds in force.</summary>
    public static DemotionThresholds Thresholds => DemotionThresholds.Standard;

    /// <summary>
    /// Examines every grant that is currently above the attended ceiling and lowers the ones whose
    /// conditions no longer hold.
    /// </summary>
    /// <remarks>
    /// Grants at or below <see cref="AutonomyGrant.HighestAttendedMode"/> are left alone. They are
    /// already asking a person before anything happens, and demoting them further would turn a
    /// transient signal into a platform that has quietly stopped proposing anything.
    /// </remarks>
    public async Task<CircuitBreakerSweep> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var killSwitch = await _killSwitch.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Unknown counts as engaged. KillSwitchState.Unknown denies everywhere else in this system
        // for the same reason: a switch that cannot be read is not a switch that is off.
        var engagedOrUnknown = killSwitch != KillSwitchState.Disengaged;

        var unhandled = await CountUnhandledAsync(now, cancellationToken).ConfigureAwait(false);
        var all = await _grants.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var examined = 0;
        var demoted = 0;
        var triggers = new List<DemotionTrigger>();

        foreach (var grant in all)
        {
            if (!grant.IsActive(now) || grant.EffectiveMode <= AutonomyGrant.HighestAttendedMode)
            {
                continue;
            }

            examined++;

            var warrantValid = await WarrantStillCoversAsync(grant, now, cancellationToken).ConfigureAwait(false);

            var signals = new DemotionSignals
            {
                KillSwitchEngagedOrUnknown = engagedOrUnknown,
                WarrantNoLongerValid = !warrantValid,

                // Breaches and failures are not yet counted per capability anywhere in the platform.
                // Reporting zero would be asserting something nobody measured, so they are reported
                // as unknown - which demotes. That is the honest reading and the safe one, and it is
                // recorded here rather than left as a surprise.
                PolicyBreaches = null,
                ExecutionFailures = null,
                UnhandledEscalations = unhandled,
                EvidenceNoLongerJustifies = false,
                EvidenceAge = now - grant.GrantedAtUtc,
            };

            var trigger = DemotionPolicy.Required(signals, Thresholds);

            if (trigger == DemotionTrigger.None)
            {
                continue;
            }

            var outcome = await _administration
                .DemoteAsync(
                    grant.AutonomyGrantId,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{trigger}: {DemotionPolicy.Explain(trigger)}"),
                    cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Succeeded)
            {
                demoted++;
                triggers.Add(trigger);
            }
        }

        return new CircuitBreakerSweep(examined, demoted, triggers);
    }

    private async Task<bool> WarrantStillCoversAsync(
        AutonomyGrant grant,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (grant.PromotionWarrantId is null)
        {
            // A grant above the attended ceiling with no warrant behind it is exactly what the
            // promotion gate exists to prevent, so meeting one is a reason to lower it rather than a
            // reason to leave it alone.
            return false;
        }

        var warrant = await _warrants
            .FindAsync(grant.PromotionWarrantId.Value, cancellationToken)
            .ConfigureAwait(false);

        return warrant is not null &&
            warrant.WhyItDoesNotCover(
                grant.Capability,
                grant.ActionType,
                grant.EnvironmentName,
                grant.GrantedMode,
                grant.MaxRiskTier,
                grant.MaxExposure,
                nowUtc) is null;
    }

    private async Task<int?> CountUnhandledAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            return await _escalations.CountUnhandledAsync(nowUtc, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Deliberate: a store that cannot answer must produce "unknown",
                              // which demotes, rather than an exception that leaves every grant
                              // where it was. The breaker's whole value is in the case where
                              // something is already wrong.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
