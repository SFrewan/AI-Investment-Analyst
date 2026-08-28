using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Abstractions;

/// <summary>Thrown when a prompt an agent depends on is not present in the store.</summary>
/// <remarks>
/// A hard failure rather than a fallback to some default text. A missing prompt means the deployed
/// code and the deployed prompts disagree about what version exists, and an agent that quietly runs
/// on substitute instructions produces output nobody can reproduce or compare.
/// </remarks>
public sealed class PromptNotFoundException : Exception
{
    public PromptNotFoundException(PromptRef prompt)
        : base($"Prompt '{prompt}' was not found. A missing prompt is a deployment error, not a " +
               "condition to recover from: an agent running on substitute instructions produces " +
               "output that cannot be reproduced or compared with anything already stored.") =>
        Prompt = prompt;

    public PromptNotFoundException(PromptRef prompt, string message)
        : base(message) =>
        Prompt = prompt;

    public PromptNotFoundException(PromptRef prompt, string message, Exception innerException)
        : base(message, innerException) =>
        Prompt = prompt;

    public PromptRef? Prompt { get; }
}
