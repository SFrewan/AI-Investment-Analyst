using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.UnitTests.Autonomy;

/// <summary>Builders for the autonomy tests.</summary>
/// <remarks>
/// Everything here produces a valid, permissive grant, so each test breaks exactly one thing and the
/// name of the test says which. A fixture that quietly produced an already-expired or already-narrow
/// grant would make a safety test pass for the wrong reason.
/// </remarks>
internal static class AutonomyFixtures
{
    internal const string Environment = "Test";

    internal static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    internal static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    internal static AutonomyGrant Grant(
        Capability capability = Capability.SimulatedExecution,
        string? actionType = null,
        AutonomyMode mode = AutonomyMode.AutoExecuteBounded,
        RiskTier maxRiskTier = RiskTier.Medium,
        decimal maxExposure = 10_000m,
        string environment = Environment,
        DateTime? nowUtc = null,
        TimeSpan? validFor = null) =>
        AutonomyGrant.Issue(
            capability,
            actionType,
            environment,
            mode,
            maxRiskTier,
            Usd(maxExposure),
            "limits.default",
            "operator@example.test",
            nowUtc ?? Now,
            validFor ?? TimeSpan.FromDays(7));

    internal static AutonomyRequest Request(
        Capability capability = Capability.SimulatedExecution,
        string actionType = "execution.simulated-order",
        RiskTier riskTier = RiskTier.Medium,
        decimal exposure = 1_000m,
        string environment = Environment,
        string currency = "USD") =>
        AutonomyRequest.Create(
            capability,
            actionType,
            riskTier,
            Money.Create(exposure, currency),
            environment);
}
