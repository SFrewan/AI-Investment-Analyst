namespace AI.Investment.Domain.Enums;

/// <summary>
/// What kind of thing proposed an action.
/// </summary>
/// <remarks>
/// The policy engine treats <see cref="AiAgent"/> differently by design, and that difference is
/// structural rather than configurable for the capabilities that govern the safety system
/// itself. Agent output is data; it is never execution authority.
/// </remarks>
public enum ProposerKind
{
    /// <summary>A person acting through the API.</summary>
    Human = 0,

    /// <summary>Deterministic code: a handler, a scheduled job, a calculator.</summary>
    DeterministicService = 1,

    /// <summary>A language-model-backed agent. None exist yet; agents arrive in Phase 4.</summary>
    AiAgent = 2,
}
