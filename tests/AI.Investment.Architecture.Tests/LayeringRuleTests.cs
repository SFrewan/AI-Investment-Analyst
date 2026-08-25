using System.Reflection;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// The layering and safety rules, as assertions rather than review comments.
/// </summary>
/// <remarks>
/// A dependency rule maintained by discipline holds until the first deadline. These tests fail
/// the build instead, which is the only version of the rule that survives contact with a project.
/// </remarks>
public sealed class LayeringRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(PolicyEngine).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ActionGateway).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;
    private static readonly Assembly ApiAssembly = typeof(AI.Investment.Api.Program).Assembly;

    /// <summary>
    /// The single most important rule in Clean Architecture, and the one the pre-Phase-0
    /// solution already satisfied. It is protected here rather than by memory.
    /// </summary>
    [Fact]
    public void Domain_depends_on_nothing_in_this_solution()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure",
                "AI.Investment.Api",
                "AI.Investment.Agents",
                "AI.Investment.Worker",
                "AI.Investment.Execution")
            .GetResult();

        AssertSuccess(result, "Domain must not reference any other project.");
    }

    /// <summary>
    /// The domain must not know that a database exists. If it did, persistence concerns would
    /// start shaping business rules.
    /// </summary>
    [Fact]
    public void Domain_does_not_reference_a_persistence_framework()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "System.Data")
            .GetResult();

        AssertSuccess(result, "Domain must not reference EF Core, Npgsql or ADO.NET.");
    }

    [Fact]
    public void Application_depends_only_on_Domain()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Infrastructure",
                "AI.Investment.Api",
                "Microsoft.EntityFrameworkCore",
                "Npgsql")
            .GetResult();

        AssertSuccess(result, "Application must not reference Infrastructure, the API or a persistence framework.");
    }

    /// <summary>
    /// The API references Infrastructure so the container can be wired. Composition is the only
    /// permitted contact; everything else goes through an Application abstraction.
    /// </summary>
    [Fact]
    public void Api_touches_Infrastructure_only_in_the_composition_root()
    {
        var offenders = Types.InAssembly(ApiAssembly)
            .That()
            .DoNotHaveName(nameof(AI.Investment.Api.Program))
            .Should()
            .NotHaveDependencyOn("AI.Investment.Infrastructure")
            .GetResult();

        AssertSuccess(
            offenders,
            "Only Program may use an Infrastructure type. Everything else must depend on an " +
            "Application abstraction.");
    }

    /// <summary>
    /// Phases not yet started must not have leaked in. Catches an accidental early dependency on
    /// agents or on a real execution plane.
    /// </summary>
    [Fact]
    public void No_ai_or_execution_dependency_has_been_introduced()
    {
        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly, ApiAssembly })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "AI.Investment.Agents",
                    "AI.Investment.Execution",
                    "Microsoft.Extensions.AI",
                    "OpenAI",
                    "Azure.AI",
                    "Anthropic",
                    "Microsoft.SemanticKernel")
                .GetResult();

            AssertSuccess(
                result,
                $"{assembly.GetName().Name} must not depend on an AI provider or an execution plane. " +
                "Agents are Phase 4; execution is gated behind its own approval.");
        }
    }

    /// <summary>
    /// Every entity configuration must be applied. A configuration class that is written but not
    /// picked up produces a table with convention defaults and no constraints - which usually
    /// surfaces as a production data-quality problem rather than a build failure.
    /// </summary>
    [Fact]
    public void Every_entity_configuration_is_sealed_and_discoverable()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .ImplementInterface(typeof(Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<>))
            .Should()
            .BeSealed()
            .And()
            .BePublic()
            .GetResult();

        AssertSuccess(result, "Entity configurations must be public and sealed so they are discovered by assembly scan.");
    }

    private static void AssertSuccess(TestResult result, string because)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var offenders = string.Join(
            Environment.NewLine + "  ",
            result.FailingTypeNames ?? []);

        Assert.Fail($"{because}{Environment.NewLine}Offending types:{Environment.NewLine}  {offenders}");
    }
}
