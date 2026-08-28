using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The calculator's argument guard.
/// </summary>
/// <remarks>
/// Separate from <see cref="RiskTierCalculatorTests"/>, which covers what the tiers are. This covers
/// what happens when the caller supplies nothing to compute from: the tier must not be computed at
/// all. Without the guard the call fails later with a null reference, which reads in a log as an
/// internal fault rather than as a caller error, and a risk tier is not something to be lenient
/// about.
/// </remarks>
public sealed class RiskTierCalculatorGuardTests
{
    [Fact]
    public void A_tier_cannot_be_computed_without_economics() =>
        Assert.Throws<ArgumentNullException>(() =>
            RiskTierCalculator.Calculate(Capability.SimulatedExecution, null!));
}
