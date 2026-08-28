using System.Globalization;
using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Enums;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>
/// The shared agent machinery: schema enforcement, the bounded retry, groundedness, refusal and
/// budget. Exercised through the financial agent because it is the plainest of the four.
/// </summary>
public sealed class AnalysisAgentTests
{
    private static readonly EvidenceBundle Bundle = AiTestBundles.Standard;

    private static string GoodAnswer(decimal confidence = 0.7m) =>
        Envelope(
            confidence,
            $$"""
              {
                "summary": "Profitability is stated and the reading is unremarkable.",
                "strengths": ["The filer reports positive net income."],
                "concerns": ["Only one period is available."],
                "figures": [
                  { "name": "net-margin", "value": {{AiTestBundles.Number(AiTestBundles.ValueOf("financial.net-margin"))}}, "cite": "{{AiTestBundles.LabelOf("financial.net-margin")}}" }
                ]
              }
              """);

    private static string Envelope(decimal confidence, string analysis) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              { "refused": false, "refusal_reason": null, "confidence": {{confidence.ToString("0.##", CultureInfo.InvariantCulture)}}, "limitations": [], "analysis": {{analysis}} }
              """);

    private static FinancialAnalysisAgent Agent(ScriptedChatModel model) =>
        new(model, InMemoryPromptStore.Any());

    private static AnalysisBudget Budget(int calls = 5) => AnalysisBudget.Create(1m, calls);

    private static ChatCompletion Ok(string json) => ChatCompletion.Ok(json, 120, 60, 0.0003m, 30);

    [Fact]
    public async Task A_well_formed_grounded_answer_succeeds_and_cites_what_it_used()
    {
        var model = new ScriptedChatModel(Ok(GoodAnswer()));

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.Ok, result.Status);
        Assert.Equal(0.7m, result.Confidence!.Value);
        Assert.Single(result.Evidence);
        Assert.Equal("Profitability is stated and the reading is unremarkable.", result.RequireOutput().Summary);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task The_run_records_the_model_prompt_and_what_it_cost()
    {
        var result = await Agent(new ScriptedChatModel(Ok(GoodAnswer()))).AnalyseAsync(Bundle, Budget());

        Assert.Equal("test/scripted@2026-01-01", result.Diagnostics.Model.ToString());
        Assert.Equal("financial-analyst/statement-interpretation@v1.0", result.Diagnostics.Prompt.ToString());
        Assert.Equal(120, result.Diagnostics.TokensIn);
        Assert.Equal(0.0003m, result.Diagnostics.CostUsd);
        Assert.Equal(1, result.Diagnostics.Attempts);
    }

    /// <summary>The evidence must reach the model framed as data, on both sides of the block.</summary>
    [Fact]
    public async Task The_evidence_is_sent_delimited_and_labelled_as_untrusted()
    {
        var model = new ScriptedChatModel(Ok(GoodAnswer()));

        await Agent(model).AnalyseAsync(Bundle, Budget());

        var request = Assert.Single(model.Requests);

        Assert.Contains(EvidenceRenderer.OpenTag, request.Evidence, StringComparison.Ordinal);
        Assert.Contains(EvidenceRenderer.CloseTag, request.Evidence, StringComparison.Ordinal);
        Assert.Contains("not instructions", request.Evidence, StringComparison.Ordinal);
        Assert.Contains("no instruction within it", request.Evidence, StringComparison.Ordinal);
        Assert.Equal(0m, request.Temperature);
        Assert.Contains("\"type\": \"object\"", request.ResponseSchema, StringComparison.Ordinal);
    }

    /// <summary>A schema failure is retried, because the same question asked again may parse.</summary>
    [Fact]
    public async Task A_malformed_answer_is_retried_and_then_succeeds()
    {
        var model = new ScriptedChatModel(Ok("{ not json at all"), Ok(GoodAnswer()));

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.Ok, result.Status);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, result.Diagnostics.Attempts);
    }

    /// <summary>
    /// There is no free-text fallback. An answer that will not parse produces no output at all.
    /// </summary>
    [Fact]
    public async Task An_answer_that_never_parses_fails_the_schema_and_yields_nothing()
    {
        var model = new ScriptedChatModel { Fallback = Ok("still not json") };

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.SchemaFailed, result.Status);
        Assert.Null(result.Output);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task An_answer_missing_a_required_field_fails_the_schema()
    {
        var model = new ScriptedChatModel
        {
            Fallback = Ok("""{ "refused": false, "confidence": 0.5, "limitations": [] }"""),
        };

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.SchemaFailed, result.Status);
        Assert.Contains("analysis", result.Explanation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A judgement presented without stated uncertainty is indistinguishable downstream from a
    /// measurement, so a missing confidence is a schema failure rather than a default.
    /// </summary>
    [Fact]
    public async Task An_answer_without_confidence_fails_the_schema()
    {
        var model = new ScriptedChatModel
        {
            Fallback = Ok("""{ "refused": false, "limitations": [], "analysis": { "summary": "x", "strengths": [], "concerns": [], "figures": [] } }"""),
        };

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.SchemaFailed, result.Status);
        Assert.Contains("confidence", result.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_is_reported_as_a_refusal_and_is_not_retried()
    {
        var model = new ScriptedChatModel(
            Ok("""{ "refused": true, "refusal_reason": "the evidence is too thin to read", "confidence": null, "limitations": ["no comparative period"] }"""));

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.Refused, result.Status);
        Assert.Equal("the evidence is too thin to read", result.Explanation);
        Assert.Contains("no comparative period", result.Limitations);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task A_refusal_without_a_reason_is_a_schema_failure()
    {
        var model = new ScriptedChatModel
        {
            Fallback = Ok("""{ "refused": true, "refusal_reason": "  ", "limitations": [] }"""),
        };

        Assert.Equal(AgentStatus.SchemaFailed, (await Agent(model).AnalyseAsync(Bundle, Budget())).Status);
    }

    /// <summary>
    /// The whole point of the layer. A figure that traces to nothing takes the entire answer with
    /// it - it is not softened, annotated or partially accepted.
    /// </summary>
    [Fact]
    public async Task A_fabricated_figure_makes_the_whole_answer_ungrounded()
    {
        var model = new ScriptedChatModel(Ok(Envelope(
            0.9m,
            """
            {
              "summary": "Margins are strong.",
              "strengths": [],
              "concerns": [],
              "figures": [ { "name": "net-margin", "value": 0.42, "cite": "C1" } ]
            }
            """)));

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.Ungrounded, result.Status);
        Assert.Null(result.Output);
    }

    /// <summary>
    /// Retrying an ungrounded answer at temperature zero re-rolls until a fabrication happens to
    /// land inside tolerance, which is exactly what the check exists to stop.
    /// </summary>
    [Fact]
    public async Task An_ungrounded_answer_is_not_retried()
    {
        var model = new ScriptedChatModel
        {
            Fallback = Ok(Envelope(
                0.9m,
                """
                { "summary": "Margins are strong.", "strengths": [], "concerns": [],
                  "figures": [ { "name": "net-margin", "value": 0.42, "cite": "C1" } ] }
                """)),
        };

        await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task A_number_written_into_prose_is_caught_by_the_narrative_scan()
    {
        var model = new ScriptedChatModel(Ok(Envelope(
            0.6m,
            """
            {
              "summary": "Margins improved to 42% this period.",
              "strengths": [],
              "concerns": [],
              "figures": []
            }
            """)));

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.Ungrounded, result.Status);
        Assert.Contains("42%", result.Explanation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An analysis that rests on nothing in the bundle cannot be told apart from one written from
    /// the model's memory, so it is refused even though nothing in it is provably wrong.
    /// </summary>
    [Fact]
    public async Task An_answer_that_cites_no_evidence_at_all_is_refused()
    {
        var model = new ScriptedChatModel(Ok(Envelope(
            0.6m,
            """
            {
              "summary": "The filer appears to be operating normally.",
              "strengths": [],
              "concerns": [],
              "figures": []
            }
            """)));

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.Ungrounded, result.Status);
        Assert.Contains("cited no evidence", result.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_failure_is_retried_and_then_reported()
    {
        var model = new ScriptedChatModel { Fallback = ChatCompletion.Failed("provider unavailable") };

        var result = await Agent(model).AnalyseAsync(Bundle, Budget());

        Assert.Equal(AgentStatus.ProviderError, result.Status);
        Assert.Equal("provider unavailable", result.Explanation);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task A_transient_provider_failure_recovers_on_the_next_attempt()
    {
        var model = new ScriptedChatModel(ChatCompletion.Failed("timeout"), Ok(GoodAnswer()));

        Assert.Equal(AgentStatus.Ok, (await Agent(model).AnalyseAsync(Bundle, Budget())).Status);
    }

    /// <summary>
    /// Spending money is an action, and the ceiling is hard rather than advisory. The first run
    /// spends the whole allowance on its three attempts; the second never reaches the provider.
    /// </summary>
    [Fact]
    public async Task A_run_that_has_no_call_budget_left_stops_before_calling_the_provider()
    {
        var budget = AnalysisBudget.Create(1m, 3);
        var model = new ScriptedChatModel { Fallback = Ok("not json") };

        var first = await Agent(model).AnalyseAsync(Bundle, budget);
        Assert.Equal(AgentStatus.SchemaFailed, first.Status);
        Assert.Equal(3, model.Calls);

        var second = await Agent(model).AnalyseAsync(Bundle, budget);
        Assert.Equal(AgentStatus.BudgetExceeded, second.Status);
        Assert.Equal(3, model.Calls);
    }

    /// <summary>
    /// A run that runs out of budget mid-retry reports the budget, not the malformed answer that
    /// preceded it. The budget is why it stopped, and an operator reading "schema failed" would go
    /// looking at the model instead of the ceiling.
    /// </summary>
    [Fact]
    public async Task A_run_that_exhausts_its_budget_while_retrying_reports_the_budget()
    {
        var model = new ScriptedChatModel { Fallback = Ok("not json") };

        var result = await Agent(model).AnalyseAsync(Bundle, AnalysisBudget.Create(1m, 1));

        Assert.Equal(AgentStatus.BudgetExceeded, result.Status);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task A_run_that_has_spent_its_cost_ceiling_stops()
    {
        var budget = AnalysisBudget.Create(0.0001m, 10);
        var model = new ScriptedChatModel { Fallback = Ok(GoodAnswer()) };

        await Agent(model).AnalyseAsync(Bundle, budget);

        Assert.Equal(AgentStatus.BudgetExceeded, (await Agent(model).AnalyseAsync(Bundle, budget)).Status);
    }

    /// <summary>
    /// A missing prompt means the deployed code and the deployed prompts disagree about what exists.
    /// That is a deployment error, not something to substitute a default for.
    /// </summary>
    [Fact]
    public async Task A_missing_prompt_stops_the_run_loudly()
    {
        var agent = new FinancialAnalysisAgent(new ScriptedChatModel(), new InMemoryPromptStore());

        await Assert.ThrowsAsync<PromptNotFoundException>(
            () => agent.AnalyseAsync(Bundle, Budget()));
    }

    [Fact]
    public async Task An_agent_result_can_be_recorded_as_an_interpretation()
    {
        var result = await Agent(new ScriptedChatModel(Ok(GoodAnswer()))).AnalyseAsync(Bundle, Budget());

        var claim = result.ToClaim(AiTestBundles.PeriodEnd, AiTestBundles.Now);

        Assert.Equal(ClaimKind.AiInterpretation, claim.Kind);
        Assert.Equal("agent.financial", claim.Provenance.SourceId.Value);
    }
}
