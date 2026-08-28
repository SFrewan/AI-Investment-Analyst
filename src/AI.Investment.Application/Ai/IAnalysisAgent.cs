using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai;

/// <summary>What every agent declares about itself, whatever it analyses.</summary>
/// <remarks>
/// Separate from the generic contract so that a pipeline can hold, audit and enumerate agents
/// without knowing their input and output types - and so that a registry of "what agents exist and
/// which prompt version is each on" is answerable without reflection.
/// </remarks>
public interface IAnalysisAgent
{
    AgentId AgentId { get; }

    /// <summary>The agent implementation's own version, distinct from the prompt's.</summary>
    string Version { get; }

    /// <summary>The prompt this agent runs on, by name and version.</summary>
    PromptRef Prompt { get; }

    /// <summary>How thoroughly this agent's output is checked against its evidence.</summary>
    GroundednessPolicy GroundednessPolicy { get; }
}

/// <summary>An agent that turns a structured input into a typed, grounded, audited answer.</summary>
/// <typeparam name="TInput">What the agent reads.</typeparam>
/// <typeparam name="TOutput">The structured answer, checkable against its evidence.</typeparam>
/// <remarks>
/// Two type parameters rather than one because the specialists and the synthesis agent read
/// different things: a specialist reads the evidence bundle, and synthesis reads specialist output
/// that has already been validated. Collapsing them would mean either that synthesis takes raw
/// agent output - which is how an ungrounded figure reaches the final narrative - or that the
/// specialists take a shape they do not need.
/// </remarks>
public interface IAnalysisAgent<in TInput, TOutput> : IAnalysisAgent
    where TOutput : class, IGroundedOutput
{
    Task<AgentResult<TOutput>> AnalyseAsync(
        TInput input,
        AnalysisBudget budget,
        CancellationToken cancellationToken = default);
}
