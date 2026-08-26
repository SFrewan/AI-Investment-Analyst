namespace AI.Investment.Application.Retention;

/// <summary>
/// Walks the archive and applies each payload's licensed retention obligation.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="IRetentionEnforcer"/>, which decides about one payload.
/// Separating "what does this payload require?" from "when do we go looking?" is what lets the
/// rule that destroys evidence be exercised exhaustively without a scheduler, a clock or a
/// filesystem full of fixtures.
/// </para>
/// <para>
/// Every deletion still goes through the seam, one authorisation per payload, because that is
/// where <c>Capability.DataRetention</c> is checked and where the audit record is written. A sweep
/// that batched them into a single approval would ask an operator to authorise a number rather
/// than a decision.
/// </para>
/// </remarks>
public interface IRetentionSweep
{
    /// <summary>
    /// Considers up to <paramref name="limit"/> archived payloads.
    /// </summary>
    /// <param name="limit">
    /// How many to examine. Bounded on purpose: a sweep should end, and one that walked an
    /// unbounded archive would hold a scheduler slot for as long as the archive is large.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RetentionSweepSummary> SweepAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>What a sweep looked at and what it concluded.</summary>
/// <remarks>
/// <see cref="Deleted"/> and <see cref="DeletionsRefused"/> are separate numbers on purpose. A
/// sweep that decided fifty payloads must go and was allowed to delete none of them is a policy
/// problem that needs seeing, and a single "processed" count would hide it completely.
/// </remarks>
/// <param name="Examined">Payloads considered.</param>
/// <param name="Retained">Payloads their licence still permits keeping.</param>
/// <param name="Deleted">Payloads deleted, each under its own authorisation.</param>
/// <param name="DeletionsRefused">
/// Payloads whose licence required deletion but which policy did not authorise - denied, awaiting
/// approval, or already claimed by an earlier sweep.
/// </param>
/// <param name="Failed">
/// Payloads that could not be assessed at all, because something threw.
/// </param>
/// <param name="Reached">
/// True when the archive was exhausted, false when the limit stopped the sweep early. Without it,
/// a caller cannot tell "nothing left to do" from "there is more, ask again".
/// </param>
public sealed record RetentionSweepSummary(
    int Examined,
    int Retained,
    int Deleted,
    int DeletionsRefused,
    int Failed,
    bool Reached)
{
    public bool HasMore => !Reached;

    /// <summary>Payloads a licence says must go which are still on disk.</summary>
    /// <remarks>
    /// The number worth alerting on. Refusals and failures are both compliance exposure; the
    /// difference between them matters when diagnosing, not when deciding whether to look.
    /// </remarks>
    public int Outstanding => DeletionsRefused + Failed;

    public override string ToString() =>
        $"examined={Examined}, retained={Retained}, deleted={Deleted}, " +
        $"refused={DeletionsRefused}, failed={Failed}, complete={Reached}";
}
