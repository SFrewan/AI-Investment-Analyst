using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// Stable identifier of an analysis agent, such as <c>financial</c> or <c>risk</c>.
/// </summary>
/// <remarks>
/// <para>
/// A readable slug, for the same reason <see cref="SourceId"/> is one: this value is written into
/// the provenance of every interpretation the agent produces and read by a human asking why the
/// system believed something. It also becomes the agent's registered producer identity, so an
/// interpretation can be traced to the thing that made it after the code has moved on.
/// </para>
/// <para>
/// Deliberately narrow. An agent identifier is not a display name and must never carry a prompt
/// version, a model name or a run identifier: those change independently and are recorded
/// separately in <see cref="AgentDiagnostics"/>. Folding them together here would make every
/// historical comparison depend on none of them having changed.
/// </para>
/// </remarks>
public sealed record AgentId
{
    public const int MaxLength = 48;

    /// <summary>The prefix under which every agent registers as a producer of claims.</summary>
    public const string ProducerPrefix = "agent.";

    private AgentId(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// The agent's identity as a claim producer, for the provenance of what it interprets.
    /// </summary>
    public SourceId ProducerId => SourceId.Create(ProducerPrefix + Value);

    public static AgentId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                nameof(value),
                "An agent identifier is required. An interpretation whose author cannot be named " +
                "cannot be audited, compared across versions, or switched off.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"An agent identifier may not exceed {MaxLength} characters. Received '{value}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"An agent identifier may contain only lower-case letters, digits and '-'. " +
                    $"Received '{value}'.");
            }
        }

        if (normalised[0] == '-' || normalised[^1] == '-')
        {
            throw new DomainValidationException(
                nameof(value),
                $"An agent identifier may not begin or end with '-'. Received '{value}'.");
        }

        return new AgentId(normalised);
    }

    public override string ToString() => Value;
}
