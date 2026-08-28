using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Operations;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Operations;

/// <summary>
/// Backpressure. Without it, a market-wide event fans the platform out into thousands of
/// simultaneous cycles, an enormous bill, and a flood of escalations that trains the operator to
/// click through approvals without reading them.
/// </summary>
public sealed class AdmissionControlTests
{
    private static AdmissionLimits Limits() =>
        AdmissionLimits.Create(4, 2, 10, 6, TimeSpan.FromHours(1));

    private static AdmissionRequest Request(
        int running = 0,
        int runningForCapability = 0,
        int queued = 0,
        int firings = 0,
        Guid? watchId = null) =>
        new(Capability.Analysis, watchId ?? Guid.NewGuid(), running, runningForCapability, queued, firings);

    [Fact]
    public void An_idle_platform_admits_work()
    {
        var decision = AdmissionControl.Admit(Request(), Limits());

        Assert.True(decision.IsAdmitted);
        Assert.Equal(AdmissionRefusal.None, decision.Refusal);
    }

    /// <summary>
    /// A platform that cannot determine how much it is already doing must not do more.
    /// </summary>
    [Fact]
    public void Unreadable_limits_admit_nothing()
    {
        var decision = AdmissionControl.Admit(Request(), AdmissionLimits.FailClosed);

        Assert.False(decision.IsAdmitted);
        Assert.Equal(AdmissionRefusal.LimitsUnavailable, decision.Refusal);
        Assert.Contains("must not do more", decision.Explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 0, 0, 0, AdmissionRefusal.GlobalConcurrency)]
    [InlineData(0, 2, 0, 0, AdmissionRefusal.CapabilityConcurrency)]
    [InlineData(0, 0, 10, 0, AdmissionRefusal.QueueDepth)]
    [InlineData(0, 0, 0, 6, AdmissionRefusal.WatchFiringRate)]
    public void Every_ceiling_refuses_by_name(
        int running,
        int runningForCapability,
        int queued,
        int firings,
        AdmissionRefusal expected)
    {
        var decision = AdmissionControl.Admit(
            Request(running, runningForCapability, queued, firings),
            Limits());

        Assert.False(decision.IsAdmitted);
        Assert.Equal(expected, decision.Refusal);
        Assert.False(string.IsNullOrWhiteSpace(decision.Explanation));
    }

    /// <summary>
    /// The per-watch allowance is about one watch, so a manually started cycle - which has no watch -
    /// is not measured against it.
    /// </summary>
    [Fact]
    public void The_firing_allowance_applies_only_to_work_a_watch_started()
    {
        var decision = AdmissionControl.Admit(
            new AdmissionRequest(Capability.Analysis, null, 0, 0, 0, 100),
            Limits());

        Assert.True(decision.IsAdmitted);
    }

    /// <summary>
    /// The boundary is admitted up to but not including the ceiling, so a ceiling of four means four
    /// running and not five.
    /// </summary>
    [Fact]
    public void The_ceiling_is_the_first_refused_value()
    {
        Assert.True(AdmissionControl.Admit(Request(running: 3), Limits()).IsAdmitted);
        Assert.False(AdmissionControl.Admit(Request(running: 4), Limits()).IsAdmitted);
    }

    [Fact]
    public void Limits_that_admit_nothing_are_refused_at_construction()
    {
        Assert.Throws<DomainValidationException>(() =>
            AdmissionLimits.Create(0, 1, 1, 1, TimeSpan.FromHours(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionLimits.Create(1, 0, 1, 1, TimeSpan.FromHours(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionLimits.Create(1, 1, -1, 1, TimeSpan.FromHours(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionLimits.Create(1, 1, 1, 0, TimeSpan.FromHours(1)));

        Assert.Throws<DomainValidationException>(() =>
            AdmissionLimits.Create(1, 1, 1, 1, TimeSpan.Zero));
    }
}
