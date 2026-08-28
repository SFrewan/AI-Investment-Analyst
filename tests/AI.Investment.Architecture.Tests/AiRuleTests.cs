using System.Reflection;
using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Pipeline;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// Structural rules for the AI layer.
/// </summary>
/// <remarks>
/// The existing rule forbidding an AI provider SDK in any assembly is deliberately left in force
/// rather than relaxed for this phase. Phase 4 adds no such package: the chat port is owned by this
/// codebase, and the adapter that talks to a paid provider belongs to the phase that decides to
/// spend money. A rule that says "no AI SDK has crept in" is worth keeping true.
/// </remarks>
public sealed class AiRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(EvidenceBundle).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(AnalysisPipeline).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    /// <summary>
    /// The vocabulary an interpretation is expressed in belongs to the domain, so that the rules
    /// about what a judgement must carry cannot be bypassed by talking to a model directly.
    /// </summary>
    [Fact]
    public void The_ai_vocabulary_and_the_groundedness_check_live_in_the_domain()
    {
        Assert.Equal(DomainAssembly, typeof(AgentResult).Assembly);
        Assert.Equal(DomainAssembly, typeof(EvidenceBundle).Assembly);
        Assert.Equal(DomainAssembly, typeof(GroundednessValidator).Assembly);
        Assert.Equal(DomainAssembly, typeof(AgentStatus).Assembly);
    }

    /// <summary>
    /// Deciding what runs next is deterministic code in the application layer. A domain that
    /// orchestrated agents would be a domain that needs a provider to be testable.
    /// </summary>
    [Fact]
    public void Orchestration_and_the_agents_live_in_the_application_layer()
    {
        Assert.Equal(ApplicationAssembly, typeof(AnalysisPipeline).Assembly);
        Assert.Equal(ApplicationAssembly, typeof(IChatModel).Assembly);
        Assert.Equal(ApplicationAssembly, typeof(IAnalysisAgent).Assembly);
    }

    /// <summary>
    /// An implementation that actually talks to something is Infrastructure, for the same reason a
    /// connector is: it is the layer allowed to reach outside the process.
    /// </summary>
    [Fact]
    public void Chat_model_implementations_live_only_in_infrastructure()
    {
        AssertNoImplementationsIn<IChatModel>(DomainAssembly, ApplicationAssembly);
        AssertNoImplementationsIn<IPromptStore>(DomainAssembly, ApplicationAssembly);
    }

    /// <summary>
    /// The AI layer must not become the exception to the dependency rule, whatever convenience is
    /// on offer.
    /// </summary>
    [Fact]
    public void The_domain_ai_namespace_depends_on_nothing_outside_the_domain()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Ai")
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure",
                "AI.Investment.Api")
            .GetResult();

        AssertSuccess(result, "The AI vocabulary must not depend outward.");
    }

    /// <summary>
    /// Agents are configuration of one machinery, not a place to re-implement it. Sealing them says
    /// so: an agent that could be subclassed is an agent whose validation could be overridden.
    /// </summary>
    [Fact]
    public void Every_shipped_agent_is_sealed()
    {
        var agents = ApplicationAssembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IAnalysisAgent).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(agents);
        Assert.All(agents, type => Assert.True(type.IsSealed, $"{type.Name} must be sealed."));
    }

    /// <summary>
    /// Everything an agent can return has to be checkable against its evidence. An output type that
    /// did not implement the contract would be one nothing could validate.
    /// </summary>
    [Fact]
    public void Every_agent_output_type_can_be_checked_for_groundedness()
    {
        var outputs = ApplicationAssembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Where(type => type.Namespace is not null &&
                           type.Namespace.StartsWith("AI.Investment.Application.Ai.Agents", StringComparison.Ordinal))
            .Where(type => type.Name.EndsWith("Reading", StringComparison.Ordinal) ||
                           type.Name.EndsWith("Assessment", StringComparison.Ordinal) ||
                           type.Name.EndsWith("Synthesis", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(outputs);

        foreach (var type in outputs)
        {
            Assert.True(
                typeof(IGroundedOutput).IsAssignableFrom(type),
                $"{type.Name} is an agent output and must implement IGroundedOutput.");
        }
    }

    /// <summary>
    /// The AI layer reaches nothing. The existing rules say this of Domain and Application as a
    /// whole; stating it again for the AI namespaces makes the failure message name the right thing
    /// when somebody adds a client "just to fetch one article".
    /// </summary>
    [Fact]
    public void The_ai_layer_cannot_reach_the_network_or_the_database()
    {
        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly })
        {
            var result = Types.InAssembly(assembly)
                .That()
                .ResideInNamespaceContaining(".Ai")
                .ShouldNot()
                .HaveDependencyOnAny(
                    "System.Net",
                    "System.Net.Http",
                    "Microsoft.EntityFrameworkCore",
                    "Npgsql")
                .GetResult();

            AssertSuccess(
                result,
                $"{assembly.GetName().Name}'s AI namespaces must not reach outside the process.");
        }
    }

    [Fact]
    public void The_prompt_store_and_the_refusing_model_are_infrastructure()
    {
        var implementations = InfrastructureAssembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IChatModel).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(implementations);
    }

    private static void AssertNoImplementationsIn<TContract>(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var offenders = assembly.GetTypes()
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .Where(type => typeof(TContract).IsAssignableFrom(type))
                .Select(type => type.FullName!)
                .ToList();

            Assert.Empty(offenders);
        }
    }

    private static void AssertSuccess(TestResult result, string because)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var offenders = string.Join(", ", result.FailingTypeNames ?? []);

        Assert.Fail($"{because} Offending types: {offenders}");
    }
}
