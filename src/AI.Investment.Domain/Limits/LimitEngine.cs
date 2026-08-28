using System.Globalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Limits;

/// <summary>
/// The pre-execution ceilings, evaluated in code before anything is allowed to happen.
/// </summary>
/// <remarks>
/// <para>
/// A pure function of the proposal, the current exposure and the configured limits. That shape is
/// the point: these checks are the ones standing between a defect and a loss, so they are held to
/// the highest test bar in the solution, and a function with no dependencies is one that can
/// actually be tested exhaustively rather than approximately.
/// </para>
/// <para>
/// <strong>It reports every breach, not the first.</strong> A proposal stopped by three ceilings at
/// once needs a different response from one that overshot a single limit marginally, and only the
/// full list distinguishes them.
/// </para>
/// <para>
/// <strong>A currency mismatch is a breach, not something to skip.</strong> A limit configured in a
/// currency the proposal does not use cannot be compared, and the safe reading of "cannot compare"
/// is "refuse" - the alternative is a ceiling that silently never binds.
/// </para>
/// </remarks>
public static class LimitEngine
{
    public static LimitVerdict Evaluate(
        ActionProposal proposal,
        ExposureSnapshot snapshot,
        LimitSet limits,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(limits);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (limits.RefusesEverything)
        {
            return LimitVerdict.Refused(
            [
                LimitBreach.Create(
                    LimitKind.Unknown,
                    "the configured limits could not be read, so nothing is permitted. A system that " +
                    "cannot determine its own ceilings must not act."),
            ]);
        }

        var breaches = new List<LimitBreach>();

        CheckInstrument(proposal, limits, breaches);
        CheckPositionSize(proposal, limits, breaches);
        CheckTotalExposure(proposal, snapshot, limits, breaches);
        CheckDailyLoss(snapshot, limits, proposal, breaches);
        CheckDrawdown(snapshot, limits, proposal, breaches);
        CheckActionCount(proposal, snapshot, limits, breaches);
        CheckCycleCost(proposal, snapshot, limits, breaches);
        CheckConcentration(proposal, snapshot, limits, breaches);
        CheckCooldown(snapshot, limits, proposal, nowUtc, breaches);

        return LimitVerdict.Refused(breaches);
    }

    private static void CheckInstrument(ActionProposal proposal, LimitSet limits, List<LimitBreach> breaches)
    {
        if (!limits.RestrictsInstruments || limits.Allows(proposal.Target.Identifier))
        {
            return;
        }

        breaches.Add(LimitBreach.Create(
            LimitKind.InstrumentAllowList,
            $"'{proposal.Target}' is not on the instrument allow-list."));
    }

