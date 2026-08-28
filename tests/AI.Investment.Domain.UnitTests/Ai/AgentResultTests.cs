using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ai;

public sealed class AgentResultTests
{
    private sealed class Reading
    {
        public string Text { get; init; } = "a reading";
    }

    private static AgentResult<Reading> Ok(IEnumerable<string>? limitations = null) =>
        AgentResults.Ok(
            AiFixtures.Agent,
            "1.0",
            new Reading(),
            Confidence.Create(0.7m),
            [AiFixtures.Fact(1m).Id],
            AiFixtures.Diagnostics,
            limitations);

    [Fact]
    public void A_successful_result_carries_its_output_confidence_and_evidence()
    {
        var result = Ok();

        Assert.True(result.Succeeded);
        Assert.Equal(AgentStatus.Ok, result.Status);
        Assert.NotNull(result.Output);
        Assert.Equal(0.7m, result.Confidence!.Value);
        Assert.Single(result.Evidence);
        Assert.Null(result.Explanation);
    }

    /// <summary>
    /// A judgement without stated uncertainty is indistinguishable downstream from a measurement.
    /// </summary>
    [Fact]
    public void A_successful_result_without_confidence_cannot_be_constructed() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            new AgentResult<Reading>(
                AiFixtures.Agent,
                "1.0",
                AgentStatus.Ok,
                new Reading(),
                confidence: null,
                [AiFixtures.Fact(1m).Id],
                limitations: null,
                AiFixtures.Diagnostics,
                explanation: null));

    /// <summary>
    /// A judgement with no traceable supporting claim cannot be checked and must be treated as
    /// fabricated.
    /// </summary>
    [Fact]
    public void A_successful_result_citing_no_evidence_cannot_be_constructed() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            AgentResults.Ok(
                AiFixtures.Agent,
                "1.0",
                new Reading(),
                Confidence.Create(0.7m),
                [],
                AiFixtures.Diagnostics));

    [Theory]
    [InlineData(AgentStatus.SchemaFailed)]
    [InlineData(AgentStatus.Ungrounded)]
    [InlineData(AgentStatus.Refused)]
    [InlineData(AgentStatus.ProviderError)]
    [InlineData(AgentStatus.BudgetExceeded)]
    public void A_failed_result_carries_no_output_and_states_why(AgentStatus status)
    {
        var result = AgentResults.Failed<Reading>(
            AiFixtures.Agent,
            "1.0",
            status,
            "because the test said so",
            AiFixtures.Diagnostics);

        Assert.False(result.Succeeded);
        Assert.Null(result.Output);
        Assert.Null(result.Confidence);
        Assert.Equal("because the test said so", result.Explanation);
    }

    /// <summary>A partially trusted answer is one that will be read as a whole one.</summary>
    [Fact]
    public void A_failed_result_may_not_also_carry_an_output() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            new AgentResult<Reading>(
                AiFixtures.Agent,
                "1.0",
                AgentStatus.Ungrounded,
                new Reading(),
                confidence: null,
                evidence: null,
                limitations: null,
                AiFixtures.Diagnostics,
                "ungrounded"));

    [Fact]
    public void A_failed_result_must_be_explained() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            AgentResults.Failed<Reading>(
                AiFixtures.Agent,
                "1.0",
                AgentStatus.Refused,
                "   ",
                AiFixtures.Diagnostics));

    /// <summary>
    /// The unset status is a failure, not a success. A result that skipped initialisation must not
    /// present itself as a completed analysis.
    /// </summary>
    [Fact]
    public void An_unset_status_is_refused_at_construction() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            new AgentResult<Reading>(
                AiFixtures.Agent,
                "1.0",
                AgentStatus.Unknown,
                output: null,
                confidence: null,
                evidence: null,
                limitations: null,
                AiFixtures.Diagnostics,
                "unset"));

    [Fact]
    public void An_agent_must_state_its_own_version() =>
        Assert.Throws<DomainValidationException>(() =>
            AgentResults.Failed<Reading>(
                AiFixtures.Agent,
                "  ",
                AgentStatus.Refused,
                "no version",
                AiFixtures.Diagnostics));

    [Fact]
    public void Requiring_the_output_of_a_failed_run_throws_rather_than_returning_a_default()
    {
        var result = AgentResults.Failed<Reading>(
            AiFixtures.Agent,
            "1.0",
            AgentStatus.ProviderError,
            "provider down",
            AiFixtures.Diagnostics);

        var exception = Assert.Throws<DomainRuleViolationException>(() => result.RequireOutput());

        Assert.Contains("provider down", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The only door between an agent and the claim graph, and it opens onto exactly one kind.
    /// </summary>
    [Fact]
    public void An_agent_result_becomes_an_interpretation_and_never_a_fact()
    {
        var claim = Ok(["could not read the segment detail"]).ToClaim(AiFixtures.PeriodEnd, AiFixtures.Now);

        Assert.Equal(ClaimKind.AiInterpretation, claim.Kind);
        Assert.False(claim.IsFact);
        Assert.True(claim.IsJudgement);
        Assert.Equal(0.7m, claim.Confidence!.Value);
        Assert.Single(claim.DerivedFrom);
        Assert.Equal("agent.financial", claim.Provenance.SourceId.Value);
        Assert.Contains("could not read the segment detail", claim.Caveats);
    }

    [Fact]
    public void Blank_limitations_are_dropped_rather_than_stored_as_empty_strings()
    {
        var result = Ok(["   ", "a real limitation", ""]);

        Assert.Equal(["a real limitation"], result.Limitations);
    }

    [Fact]
    public void Diagnostics_must_record_at_least_one_attempt() =>
        Assert.Throws<DomainValidationException>(() =>
            AgentDiagnostics.Create(AiFixtures.Model, AiFixtures.Prompt, 0, 0, 0m, 0, 0));

    [Fact]
    public void Diagnostics_for_a_run_that_never_reached_a_provider_name_no_model()
    {
        var diagnostics = AgentDiagnostics.NotAttempted(AiFixtures.Prompt);

        Assert.True(diagnostics.Model.IsNone);
        Assert.Equal(0m, diagnostics.CostUsd);
        Assert.Equal(1, diagnostics.Attempts);
    }
}
