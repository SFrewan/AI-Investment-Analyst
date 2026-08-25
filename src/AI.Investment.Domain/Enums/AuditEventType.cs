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
}
