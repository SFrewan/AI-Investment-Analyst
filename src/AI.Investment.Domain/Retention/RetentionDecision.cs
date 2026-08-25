namespace AI.Investment.Domain.Retention;

/// <summary>
/// What must happen to one archived payload, and which rule says so.
/// </summary>
/// <remarks>
/// Carries its rule identifier for the same reason a policy decision does: a deletion is
/// irreversible, so "why was this removed?" has to be answerable from the record long after the
/// payload itself is gone.
/// </remarks>
/// <param name="Outcome">Keep it, or delete it because the licence requires that.</param>
/// <param name="RuleId">The versioned rule that produced this decision.</param>
/// <param name="Reason">A human-readable explanation, safe to store permanently.</param>
/// <param name="RequiresEvidenceMarking">
/// True when the payload is referenced by stored evidence and must still be deleted. The
/// reference is not cancelled by the deletion - the claim survives and is marked unreplayable, so
/// the gap is visible rather than discovered by a backtest that quietly returns nothing.
/// </param>
public sealed record RetentionDecision(
    RetentionOutcome Outcome,
    string RuleId,
    string Reason,
    bool RequiresEvidenceMarking = false)
{
    public bool RequiresDeletion => Outcome == RetentionOutcome.DeleteRequired;

    public override string ToString() =>
        $"{Outcome} [{RuleId}] {Reason}" + (RequiresEvidenceMarking ? " (evidence marking required)" : string.Empty);
}
