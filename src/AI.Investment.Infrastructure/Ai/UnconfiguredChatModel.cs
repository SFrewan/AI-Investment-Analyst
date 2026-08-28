using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Infrastructure.Ai;

/// <summary>
/// The chat model the system runs on when no provider has been configured: it refuses, every time.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the default so that the absence of a provider is a visible, audited refusal rather
/// than a gap somebody fills later with a convenience. The alternative - throwing on resolve, or
/// leaving the dependency unregistered - fails at startup, which sounds stricter and is worse: the
/// platform's job is to come up and decline, where an operator can see it, not to fail to come up.
/// </para>
/// <para>
/// This is the fail-closed default in the same sense as an unknown kill-switch state. There is no
/// path through this type that produces an answer, so a misconfiguration can never be mistaken for
/// a working AI layer producing unusually terse analyses.
/// </para>
/// <para>
/// Phase 4 ships no real provider on purpose. Calling one costs money, needs a credential, and
/// makes every test depend on a network and on somebody else's model version. The port exists, the
/// agents run against it, and the adapter that talks to a paid provider is written when the phase
/// that needs it arrives.
/// </para>
/// </remarks>
public sealed class UnconfiguredChatModel : IChatModel
{
    public const string Reason =
        "No language-model provider is configured. The AI layer fails closed: it declines to " +
        "answer rather than producing an analysis that rests on nothing.";

    public ModelRef Model => ModelRef.None;

    public Task<ChatCompletion> CompleteAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(ChatCompletion.Failed(Reason));
    }
}
