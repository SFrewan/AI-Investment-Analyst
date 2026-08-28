namespace AI.Investment.Application.Ai.Evaluation;

/// <summary>What an evaluation case is testing.</summary>
/// <remarks>
/// Both directions matter, and a harness that only checked the first would be measuring the model
/// while ignoring the controls. <see cref="Rejected"/> cases carry evidence designed to tempt an
/// answer the validators must refuse; if the refusal rate on those ever reaches zero, the check has
/// stopped working, and that is not visible from the success rate on the others.
/// </remarks>
public enum EvaluationExpectation
{
    /// <summary>Never valid on a real case; present so the unset value names something.</summary>
    Unknown = 0,

    /// <summary>The agent should produce an answer that passes schema and groundedness.</summary>
    Grounded = 1,

    /// <summary>The agent's answer should be refused by the validators, or refused by the agent.</summary>
    Rejected = 2,
}
