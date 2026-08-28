using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>Each agent's own output shape, and the identity it declares about itself.</summary>
public sealed class AgentContractTests
{
    private static readonly EvidenceBundle Bundle = AiTestBundles.Standard;

    private static AnalysisBudget Budget() => AnalysisBudget.Create(1m, 5);

    private static ChatCompletion Ok(string json) => ChatCompletion.Ok(json, 100, 50, 0.0002m, 20);

    private static string Cite => AiTestBundles.LabelOf("financial.net-margin");

    [Fact]
    public void Every_agent_declares_a_distinct_identity_and_its_own_prompt()
    {
        var model = new ScriptedChatModel();
        var prompts = InMemoryPromptStore.Any();

        IAnalysisAgent[] agents =
        [
            new FinancialAnalysisAgent(model, prompts),
            new NewsAnalysisAgent(model, prompts),
            new RiskAnalysisAgent(model, prompts),
            new SynthesisAgent(model, prompts),
        ];

        Assert.Equal(4, agents.Select(agent => agent.AgentId.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, agents.Select(agent => agent.Prompt.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.All(agents, agent => Assert.False(string.IsNullOrWhiteSpace(agent.Version)));
        Assert.All(agents, agent => Assert.Equal(GroundednessPolicy.Strict, agent.GroundednessPolicy));
    }

    [Fact]
    public async Task The_news_agent_reads_a_sentiment_and_its_themes()
    {
        var json = $$"""
                     {
                       "refused": false, "confidence": 0.55, "limitations": [],
                       "analysis": {
                         "summary": "Coverage leans cautious on supply.",
                         "sentiment": "Mixed",
                         "themes": ["supply chain", "pricing"],
                         "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ]
                       }
                     }
                     """;

        var agent = new NewsAnalysisAgent(new ScriptedChatModel(Ok(json)), InMemoryPromptStore.Any());

        var reading = (await agent.AnalyseAsync(Bundle, Budget())).RequireOutput();

        Assert.Equal(NewsSentiment.Mixed, reading.Sentiment);
        Assert.Equal(["supply chain", "pricing"], reading.Themes);
    }

    /// <summary>
    /// The unset enum member is never a legitimate answer: an unrecognised word must fail rather
    /// than fall back to a value that reads as neutral.
    /// </summary>
    [Theory]
    [InlineData("Unknown")]
    [InlineData("Bullish")]
    [InlineData("")]
    public async Task The_news_agent_refuses_a_sentiment_outside_the_named_set(string sentiment)
    {
        var json = $$"""
                     {
                       "refused": false, "confidence": 0.5, "limitations": [],
                       "analysis": { "summary": "s", "sentiment": "{{sentiment}}", "themes": [], "figures": [] }
                     }
                     """;

        var agent = new NewsAnalysisAgent(
            new ScriptedChatModel { Fallback = Ok(json) },
            InMemoryPromptStore.Any());

        Assert.Equal(AgentStatus.SchemaFailed, (await agent.AnalyseAsync(Bundle, Budget())).Status);
    }

    [Fact]
    public async Task The_risk_agent_reads_risks_with_severities()
    {
        var json = $$"""
                     {
                       "refused": false, "confidence": 0.6, "limitations": [],
                       "analysis": {
                         "summary": "Two exposures stand out.",
                         "risks": [
                           { "description": "Concentration in one customer.", "severity": "High" },
                           { "description": "Thin liquidity headroom.", "severity": "Medium" }
                         ],
                         "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ]
                       }
                     }
                     """;

        var agent = new RiskAnalysisAgent(new ScriptedChatModel(Ok(json)), InMemoryPromptStore.Any());

        var assessment = (await agent.AnalyseAsync(Bundle, Budget())).RequireOutput();

        Assert.Equal(2, assessment.Risks.Count);
        Assert.Equal(RiskSeverity.High, assessment.HighestSeverity);
    }

    /// <summary>A risk stored without a severity sorts as though it were the mildest.</summary>
    [Fact]
    public async Task The_risk_agent_refuses_a_risk_with_no_usable_severity()
    {
        var json = """
                   {
                     "refused": false, "confidence": 0.6, "limitations": [],
                     "analysis": {
                       "summary": "s",
                       "risks": [ { "description": "d", "severity": "Unknown" } ],
                       "figures": []
                     }
                   }
                   """;

        var agent = new RiskAnalysisAgent(
            new ScriptedChatModel { Fallback = Ok(json) },
            InMemoryPromptStore.Any());

        Assert.Equal(AgentStatus.SchemaFailed, (await agent.AnalyseAsync(Bundle, Budget())).Status);
    }

    [Fact]
    public void An_identified_risk_must_state_a_severity() =>
        Assert.Throws<Domain.Exceptions.DomainValidationException>(
            () => IdentifiedRisk.Create("a risk", RiskSeverity.Unknown));

    [Fact]
    public async Task The_synthesis_agent_reads_a_stance_and_key_points()
    {
        var json = $$"""
                     {
                       "refused": false, "confidence": 0.5, "limitations": [],
                       "analysis": {
                         "narrative": "The specialists broadly agree.",
                         "stance": "Cautious",
                         "key_points": ["Profitable but narrow evidence."],
                         "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ]
                       }
                     }
                     """;

        var agent = new SynthesisAgent(new ScriptedChatModel(Ok(json)), InMemoryPromptStore.Any());

        var input = SynthesisInput.Create(
            Bundle,
            [
                SpecialistFinding.Create(
                    Domain.Ai.AgentId.Create("financial"),
                    Domain.ValueObjects.Confidence.Create(0.7m),
                    "Profitability is stated.",
                    ["Positive net income."],
                    [AssertedFigure.Create("net-margin", 0.1m)]),
            ]);

        var synthesis = (await agent.AnalyseAsync(input, Budget())).RequireOutput();

        Assert.Equal(AnalysisStance.Cautious, synthesis.Stance);
        Assert.Single(synthesis.KeyPoints);
    }

    /// <summary>
    /// Summarising an empty set of findings would produce a narrative with nothing behind it, which
    /// is the most convincing kind of fabrication this system can emit.
    /// </summary>
    [Fact]
    public void Synthesis_refuses_to_run_on_no_findings() =>
        Assert.Throws<Domain.Exceptions.DomainRuleViolationException>(
            () => SynthesisInput.Create(Bundle, []));

    /// <summary>Synthesis reads the specialists' findings, and it reads them after validation.</summary>
    [Fact]
    public async Task The_synthesis_prompt_carries_the_findings_as_well_as_the_evidence()
    {
        var model = new ScriptedChatModel { Fallback = ChatCompletion.Failed("stop here") };

        var agent = new SynthesisAgent(model, InMemoryPromptStore.Any());

        var input = SynthesisInput.Create(
            Bundle,
            [
                SpecialistFinding.Create(
                    Domain.Ai.AgentId.Create("risk"),
                    Domain.ValueObjects.Confidence.Create(0.4m),
                    "One exposure stands out.",
                    ["Customer concentration."],
                    []),
            ]);

        await agent.AnalyseAsync(input, Budget());

        var request = model.Requests[0];

        Assert.Contains(EvidenceRenderer.OpenTag, request.Evidence, StringComparison.Ordinal);
        Assert.Contains("<findings>", request.Evidence, StringComparison.Ordinal);
        Assert.Contains("One exposure stands out.", request.Evidence, StringComparison.Ordinal);
        Assert.Contains("Customer concentration.", request.Evidence, StringComparison.Ordinal);
    }
}
