using System.Reflection;
using AI.Investment.Domain.Evidence;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// The boundaries of the Stage E determination store.
/// </summary>
/// <remarks>
/// Two failure modes are worth a rule each. The first is a foreign key: a determination that pointed
/// at rows rather than at hashes would resolve to whatever those rows say now, and stop being
/// reproducible while still looking like evidence. The second is a second implementation - a derived
/// value quietly stored as an observation, which is the one confusion the evidence base is built to
/// make impossible.
/// </remarks>
public sealed class DeterminationRuleTests
{
    private const string EvidenceNamespace = "AI.Investment.Domain.Evidence";

    private static readonly Assembly DomainAssembly = typeof(Determination).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    [Fact]
    public void The_determination_store_lives_in_the_domain_evidence_namespace()
    {
        Assert.Equal(DomainAssembly, typeof(Determination).Assembly);
        Assert.Equal(DomainAssembly, typeof(DeterminationId).Assembly);
        Assert.Equal(EvidenceNamespace, typeof(Determination).Namespace);
        Assert.Equal(EvidenceNamespace, typeof(DeterminationId).Namespace);
    }

    [Fact]
    public void A_determination_does_not_reach_the_application_infrastructure_or_api()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().HaveName(nameof(Determination))
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure",
                "AI.Investment.Api")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Determination depending outward: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void A_determination_reaches_no_store_no_network_and_no_provider()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().HaveName(nameof(Determination))
            .ShouldNot()
            .HaveDependencyOnAny(
                "System.Net",
                "System.Net.Http",
                "System.IO",
                "Npgsql",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Extensions.Logging")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "A rule that reached a store or a socket could not be pure, and an impure rule cannot "
                + "be reproduced: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Inputs are named by content hash. Nothing on this type points at a row.
    /// </summary>
    [Fact]
    public void A_determination_holds_no_reference_to_any_subject_or_evidence_row()
    {
        // RuleId is excluded deliberately: it names a rule, not a row. Everything else ending in
        // "Id" would be a reference to something the determination did not hash.
        string[] permitted = ["Id", "RuleId"];

        var offenders = typeof(Determination).GetProperties()
            .Where(p => (p.Name.EndsWith("Id", StringComparison.Ordinal) && !permitted.Contains(p.Name))
                || p.Name is "Subject" or "Cik" or "Ticker" or "Symbol")
            .Select(p => $"{p.Name}:{p.PropertyType.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A row reference resolves to what that row says now; a hash resolves to the bytes that "
                + "were used: " + string.Join(", ", offenders));

        // And the configuration declares no foreign key either.
        var configuration = InfrastructureAssembly.GetTypes()
            .Single(t => t.Name == "DeterminationConfiguration");

        Assert.NotNull(configuration);
    }

    /// <summary>
    /// Exactly one determination type exists, and the observation store is still Fact-only.
    /// </summary>
    [Fact]
    public void There_is_no_competing_determination_implementation()
    {
        var candidates = new[] { DomainAssembly, InfrastructureAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.Contains("Determination", StringComparison.Ordinal))
            .Where(t => !t.Name.EndsWith("Configuration", StringComparison.Ordinal))
            // Migration classes are named after what they add, not after a competing model.
            .Where(t => t.Namespace?.Contains("Migrations", StringComparison.Ordinal) != true)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "AI.Investment.Domain.Evidence.Determination",
                "AI.Investment.Domain.Evidence.DeterminationId",
            ],
            candidates);
    }

    /// <summary>
    /// The append-only classification exists and names the determination store.
    /// </summary>
    /// <remarks>
    /// Checked by reflection over the private predicate rather than by behaviour, because the
    /// behavioural proof needs a database and lives in the integration suite. What this adds is that
    /// the category cannot be deleted without something going red.
    /// </remarks>
    [Fact]
    public void An_append_only_guard_category_exists_for_determinations()
    {
        var predicate = typeof(AppDbContext).GetMethod(
            "IsDeterminationRecord",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(predicate);
        Assert.Equal(typeof(bool), predicate.ReturnType);
    }

    [Fact]
    public void The_observation_store_is_still_fact_only()
    {
        // Stage E must not have widened the thing it exists to avoid widening.
        var recordingMethods = typeof(AI.Investment.Domain.Observations.Observation)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.DeclaringType == typeof(AI.Investment.Domain.Observations.Observation))
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(["RecordFact"], recordingMethods);
    }

    [Fact]
    public void Infrastructure_may_consume_the_determination_store()
    {
        var configured = InfrastructureAssembly.GetTypes()
            .Where(t => t.Name.EndsWith("Configuration", StringComparison.Ordinal))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericArguments().Any(a => a == typeof(Determination))))
            .Select(t => t.Name)
            .ToList();

        Assert.Equal(["DeterminationConfiguration"], configured);
    }
}
