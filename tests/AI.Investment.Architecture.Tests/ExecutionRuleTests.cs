using System.Reflection;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Execution;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Infrastructure.Execution;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// Structural rules for the opportunity, approval, capital and execution layer.
/// </summary>
/// <remarks>
/// <para>
/// The rule that matters most is the first: <strong>every execution venue in this solution reports
/// itself simulated</strong>. Registering a live venue is meant to be a separate, formal decision
/// behind the validation gate, and a decision that can be taken by adding a class nobody notices is
/// not a gate. This test is what makes the claim in the composition root true rather than a comment.
/// </para>
/// <para>
/// The rules are asserted by reflection over the built assemblies rather than by reading source, so
/// they fail on the reference rather than on the eventual call.
/// </para>
/// </remarks>
public sealed class ExecutionRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(Opportunity).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(OpportunityExecutor).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    private static readonly string[] BrokerSdkMarkers =
    [
        "Alpaca",
        "InteractiveBrokers",
        "IBApi",
        "TDAmeritrade",
        "Binance",
        "Coinbase",
        "FIX",
        "QuickFix",
        "Tradier",
        "Polygon",
    ];

    [Fact]
    public void Every_execution_venue_in_the_solution_is_simulated()
    {
        var venues = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface)
            .Where(type => typeof(IExecutionVenue).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(venues);

        var live = venues.Where(type => type != typeof(SimulatedVenue)).Select(type => type.FullName!).ToList();

        Assert.True(
            live.Count == 0,
            "Only a simulated venue may exist until the live-venue gate is formally passed. " +
            $"Found: {string.Join(", ", live)}.");
    }

    [Fact]
    public void The_simulated_venue_declares_itself_simulated_and_cannot_be_overridden()
    {
        Assert.True(typeof(SimulatedVenue).IsSealed);

        var property = typeof(SimulatedVenue).GetProperty(nameof(IExecutionVenue.IsSimulated));

        Assert.NotNull(property);
        Assert.False(property!.CanWrite);
    }

    [Fact]
    public void No_assembly_references_a_broker_or_exchange_sdk()
    {
        var assemblies = new[] { DomainAssembly, ApplicationAssembly, InfrastructureAssembly };

        var offenders = assemblies
            .SelectMany(assembly => assembly
                .GetReferencedAssemblies()
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .Where(reference => BrokerSdkMarkers.Any(marker =>
                reference.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"No broker or exchange SDK may be referenced. Found: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void The_ai_layer_cannot_reach_the_opportunity_decision_path()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Application.Ai")
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application.Approvals",
                "AI.Investment.Application.Execution",
                "AI.Investment.Domain.Approvals",
                "AI.Investment.Domain.Limits",
                "AI.Investment.Domain.Capital")
            .GetResult();

        AssertSuccess(
            result,
            "An agent's output is data. Nothing in the AI layer may reference the approval, limit, " +
            "capital or execution machinery, because a reference is the first half of a call.");
    }

    [Fact]
    public void The_domain_decision_types_depend_on_nothing_outside_the_domain()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Opportunities")
            .Or()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Approvals")
            .Or()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Limits")
            .Or()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Capital")
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure",
                "AI.Investment.Api",
                "Microsoft.EntityFrameworkCore",
                "Npgsql")
            .GetResult();

        AssertSuccess(result, "The decision core must stay free of the layers above it.");
    }

    [Fact]
    public void The_limit_engine_and_the_ledger_are_pure_and_cannot_reach_outside_the_process()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Limits")
            .Or()
            .ResideInNamespaceStartingWith("AI.Investment.Domain.Capital")
            .ShouldNot()
            .HaveDependencyOnAny("System.Net", "System.Net.Http", "System.IO", "Microsoft.Extensions.Logging")
            .GetResult();

        AssertSuccess(
            result,
            "The checks standing between a defect and a loss must be exhaustively testable, which " +
            "means depending on nothing that has to be arranged first.");
    }

    [Fact]
    public void The_executor_reaches_the_world_only_through_application_abstractions()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith("AI.Investment.Application.Execution")
            .Or()
            .ResideInNamespaceStartingWith("AI.Investment.Application.Approvals")
            .Or()
            .ResideInNamespaceStartingWith("AI.Investment.Application.Opportunities")
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Infrastructure",
                "AI.Investment.Api",
                "Microsoft.EntityFrameworkCore",
                "Npgsql",
                "System.Net.Http")
            .GetResult();

        AssertSuccess(result, "The execution path must not know which database or venue it has.");
    }

    [Fact]
    public void The_action_gateway_is_the_only_way_the_executor_causes_an_effect()
    {
        var executor = typeof(OpportunityExecutor);

        var fields = executor
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToList();

        Assert.Contains(typeof(IActionGateway), fields);

        Assert.All(
            fields,
            type => Assert.True(
                type.IsInterface || type == typeof(IActionGateway),
                $"{executor.Name} holds a concrete dependency of type {type.Name}. Every effect it " +
                "can cause must arrive through an abstraction the composition root chose."));
    }

    [Fact]
    public void The_capital_ledger_exposes_no_way_to_set_a_balance()
    {
        var settable = typeof(LedgerEntry)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            settable.Count == 0,
            $"A ledger entry is immutable once posted. Publicly settable: {string.Join(", ", settable)}.");

        Assert.Null(typeof(CapitalLedger).GetMethod("SetBalance"));
    }

    [Fact]
    public void An_approval_token_exposes_no_public_setter()
    {
        var settable = typeof(ApprovalToken)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            settable.Count == 0,
            $"An approval is not editable after it is issued. Publicly settable: {string.Join(", ", settable)}.");
    }

    [Fact]
    public void Every_limit_kind_has_a_check_in_the_engine()
    {
        var covered = typeof(LimitEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToList();

        // One private Check* method per configurable kind, plus the currency guard.
        var expected = Enum.GetValues<LimitKind>().Count(kind => kind != LimitKind.Unknown);

        Assert.Equal(expected, covered.Count(name => name.StartsWith("Check", StringComparison.Ordinal)));
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
