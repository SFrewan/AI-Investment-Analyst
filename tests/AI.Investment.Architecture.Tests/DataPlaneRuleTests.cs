using System.Globalization;
using System.Reflection;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// The data plane's structural invariants, as assertions rather than review comments.
/// </summary>
/// <remarks>
/// <para>
/// Stages 1 to 9 established a set of properties that are easy to state and easy to erode: the
/// network is reached in one layer, evidence is destroyed by one component, an unset enum is always
/// a named value, and every mapped aggregate can actually be materialised. Each is currently true
/// because somebody was careful. These make each one true because the build says so.
/// </para>
/// <para>
/// Some are expressed with NetArchTest and some with plain reflection. The split is not stylistic:
/// dependency rules are what NetArchTest is for, and "every enum has a member for zero" is a
/// question about metadata that reads far more clearly as a loop than as a fluent chain.
/// </para>
/// </remarks>
public sealed class DataPlaneRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(PolicyEngine).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(ActionGateway).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    // ---------- the network is reached in exactly one layer ----------

    /// <summary>
    /// Nothing in the domain fetches anything.
    /// </summary>
    /// <remarks>
    /// A domain that could make a request would make business rules depend on a network being up,
    /// and would put the one thing that must be replayable years later behind something that will
    /// not answer the same way twice.
    /// </remarks>
    [Fact]
    public void Domain_cannot_reach_the_network()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("System.Net", "System.Net.Http", "System.Net.Sockets")
            .GetResult();

        AssertSuccess(result, "The domain must not be able to fetch anything.");
    }

    /// <summary>
    /// Neither does the application layer.
    /// </summary>
    /// <remarks>
    /// This is the rule that keeps the ingestion gateway meaningful. An application service holding
    /// an <c>HttpClient</c> would bypass source admission, provider capability checking, the rate
    /// limiter, the archive and the ledger in one step - every gate the data plane has - and it
    /// would look entirely ordinary while doing it. Connectors are Infrastructure.
    /// </remarks>
    [Fact]
    public void Application_cannot_reach_the_network()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("System.Net", "System.Net.Http", "System.Net.Sockets")
            .GetResult();

        AssertSuccess(
            result,
            "Only Infrastructure connectors may make requests. An Application service with an " +
            "HttpClient bypasses admission, rate limiting, the archive and the ledger at once.");
    }

    /// <summary>
    /// Every connector and every normaliser lives in Infrastructure.
    /// </summary>
    /// <remarks>
    /// The dependency rule above says the network cannot be reached from the inner layers; this
    /// says the same thing from the other direction, and catches the case where an implementation
    /// is written inside Application with a stub body and grown into a real one later.
    /// </remarks>
    [Fact]
    public void Connectors_and_normalisers_live_only_in_Infrastructure()
    {
        AssertNoImplementationsIn<IDataProvider>(DomainAssembly, ApplicationAssembly);
        AssertNoImplementationsIn<INormalizer>(DomainAssembly, ApplicationAssembly);
    }

    // ---------- scheduling is a host concern ----------

    /// <summary>
    /// Deciding <em>when</em> something runs belongs to the host, not to the layers that decide
    /// what running means.
    /// </summary>
    /// <remarks>
    /// The retention sweep is the case that makes this concrete. It knows how to walk the archive
    /// and nothing about timers; the timer lives in the API's hosted service. Had the two been one
    /// type, the rule that destroys evidence could not be exercised without a clock.
    /// </remarks>
    [Fact]
    public void Domain_and_Application_do_not_schedule_themselves()
    {
        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "Microsoft.Extensions.Hosting",
                    "System.Threading.PeriodicTimer",
                    "System.Timers")
                .GetResult();

            AssertSuccess(
                result,
                $"{assembly.GetName().Name} must not decide when work runs. That is the host's job, " +
                "and keeping it there is what lets these layers be tested without a clock.");
        }
    }

    /// <summary>
    /// The inner layers do not write to a log or a console directly.
    /// </summary>
    /// <remarks>
    /// Not a style rule. A domain rule that logged would be a domain rule with a side effect, and
    /// the point of keeping the policy engine, the retention policy and the freshness policy pure
    /// is that their conclusions can be reconstructed from their inputs alone.
    /// </remarks>
    [Fact]
    public void Domain_does_not_log()
    {
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.Extensions.Logging", "Serilog", "System.Diagnostics.Trace")
            .GetResult();

        AssertSuccess(result, "A pure decision must stay pure. Reporting it is somebody else's job.");
    }

    // ---------- an unset value is never an undefined one ----------

    /// <summary>
    /// Every enum in the domain and the application defines a member for zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default(T)</c> for an enum is zero whether or not zero is declared, so an enum without a
    /// zero member produces a value that is not any of its cases and still passes every switch that
    /// has a <c>default</c> branch. This platform leans hard on the meaning of an unset value -
    /// <c>KillSwitchState.Unknown</c> denies, <c>ObservationValueKind.Unknown</c> refuses to be
    /// read, <c>PolicyOutcome.Deny</c> is the default outcome - and all of that reasoning assumes
    /// the default is a case somebody chose.
    /// </para>
    /// <para>
    /// This does not assert <em>which</em> member is zero, because the right answer differs. Most
    /// choose the safe or unknown case; <c>RetentionOutcome.Retain</c> is deliberately zero because
    /// there the irreversible operation is the deletion. What must never happen is zero belonging
    /// to nobody.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_enum_defines_a_member_for_its_default_value()
    {
        var offenders = new List<string>();

        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly })
        {
            foreach (var type in assembly.GetTypes().Where(t => t.IsEnum))
            {
                if (!Enum.GetValues(type).Cast<object>().Any(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) == 0))
                {
                    offenders.Add($"{type.FullName} has no member equal to 0");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Every enum must name its default, or default(T) is a value that is none of its cases:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ---------- everything mapped can actually be materialised ----------

    /// <summary>
    /// Every aggregate root has a parameterless constructor for the persistence provider.
    /// </summary>
    /// <remarks>
    /// EF materialises through a constructor, and an aggregate that protects its invariants with a
    /// single validating factory has no constructor EF can use unless one is written for it. The
    /// failure mode is the reason this is a test: the build succeeds, the migration succeeds, and
    /// the first query against that table throws - which on a data plane may be a scheduled job
    /// at 3am rather than a developer at a keyboard.
    /// </remarks>
    [Fact]
    public void Every_aggregate_root_can_be_materialised_by_the_persistence_provider()
    {
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes().Where(IsAggregateRoot))
        {
            var parameterless = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);

            if (parameterless is null)
            {
                offenders.Add($"{type.FullName} has no parameterless constructor");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "An aggregate EF cannot construct fails at the first query, not at build time:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every entity that has a configuration is reachable as a <c>DbSet</c>.
    /// </summary>
    /// <remarks>
    /// A configuration whose entity has no <c>DbSet</c> still shapes the table, so nothing looks
    /// wrong - but the stores reach their tables through the context's properties, so the entity is
    /// mapped and unusable. Written after stage 6 added two configurations and two <c>DbSet</c>s in
    /// separate edits, which is exactly the shape of change where one of them gets forgotten.
    /// </remarks>
    [Fact]
    public void Every_configured_entity_is_exposed_as_a_DbSet()
    {
        var configured = InfrastructureAssembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .ToList();

        var exposed = typeof(AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToHashSet();

        var missing = configured.Where(t => !exposed.Contains(t)).Select(t => t.Name).ToList();

        Assert.True(
            configured.Count > 0,
            "No entity configurations were found. This test would then prove nothing.");

        Assert.True(
            missing.Count == 0,
            "These entities are configured but have no DbSet, so nothing can reach them: " +
            string.Join(", ", missing));
    }

    // ---------- helpers ----------

    private static bool IsAggregateRoot(Type type)
    {
        if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
        {
            return false;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertNoImplementationsIn<TInterface>(params Assembly[] assemblies)
    {
        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => typeof(TInterface).IsAssignableFrom(t))
            .Select(t => $"{t.FullName} in {t.Assembly.GetName().Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{typeof(TInterface).Name} may only be implemented in Infrastructure: " +
            string.Join(", ", offenders));
    }

    private static void AssertSuccess(TestResult result, string because)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var failing = result.FailingTypeNames ?? [];

        Assert.Fail($"{because}{Environment.NewLine}Offending types: {string.Join(", ", failing)}");
    }
}
