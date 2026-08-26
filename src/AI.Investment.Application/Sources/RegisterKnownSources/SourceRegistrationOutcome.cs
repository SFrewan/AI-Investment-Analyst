namespace AI.Investment.Application.Sources.RegisterKnownSources;

/// <summary>What happened to one shipped source definition.</summary>
public enum SourceRegistrationOutcome
{
    /// <summary>Registered, inactive. Activation remains a separate act.</summary>
    Registered = 0,

    /// <summary>Already in the registry; left exactly as it is.</summary>
    AlreadyRegistered = 1,

    /// <summary>Policy did not permit the registration.</summary>
    Refused = 2,
}

/// <summary>The result of seeding one source.</summary>
/// <param name="SourceId">Which source.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Why, in the policy engine's words when it refused.</param>
public sealed record SourceRegistrationResult(
    string SourceId,
    SourceRegistrationOutcome Outcome,
    string Reason);
