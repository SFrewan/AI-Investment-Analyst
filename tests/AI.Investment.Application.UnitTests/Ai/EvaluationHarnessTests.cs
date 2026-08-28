using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Application.Ai.Evaluation;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>
/// The AI phase's exit criterion, as something a test can fail.
/// </summary>
/// <remarks>
/// The harness measures four things, and the fourth is the one that measures the controls rather
/// than the model: a run where every answer parses, everything is grounded, and the deliberately
/// fabricated cases are accepted anyway would score perfectly on the other three.
/// </remarks>
public sealed class EvaluationHarnessTests
{
    private static readonly EvidenceBundle Grounded = AiTestBundles.Standard;

    private static string Cite => AiTestBundles.LabelOf("financial.net-margin");

    private static AnalysisBudget Budget() => AnalysisBudget.Create(1m, 10);

    private static FinancialAnalysisAgent Agent(IChatModel model) =>
        new(model, InMemoryPromptStore.Any());

    private static string GoodAnswer() =>
        $$"""
          { "refused": false, "confidence": 0.7, "limitations": [],
            "analysis": { "summary": "Profitability is stated.", "strengths": [], "concerns": [],
              "figures": [ { "name": "net-margin", "value": 0.1, "cite": "{{Cite}}" } ] } }
          """;

    private static string FabricatedAnswer() =>
        $$"""
          { "refused": false, "confidence": 0.95, "limitations": [],
            "analysis": { "summary": "Margins are exceptional.", "strengths": [], "concerns": [],
              "figures": [ { "name": "net-margin", "value": 0.42, "cite": "{{Cite}}" } ] } }
          """;

    /// <summary>A second bundle, so a report covers more than one scenario.</summary>
    private static EvidenceBundle SecondBundle() =>
        EvidenceBundle.Create(
            IngestionSubject.Create("Company", "MSFT"),
            KnowledgeCutoff.At(AiTestBundles.Now),
            [
                EvidenceItem.Create(
                    "financial.net-margin",
                    Claims.Fact(
                        0.1m,
                        Provenance.Create(
                            "sec-edgar",
                            AiTestBundles.PeriodEnd,
                            AiTestBundles.Published,
                            AiTestBundles.Published))),
            ]);

    [Fact]
    public async Task A_well_behaved_agent_clears_the_phase_four_bar()
    {
        var report = await EvaluationHarness.RunAsync(
            Agent(ScriptedChatModel.Always(GoodAnswer())),
            [
                EvaluationCase.Create("standard bundle", Grounded, EvaluationExpectation.Grounded),
                EvaluationCase.Create("second bundle", SecondBundle(), EvaluationExpectation.Grounded),
            ],
            Budget);

        Assert.True(report.Meets(EvaluationThresholds.Phase4), report.Explain());
        Assert.Equal(1m, report.SchemaValidity);
        Assert.Equal(1m, report.Groundedness);
        Assert.Equal(1m, report.Stability);
        Assert.Equal(1m, report.ExpectationAccuracy);
        Assert.Equal(4, report.TotalRuns);
    }

    /// <summary>
    /// The case that proves the harness measures the controls. If the rejection rate on fabricated
    /// evidence ever reaches zero, the check has stopped working - and that is invisible from the
    /// success rate on everything else.
    /// </summary>
    [Fact]
    public async Task A_case_that_must_be_rejected_is_scored_by_whether_it_was()
    {
        var report = await EvaluationHarness.RunAsync(
            Agent(ScriptedChatModel.Always(FabricatedAnswer())),
            [EvaluationCase.Create("fabricated margin", Grounded, EvaluationExpectation.Rejected)],
            Budget);

        Assert.Equal(1m, report.ExpectationAccuracy);
        Assert.Equal(0m, report.Groundedness);
        Assert.True(report.Meets(EvaluationThresholds.Create(1m, 0m, 1m, 1m)));
    }

