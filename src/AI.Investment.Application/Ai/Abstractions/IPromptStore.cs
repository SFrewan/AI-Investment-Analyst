using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Abstractions;

/// <summary>Resolves a versioned prompt to its text.</summary>
/// <remarks>
/// There is no method to write a prompt, and there will not be one. A prompt change is a code
/// change: it goes through the same review as any other, because it silently invalidates every
/// historical comparison unless the version moves with it. A store that could be written to at run
/// time would let the system alter what it asks itself, which is the self-modification this
/// platform refuses on principle.
/// </remarks>
public interface IPromptStore
{
    /// <summary>Returns the prompt, or throws <see cref="PromptNotFoundException"/>.</summary>
    Task<PromptTemplate> GetAsync(PromptRef prompt, CancellationToken cancellationToken = default);
}
