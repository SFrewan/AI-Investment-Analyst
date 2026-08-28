using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Application.Ai.Pipeline;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>
/// The orchestrator: fixed control flow, validated input to synthesis, and an audit entry for every
/// run whichever way it went.
/// </summary>
public sealed class AnalysisPipelineTests
{
    private static readonly EvidenceBundle Bundle = AiTestBundles.Standard;

    private static string Cite => AiTestBundles.LabelOf("financial.net-margin");

    private static ChatCompletion Ok(string json) => ChatCompletion.Ok(json, 100, 50, 0.0002m, 20);

    private static string FinancialAnswer() =>
        $$"""
          { "refused": false, "confidence": 0.7, "limitations": [],
            "analysis": { "summary": "Profitability is stated.", "strengths": ["Positive income."],
              "concerns": [], "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ] } }
          """;

    private static string NewsAnswer() =>
        $$"""
          { "refused": false, "confidence": 0.5, "limitations": [],
            "analysis": { "summary": "Coverage is quiet.", "sentiment": "Neutral", "themes": ["pricing"],
              "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ] } }
          """;

    private static string RiskAnswer() =>
        $$"""
          { "refused": false, "confidence": 0.4, "limitations": [],
            "analysis": { "summary": "One exposure stands out.",
              "risks": [ { "description": "Customer concentration.", "severity": "Medium" } ],
              "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ] } }
          """;

    private static string SynthesisAnswer() =>
        $$"""
          { "refused": false, "confidence": 0.5, "limitations": [],
            "analysis": { "narrative": "The picture is unremarkable.", "stance": "Neutral",
              "key_points": ["Evidence is narrow."],
              "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ] } }
          """;

    private sealed record Harness(AnalysisPipeline Pipeline, NullAuditSink Audit);

    private static Harness Build(
        string? financial = null,
        string? news = null,
        string? risk = null,
        string? synthesis = null)
    {
        var prompts = InMemoryPromptStore.Any();
        var audit = new NullAuditSink();

        var pipeline = new AnalysisPipeline(
            new FinancialAnalysisAgent(
                new ScriptedChatModel { Fallback = Ok(financial ?? FinancialAnswer()) }, prompts),
            new NewsAnalysisAgent(
                new ScriptedChatModel { Fallback = Ok(news ?? NewsAnswer()) }, prompts),
            new RiskAnalysisAgent(
                new ScriptedChatModel { Fallback = Ok(risk ?? RiskAnswer()) }, prompts),
            new SynthesisAgent(
                new ScriptedChatModel { Fallback = Ok(synthesis ?? SynthesisAnswer()) }, prompts),
            audit,
            new FixedClock(AiTestBundles.Now));

        return new Harness(pipeline, audit);
    }

    private static AnalysisRequest Request(int calls = 40) =>
        AnalysisRequest.Create(
            CorrelationId.Create("test-analysis"),
            Bundle,
            AnalysisBudget.Create(10m, calls));

    [Fact]
    public async Task A_complete_run_produces_three_specialists_and_a_synthesis()
    {
        var outcome = await Build().Pipeline.RunAsync(Request());

        Assert.Equal(3, outcome.Specialists.Count);
        Assert.Equal(3, outcome.SucceededCount);
        Assert.True(outcome.IsComplete);
        Assert.Equal("The picture is unremarkable.", outcome.Synthesis!.RequireOutput().Narrative);
    }

    /// <summary>Every run is recorded, and a refusal is as much a record as an answer.</summary>
    [Fact]
    public async Task Every_agent_run_is_written_to_the_audit_trail()
    {
        var harness = Build();

        await harness.Pipeline.RunAsync(Request());

        Assert.Equal(4, harness.Audit.Records.Count);
        Assert.All(
            harness.Audit.Records,
            record => Assert.Equal(ProposerKind.AiAgent, record.ActorKind));
        Assert.All(
            harness.Audit.Records,
            record => Assert.Equal(AuditEventType.AgentOutputAccepted, record.EventType));
    }