    [Fact]
    public async Task An_agent_that_answers_when_it_should_have_been_refused_fails_the_bar()
    {
        var report = await EvaluationHarness.RunAsync(
            Agent(ScriptedChatModel.Always(GoodAnswer())),
            [EvaluationCase.Create("should have been caught", Grounded, EvaluationExpectation.Rejected)],
            Budget);

        Assert.Equal(0m, report.ExpectationAccuracy);
        Assert.False(report.Meets(EvaluationThresholds.Phase4));
        Assert.Contains("expected Rejected", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unparseable_answer_lowers_schema_validity_rather_than_groundedness()
    {
        var report = await EvaluationHarness.RunAsync(
            Agent(new ScriptedChatModel { Fallback = ChatCompletion.Ok("not json", 10, 10, 0m, 1) }),
            [EvaluationCase.Create("garbage", Grounded, EvaluationExpectation.Grounded)],
            Budget);

        Assert.Equal(0m, report.SchemaValidity);
        Assert.Equal(0m, report.Groundedness);
        Assert.Equal(2, report.SchemaFailures);
        Assert.Equal(0, report.ParsedRuns);
    }

    /// <summary>
    /// A provider outage must not be reported as a fabrication problem: the two need completely
    /// different responses. It is excluded from both rates - and because that leaves nothing
    /// measured, the report refuses to pass rather than reporting a vacuous success.
    /// </summary>
    [Fact]
    public async Task An_evaluation_where_nothing_reached_the_provider_measures_nothing_and_fails()
    {
        var report = await EvaluationHarness.RunAsync(
            Agent(new ScriptedChatModel { Fallback = ChatCompletion.Failed("provider down") }),
            [EvaluationCase.Create("outage", Grounded, EvaluationExpectation.Rejected)],
            Budget);

        Assert.Equal(2, report.ProviderFailures);
        Assert.Equal(0, report.AnsweredRuns);
        Assert.Equal(0, report.ParsedRuns);
        Assert.Equal(0, report.SchemaFailures);
        Assert.Equal(1m, report.ExpectationAccuracy);
        Assert.False(report.Meets(EvaluationThresholds.Phase4));
        Assert.Contains("nothing was measured", report.Explain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Stability is a tautology against a deterministic provider, and that is the point: the metric
    /// exists so that the day a sampling provider arrives, the number moves and somebody decides
    /// what to do about it.
    /// </summary>
    [Fact]
    public async Task Repeated_runs_of_the_same_case_agree()
    {
        var report = await EvaluationHarness.RunAsync(
            Agent(ScriptedChatModel.Always(GoodAnswer())),
            [EvaluationCase.Create("stability", Grounded, EvaluationExpectation.Grounded)],
            Budget,
            repeats: 4);

        Assert.Equal(1m, report.Stability);
        Assert.Equal(4, report.TotalRuns);
        Assert.True(Assert.Single(report.Cases).Stable);
    }

    [Fact]
    public async Task An_agent_that_answers_differently_each_time_is_reported_as_unstable()
    {
        var wobbly = new ScriptedChatModel(
            ChatCompletion.Ok(GoodAnswer(), 10, 10, 0m, 1),
            ChatCompletion.Ok(FabricatedAnswer(), 10, 10, 0m, 1))
        {
            Fallback = ChatCompletion.Ok(GoodAnswer(), 10, 10, 0m, 1),
        };

        var report = await EvaluationHarness.RunAsync(
            Agent(wobbly),
            [EvaluationCase.Create("wobbly", Grounded, EvaluationExpectation.Grounded)],
            Budget);

        Assert.Equal(0m, report.Stability);
        Assert.False(report.Meets(EvaluationThresholds.Phase4));

        // The case failed its expectation as well as wobbling, so the report names what it saw
        // rather than only that the repeats disagreed - the observed statuses are the useful part.
        Assert.Contains("Ok, Ungrounded", report.Explain(), StringComparison.Ordinal);
        Assert.False(Assert.Single(report.Cases).Stable);
    }

    /// <summary>Stability cannot be measured from a single run, so the harness refuses to pretend.</summary>
    [Fact]
    public async Task Fewer_than_two_repeats_is_refused() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            EvaluationHarness.RunAsync(
                Agent(ScriptedChatModel.Always(GoodAnswer())),
                [EvaluationCase.Create("one run", Grounded, EvaluationExpectation.Grounded)],
                Budget,
                repeats: 1));

    /// <summary>A case that passes whatever happens is worse than not running it.</summary>
    [Fact]
    public void A_case_with_no_stated_expectation_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            EvaluationCase.Create("nameless expectation", Grounded, EvaluationExpectation.Unknown));

    [Fact]
    public void A_threshold_outside_zero_to_one_is_refused() =>
        Assert.Throws<DomainValidationException>(() => EvaluationThresholds.Create(1.5m, 1m, 1m, 1m));

    /// <summary>
    /// Diagnostics vary between identical answers, so including them would report every run as
    /// unstable - useless in exactly the situation the metric exists for.
    /// </summary>
    [Fact]
    public async Task The_stability_fingerprint_ignores_tokens_latency_and_cost()
    {
        var cheap = new ScriptedChatModel { Fallback = ChatCompletion.Ok(GoodAnswer(), 1, 1, 0m, 1) };
        var dear = new ScriptedChatModel { Fallback = ChatCompletion.Ok(GoodAnswer(), 900, 900, 5m, 900) };

        var first = await Agent(cheap).AnalyseAsync(Grounded, Budget());
        var second = await Agent(dear).AnalyseAsync(Grounded, Budget());

        Assert.NotEqual(first.Diagnostics.TokensIn, second.Diagnostics.TokensIn);
        Assert.Equal(EvaluationHarness.Fingerprint(first), EvaluationHarness.Fingerprint(second));
    }
}
