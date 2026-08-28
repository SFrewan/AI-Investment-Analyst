using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// The five deterministic dimensions autonomy is resolved from.
/// </summary>
/// <remarks>
/// <para>
/// Exactly five, and every one of them is a fact about the action rather than a judgement about it.
/// Nothing here is supplied by a model: the capability and the action type are structural, the risk
/// tier is computed by <c>RiskTierCalculator</c>, the exposure comes from the proposal's economics,
/// and the environment comes from the host. A resolution is therefore reproducible from stored data,
/// which is what makes "why was this allowed to run unattended" a question with an answer.
/// </para>
/// </remarks>
public sealed record AutonomyRequest
{
    private AutonomyRequest(
        Capability capability,
        string actionType,
        RiskTier riskTier,
        Money exposure,
        string environmentName)
    {
        Capability = capability;
        ActionType = actionType;
        RiskTier = riskTier;
        Exposure = exposure;
        EnvironmentName = environmentName;
    }

    public Capability Capability { get; }

    public string ActionType { get; }

    public RiskTier RiskTier { get; }

    public Money Exposure { get; }

    public string EnvironmentName { get; }

    public static AutonomyRequest Create(
        Capability capability,
        string actionType,
        RiskTier riskTier,
        Money exposure,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(exposure);

        if (string.IsNullOrWhiteSpace(actionType))
        {
            throw new DomainValidationException(
                nameof(actionType),
                "An autonomy request names the action type it is asking about. Resolving without one " +
                "would silently answer for the whole capability.");
        }

        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new DomainValidationException(
                nameof(environmentName),
                "An autonomy request names its environment. A grant is per-environment, and resolving " +
                "without one would let a development permission answer in production.");
        }

        return new AutonomyRequest(
            capability,
            actionType.Trim(),
            riskTier,
            exposure,
            environmentName.Trim());
    }

    public override string ToString() =>
        $"{Capability}/{ActionType} [{RiskTier}, {Exposure}] @{EnvironmentName}";
}
