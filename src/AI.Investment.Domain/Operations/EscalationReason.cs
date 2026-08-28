namespace AI.Investment.Domain.Operations;

/// <summary>
/// The deterministic conditions that make reaching a human mandatory.
/// </summary>
/// <remarks>
/// <para>
/// The human is not the operator of normal workflow; they are the authority on exceptions. Which
/// exceptions is decided here rather than by a model, and the numeric order is the precedence: when
/// several apply, the most severe is the one reported, so that an escalation's headline says the
/// worst thing that is true about it rather than the first thing noticed.
/// </para>
/// </remarks>
public enum EscalationReason
{
    /// <summary>Nothing requires a human. The action may proceed on its own merits.</summary>
    None = 0,

    /// <summary>No autonomy grant resolved, or the one that did has expired. Fail-closed.</summary>
    NoAutonomyGrant = 1,

    /// <summary>A configured limit would be breached. The limit is the decision.</summary>
    LimitBreach = 2,

    /// <summary>The action cannot be undone. Reversibility, not size, is the real axis.</summary>
    Irreversible = 3,

    /// <summary>Risk tier at or above the configured band.</summary>
    RiskTierAboveBand = 4,

    /// <summary>Exposure above the band the grant covers.</summary>
    ExposureAboveBand = 5,

    /// <summary>Evidence is stale, quarantined, or single-sourced where corroboration is required.</summary>
    UntrustworthyEvidence = 6,

    /// <summary>Confidence below the capability's threshold, or agents materially disagree.</summary>
    LowConfidence = 7,

    /// <summary>A cycle budget was exhausted. Something is wrong, or the work is bigger than expected.</summary>
    BudgetExhausted = 8,

    /// <summary>A provider failed, or the same step has been retried too many times.</summary>
    ProviderFailure = 9,

    /// <summary>The action falls outside the distribution the capability was granted for.</summary>
    Novelty = 10,
}