    private static void CheckPositionSize(ActionProposal proposal, LimitSet limits, List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxPositionSize, proposal.Capability) is not { Amount: { } ceiling })
        {
            return;
        }

        var exposure = proposal.Economics.EstimatedExposure;

        if (Mismatched(ceiling, exposure, LimitKind.MaxPositionSize, breaches))
        {
            return;
        }

        if (exposure.IsGreaterThan(ceiling))
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxPositionSize,
                $"exposure of {exposure} exceeds the {ceiling} ceiling for a single action."));
        }
    }

    private static void CheckTotalExposure(
        ActionProposal proposal,
        ExposureSnapshot snapshot,
        LimitSet limits,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxTotalExposure, proposal.Capability) is not { Amount: { } ceiling })
        {
            return;
        }

        var exposure = proposal.Economics.EstimatedExposure;

        if (Mismatched(ceiling, exposure, LimitKind.MaxTotalExposure, breaches) ||
            Mismatched(ceiling, snapshot.TotalExposure, LimitKind.MaxTotalExposure, breaches))
        {
            return;
        }

        var projected = snapshot.TotalExposure.Add(exposure);

        if (projected.IsGreaterThan(ceiling))
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxTotalExposure,
                $"total exposure would reach {projected}, above the {ceiling} ceiling."));
        }
    }

    private static void CheckDailyLoss(
        ExposureSnapshot snapshot,
        LimitSet limits,
        ActionProposal proposal,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxDailyLoss, proposal.Capability) is not { Amount: { } ceiling })
        {
            return;
        }

        if (Mismatched(ceiling, snapshot.RealisedLossToday, LimitKind.MaxDailyLoss, breaches))
        {
            return;
        }

        // At or above, not merely above. A ceiling reached exactly has been reached.
        if (!ceiling.IsGreaterThan(snapshot.RealisedLossToday))
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxDailyLoss,
                $"{snapshot.RealisedLossToday} has already been lost today against a {ceiling} ceiling."));
        }
    }

    private static void CheckDrawdown(
        ExposureSnapshot snapshot,
        LimitSet limits,
        ActionProposal proposal,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxDrawdown, proposal.Capability) is not { Amount: { } ceiling })
        {
            return;
        }

        var drawdown = snapshot.Drawdown;

        if (Mismatched(ceiling, drawdown, LimitKind.MaxDrawdown, breaches))
        {
            return;
        }

        if (!ceiling.IsGreaterThan(drawdown))
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxDrawdown,
                $"drawdown of {drawdown} has reached the {ceiling} ceiling."));
        }
    }

    private static void CheckActionCount(
        ActionProposal proposal,
        ExposureSnapshot snapshot,
        LimitSet limits,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxActionsPerCapabilityPerDay, proposal.Capability) is not { Count: { } ceiling })
        {
            return;
        }

        var taken = snapshot.ActionsToday(proposal.Capability);

        if (taken >= ceiling)
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxActionsPerCapabilityPerDay,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{taken} {proposal.Capability} actions have been taken today against a ceiling of {ceiling}.")));
        }
    }

    private static void CheckCycleCost(
        ActionProposal proposal,
        ExposureSnapshot snapshot,
        LimitSet limits,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxCostPerCycle, proposal.Capability) is not { Amount: { } ceiling })
        {
            return;
        }

        var cost = proposal.Economics.EstimatedCost;

        if (Mismatched(ceiling, cost, LimitKind.MaxCostPerCycle, breaches) ||
            Mismatched(ceiling, snapshot.CycleCost, LimitKind.MaxCostPerCycle, breaches))
        {
            return;
        }

        var projected = snapshot.CycleCost.Add(cost);

        if (projected.IsGreaterThan(ceiling))
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxCostPerCycle,
                $"this cycle would spend {projected}, above the {ceiling} ceiling."));
        }
    }

    /// <summary>
    /// Refuses a position that would be too large a share of the book.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Measured against equity, not against total exposure.</strong> Against exposure the
    /// first position in a flat book is always a hundred per cent of it, so any concentration
    /// ceiling below one would refuse every opening trade forever - a limit that cannot be
    /// satisfied is an off switch wearing a ceiling's name, and the failure would look like a
    /// correctly working control.
    /// </para>
    /// <para>
    /// A book with no equity is refused rather than divided by: a share of nothing is not a
    /// number, and the fail-closed reading of "cannot be measured" is "not permitted".
    /// </para>
    /// </remarks>
    private static void CheckConcentration(
        ActionProposal proposal,
        ExposureSnapshot snapshot,
        LimitSet limits,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.MaxConcentration, proposal.Capability) is not { Ratio: { } ceiling })
        {
            return;
        }

        var exposure = proposal.Economics.EstimatedExposure;

        if (exposure.IsZero)
        {
            return;
        }

        var equity = snapshot.CurrentEquity;

        if (Mismatched(equity, exposure, LimitKind.MaxConcentration, breaches))
        {
            return;
        }

        if (!equity.IsPositive)
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxConcentration,
                "concentration cannot be measured against a book holding no equity, and a share " +
                "that cannot be measured is refused rather than assumed to be within the ceiling."));

            return;
        }

        var instrument = snapshot.ExposureTo(proposal.Target.Identifier).Add(exposure);
        var share = instrument.Amount / equity.Amount;

        if (share > ceiling.Ratio)
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.MaxConcentration,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{proposal.Target}' would hold {share:0.###} of equity, above {ceiling}.")));
        }
    }

    private static void CheckCooldown(
        ExposureSnapshot snapshot,
        LimitSet limits,
        ActionProposal proposal,
        DateTime nowUtc,
        List<LimitBreach> breaches)
    {
        if (limits.For(LimitKind.CooldownAfterLoss, proposal.Capability) is not { Duration: { } cooldown })
        {
            return;
        }

        if (snapshot.LastRealisedLossAtUtc is not { } lastLoss)
        {
            return;
        }

        var elapsed = nowUtc - lastLoss;

        if (elapsed < cooldown)
        {
            breaches.Add(LimitBreach.Create(
                LimitKind.CooldownAfterLoss,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"only {elapsed} has passed since the last realised loss, against a {cooldown} cooldown.")));
        }
    }

    /// <summary>
    /// Reports a currency mismatch as a breach.
    /// </summary>
    /// <remarks>
    /// The fail-closed direction. A limit that cannot be compared against the thing it is meant to
    /// bound is a limit that would otherwise never bind, and its absence would be invisible - the
    /// configuration would look complete and stop nothing.
    /// </remarks>
    private static bool Mismatched(Money ceiling, Money value, LimitKind kind, List<LimitBreach> breaches)
    {
        if (ceiling.Currency == value.Currency)
        {
            return false;
        }

        breaches.Add(LimitBreach.Create(
            kind,
            $"the limit is configured in {ceiling.Currency} but the action is in {value.Currency}; " +
            "a limit that cannot be compared is refused rather than skipped."));

        return true;
    }
}
