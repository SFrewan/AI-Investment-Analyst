using System.Reflection;
using AI.Investment.Application.Autonomy;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// Structural rules for bounded autonomy.
/// </summary>
/// <remarks>
/// <para>
/// The promotion gate lives on one method of one class, and that is a design decision worth
/// defending mechanically. It is only a gate if it is the only door: a second production type calling
/// the grant factory would create a path to unattended execution that nobody had to argue for, and
/// the code review that let it through would look like an ordinary refactor.
/// </para>
/// <para>
/// The other rules here are the ones a behavioural test cannot reach: that the AI layer cannot see a
/// warrant, and that nothing outside the execution plane can hold a venue.
/// </para>
/// </remarks>
public sealed class BoundedAutonomyRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(AutonomyGrant).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(PromotionService).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    /// <summary>
    /// The one door. Only the administration service writes a grant, so the gate on that service is
    /// a gate on grants.
    /// </summary>
    [Fact]
    public void Only_the_administration_service_writes_an_autonomy_grant()
    {
        var factories = typeof(AutonomyGrant)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(AutonomyGrant))
            .ToList();

        Assert.Equal(2, factories.Count);

        var callers = new List<string>();

        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly })
        {
            foreach (var type in assembly.GetTypes())
            {
                // The administration service and the compiler-generated state machines and closures
                // the compiler emits inside it. Those carry the body of GrantAsync and are named
                // after it, so excluding the outer type alone would exclude nothing that matters.
                if (type == typeof(AutonomyGrant) ||
                    type == typeof(AutonomyAdministration) ||
                    (type.FullName ?? string.Empty).StartsWith(
                        typeof(AutonomyAdministration).FullName + "+", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (factories.Any(factory => Calls(method, factory)))
                    {
                        callers.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        Assert.True(
            callers.Count == 0,
            "these production members write an autonomy grant without going through the promotion " +
            "gate on AutonomyAdministration: " + string.Join(", ", callers));
    }

    /// <summary>
    /// A warrant is not in any agent's input schema and not in any agent's output schema, and the
    /// prohibition is a missing reference rather than an instruction.
    /// </summary>
    [Fact]
    public void The_ai_layer_cannot_reach_a_promotion_warrant()
    {
        var domain = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Ai")
            .ShouldNot()
            .HaveDependencyOn("AI.Investment.Domain.Autonomy")
            .GetResult();

        Assert.True(domain.IsSuccessful, Describe(domain));

        var application = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Application.Ai")
            .ShouldNot()
            .HaveDependencyOnAny("AI.Investment.Domain.Autonomy", "AI.Investment.Application.Autonomy")
            .GetResult();

        Assert.True(application.IsSuccessful, Describe(application));
    }

    /// <summary>
    /// The autonomy domain depends on the validation domain and on nothing that executes. Promotion
    /// is argued from measurements; it does not reach the thing it would be permitting.
    /// </summary>
    [Fact]
    public void The_autonomy_domain_cannot_reach_the_execution_path()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Autonomy")
            .ShouldNot()
            .HaveDependencyOnAny("AI.Investment.Domain.Approvals", "AI.Investment.Domain.Capital")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>The autonomy domain stays a domain: no framework, no database, no HTTP.</summary>
    [Fact]
    public void The_autonomy_domain_has_no_infrastructure()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Autonomy")
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Extensions",
                "Npgsql",
                "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>
    /// Whether one member calls another, read from the compiled body.
    /// </summary>
    /// <remarks>
    /// The same technique the validation rules use to prove that retrieval time is never an admission
    /// test. Source text would have been easier to search and would have missed a call that arrives
    /// through a local function or an expression tree.
    /// </remarks>
    private static bool Calls(MethodBase method, MethodBase target)
    {
        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();

        if (il is null)
        {
            return false;
        }

        for (var index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] is not (0x6F or 0x28))
            {
                continue;
            }

            try
            {
                if (method.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1)) == target)
                {
                    return true;
                }
            }
#pragma warning disable CA1031 // The scan walks bytes that may be operands rather than opcodes, so a
                              // token that does not resolve to a method is the normal case rather
                              // than an error.
            catch (Exception)
            {
                continue;
            }
#pragma warning restore CA1031
        }

        return false;
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "no failing types were reported."
            : string.Join(", ", result.FailingTypeNames);
}
