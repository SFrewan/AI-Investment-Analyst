namespace AI.Investment.Domain.Ingestion;

/// <summary>How an ingestion run ended.</summary>
public enum IngestionOutcome
{
    /// <summary>
    /// The run has not finished. Default so that an unset value never reads as success.
    /// </summary>
    InProgress = 0,

    /// <summary>Everything requested was retrieved and archived.</summary>
    Succeeded = 1,

    /// <summary>
    /// Some of what was requested was retrieved. Distinct from success because a partial result
    /// silently treated as complete is how gaps enter a history without anyone noticing.
    /// </summary>
    PartiallySucceeded = 2,

    /// <summary>The run was attempted and failed - transport, parsing, or the provider erroring.</summary>
    Failed = 3,

    /// <summary>
    /// The run never started because the source was not admissible. Distinct from failure: a
    /// refusal is the platform working correctly, and counting it as an error would train whoever
    /// reads the dashboard to ignore errors.
    /// </summary>
    Refused = 4,
}
