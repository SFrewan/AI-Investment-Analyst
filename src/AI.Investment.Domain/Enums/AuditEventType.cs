namespace AI.Investment.Domain.Enums;

/// <summary>Classes of event recorded in the append-only audit trail.</summary>
/// <remarks>
/// Deliberately small in Phase 1. Later phases add ingestion, analysis, approval decision,
/// outcome measurement and autonomy-grant events; the record shape is designed to take them
/// without a schema rewrite. Phase 6 exercised that design: the members from 10 onwards were added
/// with no change to <c>AuditRecord</c>'s columns and no migration of the table.
/// </remarks>
public enum AuditEventType
{
    /// <summary>A policy decision was reached for a proposal. Recorded for every outcome.</summary>
    PolicyEvaluated = 0,

    /// <summary>An effect ran and succeeded.</summary>
    ActionExecuted = 1,

    /// <summary>An effect ran and threw.</summary>
    ActionFailed = 2,

    /// <summary>An action was refused by policy. The effect was never invoked.</summary>
    ActionDenied = 3,

    /// <summary>An action needs human approval. The effect was never invoked.</summary>
    ApprovalRequired = 4,

    /// <summary>A duplicate idempotency key was seen; the effect was not repeated.</summary>
    DuplicateSuppressed = 5,

    /// <summary>An analysis was requested for a subject at a stated knowledge cutoff. Phase 4.</summary>
    AnalysisRequested = 6,

    /// <summary>An agent produced a validated, grounded output. Phase 4.</summary>
    AgentOutputAccepted = 7,

    /// <summary>
    /// An agent's output was refused - schema, groundedness, budget or provider. Phase 4.
    /// </summary>
    /// <remarks>
    /// Recorded as its own event rather than folded into the accepted one. Refusals are the
    /// entries that say whether the validators are doing anything, and a rejection rate that
    /// suddenly falls to zero is a defect in the check, not an improvement in the model.
    /// </remarks>
    AgentOutputRejected = 8,

    /// <summary>A whole analysis pipeline run finished. Phase 4.</summary>
    AnalysisCompleted = 9,

    /// <summary>An operating cycle started. Phase 6.</summary>
    CycleStarted = 10,

    /// <summary>
    /// An operating cycle stopped short of completing - a budget, a limit, or an escalation. Phase 6.
    /// </summary>
    /// <remarks>
    /// Its own event rather than a completion with a flag. Suspension is the interesting outcome in
    /// unattended operation: a run of them is the shape of a system that is trying to do something
    /// it is not allowed to do, and that pattern is invisible if it is a column on "finished".
    /// </remarks>
    CycleSuspended = 11,

    /// <summary>An operating cycle reached its last stage. Phase 6.</summary>
    CycleCompleted = 12,

    /// <summary>A watch fired and started a cycle. Phase 6.</summary>
    WatchFired = 13,

    /// <summary>
    /// A watch would have fired and was held back - cooldown, backpressure, or a duplicate. Phase 6.
    /// </summary>
    /// <remarks>
    /// Recorded, not discarded. Suppressions are how a trigger storm looks from the inside, and a
    /// suppression count that climbs while the firing count stays flat is the control working. A
    /// suppression count of zero during a volatile session is a control that is not.
    /// </remarks>
    WatchSuppressed = 14,

    /// <summary>Something was put to a human. Phase 6.</summary>
    EscalationRaised = 15,

    /// <summary>A human answered. Phase 6.</summary>
    EscalationResolved = 16,

    /// <summary>A human granted a capability some autonomy. Phase 6.</summary>
    AutonomyGranted = 17,

    /// <summary>A grant was withdrawn. Phase 6.</summary>
    AutonomyRevoked = 18,

    /// <summary>A measured threshold was crossed and a grant dropped a level automatically. Phase 6.</summary>
    AutonomyDemoted = 19,

    /// <summary>
    /// What a higher autonomy level would have decided was recorded. Nothing was executed. Phase 6.
    /// </summary>
    ShadowDecisionRecorded = 20,

    /// <summary>A queued message was delivered. Phase 6.</summary>
    OutboxDispatched = 21,

    /// <summary>
    /// A queued message exhausted its retries and was abandoned. Phase 6.
    /// </summary>
    /// <remarks>
    /// The one outbox outcome that must never be quiet. Everything else about the outbox is
    /// mechanics; this is the platform saying it has stopped trying to tell somebody something.
    /// </remarks>
    OutboxAbandoned = 22,
}
