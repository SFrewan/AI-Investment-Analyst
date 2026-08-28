using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Abstractions;

/// <summary>
/// The port through which this platform talks to a language model.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately owned rather than imported. The roadmap suggests abstracting on
/// <c>Microsoft.Extensions.AI</c>, and that remains the right adapter to write when a real
/// provider is wired up - but adopting its surface now would put a preview API, whose method names
/// have already changed once, underneath every agent in the system, and would add a package
/// dependency for a call this phase never makes. The architecture test that forbids an AI SDK in
/// any assembly therefore still passes, unweakened, and Phase 4 ships with zero new packages.
/// </para>
/// <para>
/// The port is narrow on purpose. It sends one request and gets one structured answer back. It
/// holds no tools, exposes no streaming, and offers no way for a model to call anything: agent
/// output is data, and control flow is C#.
/// </para>
/// </remarks>
public interface IChatModel
{
    /// <summary>The model this instance is pinned to, recorded on every result.</summary>
    ModelRef Model { get; }

    /// <summary>Sends one request. Never throws for a provider failure; it reports one.</summary>
    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
