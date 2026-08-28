using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>
/// A chat model that answers from a script rather than a provider.
/// </summary>
/// <remarks>
/// <para>
/// Every test in this phase runs against this. That is not a compromise: an agent test that called
/// a real model would measure the model, cost money, need a credential, and give a different answer
/// next Tuesday. What these tests are for is the machinery around the model - schema enforcement,
/// the retry bound, groundedness, refusal, budget - and all of it is exercised precisely by being
/// able to say "the provider returns exactly this".
/// </para>
/// <para>
/// It also records what it was sent, which is how the tests check that evidence really was framed
/// as untrusted data and that the schema really was passed down.
/// </para>
/// </remarks>
public sealed class ScriptedChatModel : IChatModel
{
    private readonly Queue<ChatCompletion> _script;

    public ScriptedChatModel(params ChatCompletion[] script)
    {
        ArgumentNullException.ThrowIfNull(script);

        _script = new Queue<ChatCompletion>(script);
    }

    public ModelRef Model { get; init; } = ModelRef.Create("test", "scripted", "2026-01-01");

    /// <summary>What the model would return once the script runs out. Null repeats the last entry.</summary>
    public ChatCompletion? Fallback { get; init; }

    public List<ChatRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public Task<ChatCompletion> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Requests.Add(request);

        if (_script.Count > 0)
        {
            return Task.FromResult(_script.Dequeue());
        }

        return Task.FromResult(
            Fallback ?? ChatCompletion.Failed("The script is exhausted; the agent asked more times than expected."));
    }

    /// <summary>A model that answers the same thing every time - for stability measurement.</summary>
    public static ScriptedChatModel Always(string json) =>
        new() { Fallback = ChatCompletion.Ok(json, 100, 50, 0.0002m, 20) };
}

/// <summary>Prompts held in memory, so a test never depends on a file on disk.</summary>
public sealed class InMemoryPromptStore : IPromptStore
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>A store that answers with the same placeholder text for any prompt asked of it.</summary>
    public static InMemoryPromptStore Any(string text = "Test instructions.") =>
        new() { CatchAll = text };

    public string? CatchAll { get; init; }

    public InMemoryPromptStore With(PromptRef prompt, string text)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        _texts[prompt.ToString()] = text;

        return this;
    }

    public Task<PromptTemplate> GetAsync(PromptRef prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (_texts.TryGetValue(prompt.ToString(), out var text))
        {
            return Task.FromResult(PromptTemplate.Create(prompt, text));
        }

        return CatchAll is null
            ? throw new PromptNotFoundException(prompt)
            : Task.FromResult(PromptTemplate.Create(prompt, CatchAll));
    }
}
