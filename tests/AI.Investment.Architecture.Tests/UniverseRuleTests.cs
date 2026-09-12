using System.Reflection;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Universe;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// The boundaries of the D2 universe foundation.
/// </summary>
/// <remarks>
/// The failure modes here are specific and known: a "current universe" pointer that makes every
/// historical answer a present-tense one, a stored span that freezes a derived value as evidence, a
/// CIK arriving on the wrong aggregate, or the filesystem reader that the universe lives behind
/// today being promoted along with the model. Each is a rule.
/// </remarks>
public sealed class UniverseRuleTests
{
    private const string UniverseNamespace = "AI.Investment.Domain.Universe";

    private static readonly Assembly DomainAssembly = typeof(UniverseId).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    [Fact]
    public void The_universe_model_lives_in_the_domain()
    {
        Assert.Equal(DomainAssembly, typeof(Universe).Assembly);
        Assert.Equal(DomainAssembly, typeof(UniverseMembership).Assembly);
        Assert.Equal(UniverseNamespace, typeof(Universe).Namespace);
        Assert.Equal(UniverseNamespace, typeof(UniverseMembership).Namespace);
    }

    [Fact]
    public void The_universe_model_does_not_reach_the_application_infrastructure_or_api()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(UniverseNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure",
                "AI.Investment.Api")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Universe types depending outward: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_universe_model_reaches_no_filesystem_no_network_and_no_database()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(UniverseNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "System.IO",
                "System.Net",
                "System.Net.Http",
                "Npgsql",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Extensions.Logging")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "The universe lives behind a test-side filesystem reader today, and promoting the model "
                + "must not promote that: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_universe_model_holds_no_policy_no_execution_and_no_ai()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(UniverseNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Domain.Actions",
                "AI.Investment.Domain.Approvals",
                "AI.Investment.Domain.Autonomy",
                "AI.Investment.Domain.Limits",
                "AI.Investment.Domain.Ai",
                "AI.Investment.Domain.Shadow",
                "AI.Investment.Domain.Portfolio")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Universe membership must not decide, execute or infer anything: "
                + string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// The span stays derived: nothing in the universe model stores one.
    /// </summary>
    [Fact]
    public void No_membership_span_is_stored_anywhere_in_the_universe_model()
    {
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes().Where(t => t.Namespace == UniverseNamespace))
        {
            foreach (var property in type.GetProperties())
            {
                if (property.Name is "SpanFrom" or "SpanTo" or "Span" or "MembershipSpan"
                    || property.PropertyType.Name.Contains("MembershipSpan", StringComparison.Ordinal))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A stored span freezes a derived value as evidence and must be rewritten whenever the "
                + "derivation is corrected: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// No pointer to a "current" universe, on any type.
    /// </summary>
    /// <remarks>
    /// This is the single most dangerous field the model could grow. It would be read by everything
    /// that wanted an easy answer, and every historical question answered through it would silently
    /// become a question about today.
    /// </remarks>
    [Fact]
    public void No_current_universe_pointer_exists()
    {
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes().Where(t => t.Namespace == UniverseNamespace))
        {
            foreach (var property in type.GetProperties())
            {
                var name = property.Name;

                if (name.Contains("Current", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("IsActive", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("IsLatest", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Superseded", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.Name}.{name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Selecting a universe is ordering on the seal instant, never reading a flag: "
                + string.Join(", ", offenders));
    }

    /// <summary>
    /// CIK belongs to the legal entity, and the D2 decision keeps it off both models.
    /// </summary>
    [Fact]
    public void Cik_appears_on_neither_the_universe_model_nor_the_security_model()
    {
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes()
            .Where(t => t.Namespace is UniverseNamespace or "AI.Investment.Domain.Securities"))
        {
            offenders.AddRange(type.GetProperties()
                .Where(p => p.Name.Contains("Cik", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{type.Name}.{p.Name}"));
        }

        Assert.DoesNotContain(
            Enum.GetNames<SecurityIdentifierKind>(),
            k => k.Contains("Cik", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            offenders.Count == 0,
            "One CIK can issue several securities, so a CIK here would be ambiguous in exactly the "
                + "cases that matter: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Membership names a security, never a company.
    /// </summary>
    [Fact]
    public void Membership_identity_is_the_security_and_not_the_company()
    {
        Assert.NotNull(typeof(UniverseMembership).GetProperty(nameof(UniverseMembership.SecurityId)));

        Assert.DoesNotContain(
            typeof(UniverseMembership).GetProperties().Select(p => p.Name),
            n => n.Contains("Company", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Infrastructure_may_consume_the_universe_model()
    {
        var configured = InfrastructureAssembly.GetTypes()
            .Where(t => t.Name.EndsWith("Configuration", StringComparison.Ordinal))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericArguments().Any(a => a.Namespace == UniverseNamespace)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["UniverseConfiguration", "UniverseMembershipConfiguration"], configured);
    }
}
