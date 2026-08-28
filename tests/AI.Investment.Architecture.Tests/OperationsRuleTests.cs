using System.Reflection;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Execution;
using AI.Investment.Application.Operations;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Watching;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// Structural rules for continuous operation.
/// </summary>
/// <remarks>
/// <para>
/// Three claims are asserted here that no behavioural test can establish, because each of them is a
/// claim about what <em>cannot</em> happen: nothing in the AI layer can reach a grant, nothing in
/// the shadow path can reach an effect, and nothing decides whether to wake the platform up by
/// asking a model.
/// </para>
/// <para>
/// Asserted by reflection over the built assemblies, so they fail on the reference rather than on
/// the eventual call - and so that they keep failing when somebody adds the reference for a reason
/// that seemed good at the time.
/// </para>
/// </remarks>
public sealed class OperationsRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(AutonomyGrant).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(OperatingCycleRunner).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    private const string DomainAutonomy = "AI.Investment.Domain.Autonomy";
    private const string DomainOperations = "AI.Investment.Domain.Operations";
    private const string DomainWatching = "AI.Investment.Domain.Watching";
    private const string DomainShadow = "AI.Investment.Domain.Shadow";
    private const string DomainAi = "AI.Investment.Domain.Ai";
    private const string ApplicationAi = "AI.Investment.Application.Ai";

    /// <summary>
    /// A grant is not in any agent's input schema and not in any agent's output schema, and the
    /// prohibition is structural rather than instructional.
    /// </summary>
    [Fact]
    public void The_ai_layer_cannot_reach_autonomy()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith(DomainAi)
            .ShouldNot()
            .HaveDependencyOn(DomainAutonomy)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));

        var application = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(ApplicationAi)
            .ShouldNot()
            .HaveDependencyOnAny(DomainAutonomy, "AI.Investment.Application.Autonomy")
            .GetResult();

        Assert.True(application.IsSuccessful, Describe(application));
    }

    /// <summary>
    /// And it cannot reach the operating loop either. An agent that could start a cycle could start
    /// as many as it liked, which is the budget and the backpressure gone in one step.
    /// </summary>
    [Fact]
    public void The_ai_layer_cannot_reach_the_operating_loop()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(ApplicationAi)
            .ShouldNot()
            .HaveDependencyOnAny(DomainOperations, DomainWatching, "AI.Investment.Application.Operations")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>
    /// A model must not be the thing deciding whether something is worth waking up for: it is both
    /// unreliable and unboundedly expensive, and the wake-up is what costs money.
    /// </summary>
    [Fact]
    public void Nothing_that_decides_to_wake_the_platform_up_depends_on_a_model()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith(DomainWatching)
            .ShouldNot()
            .HaveDependencyOnAny(DomainAi, ApplicationAi)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>
    /// Shadow mode measures and never acts, and the way that is guaranteed is that the shadow path
    /// has no execution surface to reach.
    /// </summary>
    [Fact]
    public void The_shadow_path_has_no_execution_surface()
    {
        var domain = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith(DomainShadow)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure")
            .GetResult();

        Assert.True(domain.IsSuccessful, Describe(domain));

        var forbidden = new[]
        {
            typeof(IActionGateway),
            typeof(IWriteAuthorization),
            typeof(IUnitOfWork),
            typeof(IExecutionVenue),
        };

        var shadowRecorder = typeof(ShadowRecorder);

        var reachable = shadowRecorder
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(reachable, type => forbidden.Contains(type));
    }

    /// <summary>
    /// The outbox is the only queue. A second messaging architecture would mean two things believing
    /// they had delivered one message.
    /// </summary>
    [Fact]
    public void There_is_exactly_one_queue()
    {
        var queues = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Where(type => typeof(IOutbox).IsAssignableFrom(type))
            .ToList();

        Assert.Single(queues);

        // And no message-broker client has crept in behind it.
        var brokerReferences = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly }
            .SelectMany(assembly => assembly.GetReferencedAssemblies())
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => BrokerMarkers.Any(marker =>
                name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(brokerReferences);
    }

    /// <summary>
    /// The domain still depends on nothing. Continuous operation added four namespaces to it and
    /// changed that not at all.
    /// </summary>
    [Fact]
    public void The_new_domain_namespaces_depend_on_no_framework()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain")
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Extensions",
                "Npgsql")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>
    /// Every stage a cycle can be in is in the declared order, so a stage added without updating the
    /// order cannot become a stage a cycle silently never reaches.
    /// </summary>
    [Fact]
    public void Every_cycle_stage_is_in_the_declared_order()
    {
        var declared = Enum.GetValues<CycleStage>()
            .Where(stage => stage != CycleStage.Unknown)
            .ToList();

        Assert.Equal(declared.Count, CycleStages.Ordered.Count);

        foreach (var stage in declared)
        {
            Assert.Contains(stage, CycleStages.Ordered);
        }
    }

    /// <summary>
    /// Every trigger type a watch can wait for is one the condition model can express, so a type
    /// added to the enum cannot become a watch that never fires.
    /// </summary>
    [Fact]
    public void Every_trigger_type_can_be_expressed_by_a_condition()
    {
        var types = Enum.GetValues<TriggerType>().Where(type => type != TriggerType.Unknown).ToList();

        Assert.NotEmpty(types);

        foreach (var type in types)
        {
            var watch = Watch.Create(
                "coverage",
                WatchTarget.Create("Security", "AAPL"),
                type,
                TriggerCondition.OnAnyObservation(),
                TimeSpan.FromMinutes(30),
                Domain.Enums.Capability.Analysis,
                "monitor",
                new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(type, watch.TriggerType);
        }
    }

    private static readonly string[] BrokerMarkers =
    [
        "MassTransit",
        "NServiceBus",
        "RabbitMQ",
        "Confluent.Kafka",
        "Azure.Messaging",
        "Amazon.SQS",
        "Hangfire",
        "Quartz",
    ];

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "no failing types were reported."
            : string.Join(", ", result.FailingTypeNames);
}
