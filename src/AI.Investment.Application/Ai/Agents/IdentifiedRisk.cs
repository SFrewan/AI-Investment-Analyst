using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>One risk the agent identified, with how serious it judges it to be.</summary>
public sealed record IdentifiedRisk
{
    public const int MaxDescriptionLength = 400;

    private IdentifiedRisk(string description, RiskSeverity severity)
    {
        Description = description;
        Severity = severity;
    }

    public string Description { get; }

    public RiskSeverity Severity { get; }

    public static IdentifiedRisk Create(string description, RiskSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new DomainValidationException(nameof(description), "A risk must be described.");
        }

        if (severity == RiskSeverity.Unknown)
        {
            throw new DomainValidationException(
                nameof(severity),
                "A risk must state a severity. 'Unknown' is the unset value, and a risk recorded " +
                "without a severity sorts as though it were the mildest.");
        }

        var trimmed = description.Trim();

        return new IdentifiedRisk(
            trimmed.Length <= MaxDescriptionLength ? trimmed : trimmed[..MaxDescriptionLength],
            severity);
    }

    public override string ToString() => $"[{Severity}] {Description}";
}
