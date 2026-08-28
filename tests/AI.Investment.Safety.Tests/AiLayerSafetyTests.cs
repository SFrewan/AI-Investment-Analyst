using System.Reflection;
using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Application.Ai.Pipeline;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The safety properties of the AI layer, as assertions rather than intentions.
/// </summary>
/// <remarks>
/// The claim this phase has to make good on is that adding judgement to the platform did not add a
/// path from a model to an effect. These tests are that claim, stated so it fails if it stops being
/// true - each one covering a way the separation could erode without anybody noticing in review.
/// </remarks>
public sealed class AiLayerSafetyTests
{
    private static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Assembly ApplicationAssembly = typeof(AnalysisPipeline).Assembly;
    private static readonly Assembly DomainAssembly = typeof(EvidenceBundle).Assembly;

    private static Claim<decimal> Fact(decimal value) =>
        Claims.Fact(value, Provenance.Create("sec-edgar", PeriodEnd, Published, Published));

    private static AgentResult<FinancialReading> Reading()
    {
        var evidence = Fact(0.1m);

        return AgentResults.Ok(
            AgentId.Create("financial"),
            "1.0",
            new FinancialReading("Profitability is stated.", [], [], []),
            Confidence.Create(0.7m),
            [evidence.Id],
            AgentDiagnostics.Create(
                ModelRef.Create("test", "scripted", "1"),
                PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0),
                10,
                10,
                0m,
                1,
                1));
    }

    /// <summary>
    /// The one door between an agent and the claim graph opens onto exactly one kind. Nothing that
    /// requires a measured value can consume a model's output by accident.
    /// </summary>
    [Fact]
    public void An_agent_output_can_only_ever_become_an_interpretation()
    {
        var claim = Reading().ToClaim(PeriodEnd, Now);

        Assert.Equal(ClaimKind.AiInterpretation, claim.Kind);
        Assert.True(claim.IsJudgement);
        Assert.False(claim.IsFact);
        Assert.Throws<DomainRuleViolationException>(() => claim.RequireFactValue());
    }

    /// <summary>
    /// The deterministic calculators refuse a judgement outright, which is what makes the epistemic
    /// separation load-bearing rather than descriptive.
    /// </summary>
    [Fact]
    public void An_agent_output_cannot_be_fed_to_a_deterministic_calculation()
    {
        // A numeric interpretation, produced exactly as an agent result records itself: the same
        // kind, the same producer, the same evidence chain. The value type differs only because a
        // calculator takes numbers, and the point is that the calculator refuses this regardless.
        var evidence = Fact(0.1m);

        var judgement = Claims.AiInterpretation(
            0.42m,
            Provenance.FromSystem(AgentId.Create("financial").ProducerId, PeriodEnd, Now),
            [evidence.Id],
            Confidence.Create(0.9m));

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => CalculationInput.Create("net-margin", judgement, UnitOfMeasure.Ratio));

        Assert.Equal("CalculationInput.EvidenceIsJudgement", exception.Rule);
    }

    /// <summary>
    /// The kind an agent result records itself under is the kind the calculators refuse. Stated as
    /// its own assertion so the two halves of the argument above cannot drift apart.
    /// </summary>
    [Fact]
    public void The_kind_an_agent_records_is_the_kind_a_calculation_refuses()
    {
        var claim = Reading().ToClaim(PeriodEnd, Now);

        Assert.Equal(ClaimKind.AiInterpretation, claim.Kind);
        Assert.True(claim.IsJudgement);
    }

    /// <summary>
    /// Feeding one agent's opinion to the next is how a single invented figure becomes an apparent
    /// consensus, and no downstream validation recovers from it.
    /// </summary>
    [Fact]
    public void An_agent_output_cannot_re_enter_the_evidence_a_later_agent_reads()
    {
        var claim = Reading().ToClaim(PeriodEnd, Now);

        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            EvidenceBundle.Create(
                IngestionSubject.Create("Company", "AAPL"),
                KnowledgeCutoff.At(Now),
                [EvidenceItem.Create("agent.reading", claim)]));

        Assert.Equal("EvidenceBundle.JudgementIsNotEvidence", exception.Rule);
    }

    /// <summary>
    /// The orchestrator holds no gateway, no repository and no unit of work. It cannot cause
    /// anything, which is a stronger statement than "it does not currently call anything".
    /// </summary>
    [Fact]
    public void The_pipeline_has_no_way_to_cause_an_effect()
    {
        var forbidden = new[]
        {
            "IActionGateway",
            "IUnitOfWork",
            "ICompanyRepository",
            "IObservationStore",
            "IPolicyEngine",
            "IPolicyContextProvider",
        };

        var dependencies = typeof(AnalysisPipeline)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, dependencies, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Nothing in the AI layer may so much as name a safety type. A reference is how a shortcut
    /// starts: first it is read, then it is passed, then it is set.
    /// </summary>
    [Fact]
    public void No_type_in_the_ai_layer_references_the_action_or_policy_seam()
    {
        var seam = new[]
        {
            "AI.Investment.Domain.Actions",
            "AI.Investment.Application.Actions",
        };

        var offenders = new List<string>();

        foreach (var assembly in new[] { DomainAssembly, ApplicationAssembly })
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace is null || !type.Namespace.Contains(".Ai", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var referenced in ReferencedNamespaces(type))
                {
                    if (Array.Exists(seam, prefix => referenced.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        offenders.Add($"{type.FullName} -> {referenced}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// A model's severity is an opinion about the world. The tier that decides whether anything may
    /// run is computed from economics and reversibility, and the two must not be the same type.
    /// </summary>
    [Fact]
    public void An_agents_risk_severity_is_not_the_platforms_risk_tier()
    {
        Assert.NotEqual(typeof(RiskSeverity), typeof(RiskTier));
        Assert.False(Enum.GetNames<RiskSeverity>().SequenceEqual(Enum.GetNames<RiskTier>()));
    }

    /// <summary>
    /// A judgement without stated uncertainty is indistinguishable downstream from a measurement,
    /// and one citing no evidence cannot be checked for groundedness at all.
    /// </summary>
    [Fact]
    public void A_successful_agent_result_must_state_confidence_and_cite_evidence()
    {
        Assert.Throws<DomainRuleViolationException>(() =>
            AgentResults.Ok(
                AgentId.Create("financial"),
                "1.0",
                new FinancialReading("s", [], [], []),
                Confidence.Create(0.7m),
                [],
                AgentDiagnostics.NotAttempted(
                    PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0))));
    }

    /// <summary>
    /// The unset status must be a failure. Making success the default would let a result that
    /// skipped initialisation present itself as a completed analysis.
    /// </summary>
    [Fact]
    public void The_default_agent_status_is_not_success() =>
        Assert.NotEqual(AgentStatus.Ok, default(AgentStatus));

    /// <summary>
    /// The strictest check must be what a caller gets by forgetting to choose, because a
    /// configuration mistake would otherwise quietly relax the one control between a model's
    /// invention and a stored score.
    /// </summary>
    [Fact]
    public void The_default_groundedness_policy_is_the_strict_one() =>
        Assert.Equal(GroundednessPolicy.Strict, default(GroundednessPolicy));

    /// <summary>Every agent runs under the strict policy; none may opt itself down.</summary>
    [Fact]
    public void No_shipped_agent_relaxes_its_own_groundedness_policy()
    {
        var agents = ApplicationAssembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IAnalysisAgent).IsAssignableFrom(type))
            .ToList();

        Assert.Equal(4, agents.Count);

        foreach (var type in agents)
        {
            var property = type.GetProperty(nameof(IAnalysisAgent.GroundednessPolicy));

            Assert.NotNull(property);
            Assert.False(
                property!.GetSetMethod() is not null,
                $"{type.Name} exposes a setter for its groundedness policy.");
        }
    }

    private static IEnumerable<string> ReferencedNamespaces(Type type)
    {
        foreach (var field in type.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (field.FieldType.Namespace is { } ns)
            {
                yield return ns;
            }
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly))
        {
            if (method.ReturnType.Namespace is { } returnNs)
            {
                yield return returnNs;
            }

            foreach (var parameter in method.GetParameters())
            {
                if (parameter.ParameterType.Namespace is { } parameterNs)
                {
                    yield return parameterNs;
                }
            }
        }
    }
}
