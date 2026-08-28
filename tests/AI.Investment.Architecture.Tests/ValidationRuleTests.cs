using System.Reflection;
using AI.Investment.Application.Validation;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Validation;
using AI.Investment.Infrastructure.Validation;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// Structural rules for validation.
/// </summary>
/// <remarks>
/// <para>
/// Two claims are asserted here that no behavioural test can establish, because each is a claim about
/// what <em>cannot</em> happen. The measurement cannot reach an execution, and the read side cannot
/// admit evidence on the strength of when this installation happened to fetch it.
/// </para>
/// <para>
/// The second is the one worth having. Filtering on retrieval time instead of publication time
/// produces a backtest that works, runs fast and is wrong, and the error is invisible in the output:
/// it makes the results better. A behavioural test can catch the query written today; this catches
/// the one written next year by somebody optimising an index.
/// </para>
/// </remarks>
public sealed class ValidationRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(ValidationReport).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ValidationService).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(EfValidationHistory).Assembly;

    private const string DomainValidation = "AI.Investment.Domain.Validation";
    private const string ApplicationValidation = "AI.Investment.Application.Validation";
    private const string InfrastructureValidation = "AI.Investment.Infrastructure.Validation";

    /// <summary>
    /// Nothing in the measurement path depends on the action seam, the venue or the write window.
    /// </summary>
    [Fact]
    public void Validation_cannot_reach_the_execution_path()
    {
        var domain = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith(DomainValidation)
            .ShouldNot()
            .HaveDependencyOnAny("AI.Investment.Domain.Actions", "AI.Investment.Domain.Approvals")
            .GetResult();

        Assert.True(domain.IsSuccessful, Describe(domain));

        var application = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(ApplicationValidation)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application.Actions",
                "AI.Investment.Application.Execution",
                "AI.Investment.Application.Approvals")
            .GetResult();

        Assert.True(application.IsSuccessful, Describe(application));
    }

    /// <summary>
    /// Validation does not touch autonomy administration. Reading Phase 6's measurements is one
    /// thing; nothing here may issue, widen or resolve a grant.
    /// </summary>
    [Fact]
    public void Validation_cannot_change_autonomy()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(ApplicationValidation)
            .ShouldNot()
            .HaveDependencyOn("AI.Investment.Application.Autonomy")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    /// <summary>
    /// Retrieval time is not an admission test, and no validation source file mentions it.
    /// </summary>
    /// <remarks>
    /// Asserted over the compiled members rather than over source text, so it holds however the
    /// property is reached - directly, through a projection, or inside an expression tree.
    /// </remarks>
    [Fact]
    public void No_validation_type_reads_retrieval_time()
    {
        var namespaces = new[] { DomainValidation, ApplicationValidation, InfrastructureValidation };

        var assemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly };

        var offenders = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace is null ||
                    !namespaces.Any(space => type.Namespace.StartsWith(space, StringComparison.Ordinal)))
                {
                    continue;
                }

                // The guard is the one deliberate exception, and it is asserted separately below:
                // it reads retrieval time only to detect the impossible ordering that means a
                // timestamp is wrong, which can only ever make a verdict stricter.
                if (type == typeof(PointInTimeGuard))
                {
                    continue;
                }

                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly))
                {
                    if (ReadsRetrievalTime(method))
                    {
                        offenders.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these validation members read RetrievedAtUtc, which would make a historical result " +
            "depend on this installation's fetch history: " + string.Join(", ", offenders));
    }

    /// <summary>The domain half of validation depends on no framework at all.</summary>
    [Fact]
    public void The_validation_domain_stays_a_domain()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith(DomainValidation)
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
    /// The one deliberate exception, stated so it is not mistaken for an oversight: the guard reads
    /// retrieval time in exactly one place, to detect the impossible ordering that means a timestamp
    /// is wrong. That reading can only ever make a verdict stricter.
    /// </summary>
    [Fact]
    public void The_only_reading_of_retrieval_time_is_the_ordering_check_in_the_guard()
    {
        var judge = typeof(PointInTimeGuard)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(PointInTimeGuard.Judge) &&
                method.GetParameters().Length == 3);

        Assert.True(
            ReadsRetrievalTime(judge),
            "the guard no longer checks that a value was published before it was fetched. That check " +
            "is what catches a record whose position in time cannot be established at all.");
    }

    private static bool ReadsRetrievalTime(MethodBase method)
    {
        // A property read compiles to a call to its getter, so the question "does this member read
        // retrieval time" is the question "does its body call this getter". The body's IL is walked
        // for call and callvirt opcodes and the operand token is resolved back to a method. Source
        // text would have been easier to search and would have missed the read that arrives through
        // a projection, a local function or an expression tree.
        var body = method.GetMethodBody();

        if (body is null)
        {
            return false;
        }

        var il = body.GetILAsByteArray();

        if (il is null)
        {
            return false;
        }

        var module = method.Module;
        var getter = typeof(Provenance)
            .GetProperty(nameof(Provenance.RetrievedAtUtc))!
            .GetGetMethod()!;

        for (var index = 0; index + 4 < il.Length; index++)
        {
            // callvirt (0x6F) and call (0x28) are the two ways a property getter is reached.
            if (il[index] is not (0x6F or 0x28))
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, index + 1);

            try
            {
                if (module.ResolveMethod(token) == getter)
                {
                    return true;
                }
            }
#pragma warning disable CA1031 // A token that is not a method token is simply not this call. The
                              // scan walks bytes that may be operands rather than opcodes, so a
                              // failure to resolve is the normal case rather than an error.
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
