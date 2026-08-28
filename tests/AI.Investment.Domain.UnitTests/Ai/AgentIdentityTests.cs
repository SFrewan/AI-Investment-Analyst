using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ai;

public sealed class AgentIdentityTests
{
    [Fact]
    public void An_agent_identifier_is_normalised_to_lower_case() =>
        Assert.Equal("financial", AgentId.Create("  Financial  ").Value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has_underscore")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    public void An_agent_identifier_refuses_anything_that_is_not_a_slug(string value) =>
        Assert.Throws<DomainValidationException>(() => AgentId.Create(value));

    /// <summary>
    /// The agent has to be nameable as a claim producer, or its interpretations have no provenance.
    /// </summary>
    [Fact]
    public void An_agent_registers_as_a_producer_of_claims() =>
        Assert.Equal("agent.financial", AgentId.Create("financial").ProducerId.Value);

    [Fact]
    public void A_prompt_reference_states_its_agent_name_and_version()
    {
        var prompt = PromptRef.Create("Financial-Analyst", "Statement-Interpretation", 2, 3);

        Assert.Equal("financial-analyst", prompt.Agent);
        Assert.Equal("statement-interpretation", prompt.Name);
        Assert.Equal("financial-analyst/statement-interpretation", prompt.Value);
        Assert.Equal("v2.3", prompt.VersionLabel);
        Assert.Equal("financial-analyst/statement-interpretation@v2.3", prompt.ToString());
    }

    /// <summary>
    /// Both segments become path components, so anything that could walk out of the prompt root is
    /// refused at construction rather than defended against at the file system.
    /// </summary>
    [Theory]
    [InlineData("..", "name")]
    [InlineData("agent/sub", "name")]
    [InlineData("agent", "name.v1")]
    [InlineData("agent", "../escape")]
    [InlineData("", "name")]
    public void A_prompt_reference_refuses_a_segment_that_is_not_a_slug(string agent, string name) =>
        Assert.Throws<DomainValidationException>(() => PromptRef.Create(agent, name, 1, 0));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void A_prompt_version_starts_at_one_and_never_goes_backwards(int major, int minor) =>
        Assert.Throws<DomainValidationException>(() => PromptRef.Create("agent", "name", major, minor));

    [Fact]
    public void Two_prompt_versions_of_the_same_prompt_are_different_references() =>
        Assert.NotEqual(
            PromptRef.Create("agent", "name", 1, 0),
            PromptRef.Create("agent", "name", 1, 1));

    [Fact]
    public void A_model_reference_names_provider_model_and_pinned_version() =>
        Assert.Equal("anthropic/claude@2026-01-01", ModelRef.Create("anthropic", "claude", "2026-01-01").ToString());

    /// <summary>
    /// An unpinned model makes a provider-side revision indistinguishable from strategy drift, which
    /// is a distinction the outcome data cannot recover later.
    /// </summary>
    [Theory]
    [InlineData("", "model", "version")]
    [InlineData("provider", "", "version")]
    [InlineData("provider", "model", "")]
    public void A_model_reference_refuses_a_missing_segment(string provider, string model, string version) =>
        Assert.Throws<DomainValidationException>(() => ModelRef.Create(provider, model, version));

    [Fact]
    public void The_absent_model_says_so_rather_than_being_null()
    {
        Assert.True(ModelRef.None.IsNone);
        Assert.False(ModelRef.Create("p", "m", "v").IsNone);
    }
}
