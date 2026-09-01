using AI.Investment.Application.Opportunities;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// That the screen states a probability of the event the validation run scores.
/// </summary>
/// <remarks>
/// <para>
/// The rule and the validation run each need a threshold: the screen counts how often a return beat
/// it, and the validation run judges whether a return beat it. When those were two separately
/// configured numbers they were free to describe two different events — and they did. Measured over
/// the stored year, the platform's own Brier score was 0.55 against the validation event and 0.11
/// against the rule's own. A validation report would have called that a badly calibrated model. It
/// was a mismatch.
/// </para>
/// <para>
/// So there is one number now, read once at composition and handed to both. This asserts that from
/// the real container rather than from the settings classes, because the failure mode being guarded
/// is a wiring failure: two correct types, wired to two different sources.
/// </para>
/// </remarks>
public sealed class EventAlignmentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public EventAlignmentTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void The_screen_and_the_validation_run_use_the_same_event_threshold()
    {
        using var scope = _factory.Services.CreateScope();

        var discovery = scope.ServiceProvider.GetRequiredService<DiscoverySettings>();
        var validation = scope.ServiceProvider.GetRequiredService<IOptions<ValidationOptions>>().Value;

        Assert.Equal(validation.EventThresholdRatio, discovery.Rule.EventThresholdRatio);
    }

    /// <summary>
    /// The threshold reaching the rule is the configured one, not the type's own default.
    /// </summary>
    /// <remarks>
    /// A default that happens to equal the configured value would make the test above pass while
    /// the wiring was absent. This asserts the value actually travelled: change the validation
    /// setting and the rule's copy moves with it.
    /// </remarks>
    [Fact]
    public void The_threshold_travels_from_configuration_rather_than_from_a_default()
    {
        var options = new DiscoveryOptions();

        Assert.Equal(0.125m, options.ToSettings(0.125m).Rule.EventThresholdRatio);
        Assert.Equal(-0.02m, options.ToSettings(-0.02m).Rule.EventThresholdRatio);
    }
}
