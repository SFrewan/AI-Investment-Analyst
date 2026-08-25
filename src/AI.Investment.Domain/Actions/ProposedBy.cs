using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Actions;

/// <summary>
/// Who or what proposed an action.
/// </summary>
/// <remarks>
/// The policy engine reads <see cref="Kind"/> and treats an AI proposer differently, so this is
/// a safety-relevant field rather than a piece of audit colour. For an agent, the prompt
/// identity is recorded too: without it a historical decision cannot be reproduced, because the
/// prompt that produced it may since have changed.
/// </remarks>
public sealed record ProposedBy
{
    public const int MaxIdentifierLength = 120;

    private ProposedBy(ProposerKind kind, string id, string? version, string? promptId, string? promptVersion)
    {
        Kind = kind;
        Id = id;
        Version = version;
        PromptId = promptId;
        PromptVersion = promptVersion;
    }

    public ProposerKind Kind { get; }

    /// <summary>Identifier of the person, service or agent.</summary>
    public string Id { get; }

    /// <summary>Version of the service or agent, where it has one.</summary>
    public string? Version { get; }

    /// <summary>Prompt identifier, for an AI proposer only.</summary>
    public string? PromptId { get; }

    /// <summary>Prompt version, for an AI proposer only.</summary>
    public string? PromptVersion { get; }

    public bool IsAi => Kind == ProposerKind.AiAgent;

    /// <summary>A person acting through the API.</summary>
    public static ProposedBy Human(string userId) =>
        new(ProposerKind.Human, Validate(userId, nameof(userId)), null, null, null);

    /// <summary>Deterministic code: a handler, a scheduled job, a calculator.</summary>
    public static ProposedBy Service(string serviceId, string? version = null) =>
        new(ProposerKind.DeterministicService, Validate(serviceId, nameof(serviceId)), version, null, null);

    /// <summary>
    /// A language-model-backed agent. No agent exists in Phase 1; this factory is here so the
    /// policy engine's AI-specific rules can be tested against a real value rather than a mock.
    /// </summary>
    public static ProposedBy AiAgent(string agentId, string version, string promptId, string promptVersion)
    {
        if (string.IsNullOrWhiteSpace(promptId) || string.IsNullOrWhiteSpace(promptVersion))
        {
            throw new DomainValidationException(
                nameof(promptId),
                "An AI proposer must record the prompt identity and version. Without it the decision " +
                "cannot be reproduced once the prompt changes.");
        }

        return new ProposedBy(
            ProposerKind.AiAgent,
            Validate(agentId, nameof(agentId)),
            Validate(version, nameof(version)),
            promptId.Trim(),
            promptVersion.Trim());
    }

    public override string ToString() =>
        Version is null ? $"{Kind}:{Id}" : $"{Kind}:{Id}@{Version}";

    private static string Validate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, "A proposer identifier is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxIdentifierLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"A proposer identifier may not exceed {MaxIdentifierLength} characters.");
        }

        return trimmed;
    }
}
