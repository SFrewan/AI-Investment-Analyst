using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Abstractions;

/// <summary>One call to a language model: instructions, evidence, and the shape of the answer.</summary>
/// <remarks>
/// <para>
/// <see cref="Evidence"/> is untrusted input. Filings and news are written by people who may have
/// an interest in what this system concludes, and text that reaches a model is text that can try to
/// instruct it. The renderer therefore delimits and labels it as data, the schema constrains what
/// can come back, groundedness checks what did, and nothing an agent says can start an action.
/// Four independent barriers, because any one of them can be argued around.
/// </para>
/// <para>
/// <see cref="Temperature"/> defaults to zero. Extraction and classification have correct answers,
/// and sampling variety into them buys nothing but an unreproducible result.
/// </para>
/// </remarks>
public sealed record ChatRequest
{
    public const int MinOutputTokens = 64;

    private ChatRequest(
        PromptRef prompt,
        string instructions,
        string evidence,
        string responseSchema,
        decimal temperature,
        int maxOutputTokens)
    {
        Prompt = prompt;
        Instructions = instructions;
        Evidence = evidence;
        ResponseSchema = responseSchema;
        Temperature = temperature;
        MaxOutputTokens = maxOutputTokens;
    }

    public PromptRef Prompt { get; }

    /// <summary>The versioned prompt text.</summary>
    public string Instructions { get; }

    /// <summary>The rendered evidence bundle, delimited and labelled as untrusted data.</summary>
    public string Evidence { get; }

    /// <summary>The JSON schema the answer must satisfy.</summary>
    public string ResponseSchema { get; }

    public decimal Temperature { get; }

    public int MaxOutputTokens { get; }

    public static ChatRequest Create(
        PromptRef prompt,
        string instructions,
        string evidence,
        string responseSchema,
        int maxOutputTokens,
        decimal temperature = 0m)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (string.IsNullOrWhiteSpace(instructions))
        {
            throw new DomainValidationException(nameof(instructions), "A request must carry its prompt text.");
        }

        if (string.IsNullOrWhiteSpace(evidence))
        {
            throw new DomainValidationException(
                nameof(evidence),
                "A request must carry evidence. A model asked to analyse nothing will answer from " +
                "memory, and there is no way to tell that answer from a grounded one.");
        }

        if (string.IsNullOrWhiteSpace(responseSchema))
        {
            throw new DomainValidationException(
                nameof(responseSchema),
                "A request must state the schema of the answer. Free text has no place between " +
                "components of this system.");
        }

        if (temperature is < 0m or > 1m)
        {
            throw new DomainValidationException(nameof(temperature), "Temperature must be between 0 and 1.");
        }

        if (maxOutputTokens < MinOutputTokens)
        {
            throw new DomainValidationException(
                nameof(maxOutputTokens),
                $"An output budget below {MinOutputTokens} tokens truncates the answer, which arrives " +
                "as a schema failure and reads like a model defect.");
        }

        return new ChatRequest(
            prompt,
            instructions,
            evidence,
            responseSchema,
            temperature,
            maxOutputTokens);
    }
}
