using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Ai;
using Xunit;

namespace AI.Investment.Integration.Tests.Ai;

/// <summary>
/// The registered default: with no provider configured, the AI layer declines rather than inventing.
/// </summary>
/// <remarks>
/// This is the fail-closed property in the same sense as an unknown kill-switch state. There is no
/// path through the shipped configuration that produces an analysis, so a misconfiguration can
/// never be mistaken for a working AI layer that happens to be terse.
/// </remarks>
public sealed class UnconfiguredChatModelTests
{
    private static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static EvidenceBundle Bundle() =>
        EvidenceBundle.Create(
            IngestionSubject.Create("Company", "AAPL"),
            KnowledgeCutoff.At(Now),
            [
                EvidenceItem.Create(
                    "financials.revenue",
                    Claims.Fact(1000m, Provenance.Create("sec-edgar", PeriodEnd, Published, Published))),
            ]);

    [Fact]
    public async Task It_names_no_model_and_refuses_every_request()
    {
        var model = new UnconfiguredChatModel();

        Assert.True(model.Model.IsNone);

        var completion = await model.CompleteAsync(
            ChatRequest.Create(
                PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0),
                "instructions",
                "<evidence>data</evidence>",
                "{}",
                256));

        Assert.False(completion.Succeeded);
        Assert.Null(completion.Json);
        Assert.Equal(UnconfiguredChatModel.Reason, completion.Error);
        Assert.Equal(0m, completion.CostUsd);
    }

    /// <summary>
    /// End to end on the shipped default: an agent wired to the registered model produces a refusal
    /// with no output, not a thin analysis.
    /// </summary>
    [Fact]
    public async Task An_agent_running_on_the_shipped_default_produces_no_analysis()
    {
        var agent = new FinancialAnalysisAgent(
            new UnconfiguredChatModel(),
            new InlinePromptStore("Test instructions."));

        var result = await agent.AnalyseAsync(Bundle(), AnalysisBudget.Create(1m, 5));

        Assert.Equal(AgentStatus.ProviderError, result.Status);
        Assert.Null(result.Output);
        Assert.Contains("fails closed", result.Explanation!, StringComparison.Ordinal);
        Assert.True(result.Diagnostics.Model.IsNone);
    }

    private sealed class InlinePromptStore : IPromptStore
    {
        private readonly string _text;

        public InlinePromptStore(string text) => _text = text;

        public Task<PromptTemplate> GetAsync(PromptRef prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(PromptTemplate.Create(prompt, _text));
    }
}
