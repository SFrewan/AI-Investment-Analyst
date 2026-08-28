namespace AI.Investment.Domain.Enums;

/// <summary>Classes of event recorded in the append-only audit trail.</summary>
/// <remarks>
/// Deliberately small in Phase 1. Later phases add ingestion, analysis, approval decision,
/// outcome measurement and autonomy-grant events; the record shape is designed to take them
/// without a schema rewrite.
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
}
