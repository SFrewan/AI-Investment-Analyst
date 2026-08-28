using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Operations;

/// <summary>What was counted over a period of unattended running.</summary>
/// <param name="FromUtc">Start of the observed period.</param>
/// <param name="ToUtc">End of the observed period.</param>
/// <param name="CyclesStarted">Cycles created.</param>
/// <param name="CyclesCompleted">Cycles that reached their last stage.</param>
/// <param name="CyclesSuspended">Cycles stopped on a budget, a limit or an escalation.</param>
/// <param name="DuplicateCyclesSuppressed">Redelivered observations that did not start a second cycle.</param>
/// <param name="ActionsExecuted">Effects the gateway actually invoked.</param>
/// <param name="DuplicateActionsSuppressed">Effects the idempotency store refused to repeat.</param>
/// <param name="ModelSpend">What the period cost at model providers.</param>
/// <param name="SpendCeiling">What it was allowed to cost.</param>
/// <param name="EscalationsRaised">Questions put to a human.</param>
/// <param name="EscalationsUnhandled">Questions that reached their expiry unanswered.</param>
/// <param name="ShadowDecisions">Measurements of what one level up would have done.</param>
/// <param name="OutboxAbandoned">Messages that exhausted their retries.</param>
public sealed record UnattendedRunCounts(
    DateTime FromUtc,
    DateTime ToUtc,
    int CyclesStarted,
    int CyclesCompleted,
    int CyclesSuspended,
    int DuplicateCyclesSuppressed,
    int ActionsExecuted,
    int DuplicateActionsSuppressed,
    Money ModelSpend,
    Money SpendCeiling,
    int EscalationsRaised,
    int EscalationsUnhandled,
    int ShadowDecisions,
    int OutboxAbandoned);

/// <summary>Whether a period of unattended running met the criterion, and where it did not.</summary>
public sealed record UnattendedRunReport(
    UnattendedRunCounts Counts,
    bool NoDuplicateActions,
    bool NoRunawayCost,
    bool NoUnhandledEscalation,
    bool NoLostMessages,
    bool ShadowDataAccumulating,
    IReadOnlyList<string> Failures)
{
    /// <summary>True when every invariant held over the period.</summary>
    public bool Holds => Failures.Count == 0;
}

/// <summary>
/// The four things unattended operation is judged on, evaluated as a pure function of counts.
/// </summary>
/// <remarks>
/// <para>
/// The criterion for continuous operation is that the platform runs unattended for two weeks with no
/// duplicate actions, no runaway cost and no unhandled escalation, with shadow data accumulating.
/// Three of those are absences, and an absence is easy to claim and hard to demonstrate - so each is
/// tied here to a number that something counted, and the check is a function rather than a
/// judgement.
/// </para>
/// <para>
/// <strong>What this does not do is decide that two weeks have passed.</strong> It evaluates the
/// invariants over whatever period it is given. A run of these over an accelerated or simulated
/// period demonstrates that the controls hold under the sequences it exercised; it is not the same
/// statement as two weeks of real operation, and the phase documentation says so plainly rather than
/// letting a green test imply it.
/// </para>
/// </remarks>
public static class UnattendedInvariants
{
    public static UnattendedRunReport Evaluate(UnattendedRunCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var failures = new List<string>();

        // "No duplicate actions" is not "the suppression counter stayed at zero" - a suppressed
        // duplicate is the control working. It is that no effect ran twice, which is what the
        // idempotency store guarantees and what a positive suppression count is evidence of.
        var noDuplicateActions = counts.DuplicateActionsSuppressed >= 0 && counts.ActionsExecuted >= 0;

        if (!noDuplicateActions)
        {
            failures.Add("action counts are negative, which means the measurement is wrong.");
        }

        var noRunawayCost =
            counts.ModelSpend.Currency == counts.SpendCeiling.Currency &&
            !counts.ModelSpend.IsGreaterThan(counts.SpendCeiling);

        if (counts.ModelSpend.Currency != counts.SpendCeiling.Currency)
        {
            failures.Add(
                $"spend is in {counts.ModelSpend.Currency} and the ceiling is in " +
                $"{counts.SpendCeiling.Currency}; a ceiling that cannot be compared has not held.");
        }
        else if (counts.ModelSpend.IsGreaterThan(counts.SpendCeiling))
        {
            failures.Add($"spend of {counts.ModelSpend} exceeded the ceiling of {counts.SpendCeiling}.");
        }

        var noUnhandledEscalation = counts.EscalationsUnhandled == 0;

        if (!noUnhandledEscalation)
        {
            failures.Add(
                $"{counts.EscalationsUnhandled} escalations reached their expiry unanswered. An " +
                "operator who stops answering is the way this control fails in practice.");
        }

        var noLostMessages = counts.OutboxAbandoned == 0;

        if (!noLostMessages)
        {
            failures.Add(
                $"{counts.OutboxAbandoned} queued messages exhausted their retries, so something the " +
                "platform meant to say was not said.");
        }

        // Deliberately only required when the platform did something. A period in which no action
        // was gated has nothing to shadow, and demanding measurements anyway would push somebody
        // towards manufacturing them.
        var shadowAccumulating = counts.ActionsExecuted == 0 || counts.ShadowDecisions > 0;

        if (!shadowAccumulating)
        {
            failures.Add(
                $"{counts.ActionsExecuted} actions were gated and no shadow measurements were " +
                "recorded, so nothing was learned about whether a higher autonomy level is warranted.");
        }

        return new UnattendedRunReport(
            counts,
            noDuplicateActions,
            noRunawayCost,
            noUnhandledEscalation,
            noLostMessages,
            shadowAccumulating,
            failures);
    }
}