    /// <summary>
    /// The evidence fingerprint is what makes the rest worth storing: without it "the evidence
    /// changed" and "the answer changed" are indistinguishable a month later.
    /// </summary>
    [Fact]
    public async Task An_audit_record_names_the_model_the_prompt_and_the_evidence_hash()
    {
        var harness = Build();

        await harness.Pipeline.RunAsync(Request());

        var record = harness.Audit.Records[0];

        Assert.Equal(Bundle.Hash, record.Details["analysis.evidenceHash"]);
        Assert.Equal("test/scripted@2026-01-01", record.Details["model"]);
        Assert.Contains("@v1.0", record.Details["prompt"], StringComparison.Ordinal);
        Assert.Equal("1.0", record.Details["agent.version"]);
    }

    /// <summary>An agent run is not an action and never acquires the identifiers of one.</summary>
    [Fact]
    public async Task An_agent_run_is_never_recorded_as_an_action()
    {
        var harness = Build();

        await harness.Pipeline.RunAsync(Request());

        Assert.All(harness.Audit.Records, record =>
        {
            Assert.Null(record.ProposalId);
            Assert.Null(record.DecisionId);
            Assert.Null(record.ExecutionId);
            Assert.Null(record.Outcome);
            Assert.Null(record.Capability);
            Assert.Null(record.RiskTier);
        });
    }

    [Fact]
    public async Task A_rejected_run_is_recorded_as_a_rejection()
    {
        var harness = Build(news: """{ "refused": true, "refusal_reason": "no coverage supplied", "limitations": [] }""");

        await harness.Pipeline.RunAsync(Request());

        var rejected = Assert.Single(
            harness.Audit.Records,
            record => record.EventType == AuditEventType.AgentOutputRejected);

        Assert.Equal("news", rejected.Details["agent.id"]);
        Assert.Equal("Refused", rejected.Details["agent.status"]);
    }

    /// <summary>
    /// A failed specialist contributes nothing at all - not a summary, not a caveat, not its
    /// figures. A caveat in a prompt is a suggestion; the narrative that results is what gets quoted.
    /// </summary>
    [Fact]
    public async Task A_failed_specialist_is_excluded_from_synthesis_entirely()
    {
        var ungrounded = $$"""
                           { "refused": false, "confidence": 0.9, "limitations": [],
                             "analysis": { "summary": "Coverage is strong.", "sentiment": "Positive", "themes": [],
                               "figures": [ { "name": "invented", "value": 4242, "cite": "{{Cite}}" } ] } }
                           """;

        var harness = Build(news: ungrounded);

        var outcome = await harness.Pipeline.RunAsync(Request());

        Assert.Equal(2, outcome.SucceededCount);
        Assert.True(outcome.IsComplete);

        var newsRun = Assert.Single(outcome.Specialists, result => result.AgentId.Value == "news");
        Assert.Equal(AgentStatus.Ungrounded, newsRun.Status);
    }

    /// <summary>
    /// Failed runs are kept in the outcome. A run where one specialist refused is a different thing
    /// from a run where all three succeeded, and an outcome carrying only successes hides that.
    /// </summary>
    [Fact]
    public async Task Failed_specialist_runs_are_kept_in_the_outcome()
    {
        var outcome = await Build(
            risk: """{ "refused": true, "refusal_reason": "nothing to assess", "limitations": [] }""")
            .Pipeline.RunAsync(Request());

        Assert.Equal(3, outcome.Specialists.Count);
        Assert.Equal(2, outcome.SucceededCount);
    }

    [Fact]
    public async Task When_every_specialist_fails_there_is_no_synthesis_at_all()
    {
        const string Refusal = """{ "refused": true, "refusal_reason": "nothing to read", "limitations": [] }""";

        var harness = Build(Refusal, Refusal, Refusal);

        var outcome = await harness.Pipeline.RunAsync(Request());

        Assert.Equal(0, outcome.SucceededCount);
        Assert.Null(outcome.Synthesis);
        Assert.False(outcome.IsComplete);
        Assert.Equal(3, harness.Audit.Records.Count);
    }

    /// <summary>The ceiling is shared across the fan-out, or three agents each get the whole budget.</summary>
    [Fact]
    public async Task The_budget_is_shared_across_every_agent_in_the_run()
    {
        var request = Request(calls: 2);

        var outcome = await Build().Pipeline.RunAsync(request);

        Assert.Equal(2, request.Budget.Calls);
        Assert.Contains(outcome.Specialists, result => result.Status == AgentStatus.BudgetExceeded);
    }
}
