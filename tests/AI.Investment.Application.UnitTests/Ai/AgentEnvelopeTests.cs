using AI.Investment.Application.Ai;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>
/// The shared part of every answer. The parser is the schema enforcement in this phase, so absence
/// has to be an error rather than a default that reads like an answer.
/// </summary>
public sealed class AgentEnvelopeTests
{
    [Fact]
    public void A_complete_envelope_is_read()
    {
        var envelope = AgentEnvelope.Parse(
            """
            { "refused": false, "refusal_reason": null, "confidence": 0.62,
              "limitations": ["no comparative period", "  "],
              "analysis": { "summary": "s" } }
            """);

        Assert.False(envelope.Refused);
        Assert.Equal(0.62m, envelope.Confidence!.Value);
        Assert.Equal(["no comparative period"], envelope.Limitations);
        Assert.Equal("s", envelope.Analysis.GetProperty("summary").GetString());
    }

    [Fact]
    public void A_refusal_carries_its_reason_and_no_confidence()
    {
        var envelope = AgentEnvelope.Parse(
            """{ "refused": true, "refusal_reason": "the evidence is too thin", "limitations": [] }""");

        Assert.True(envelope.Refused);
        Assert.Equal("the evidence is too thin", envelope.RefusalReason);
        Assert.Null(envelope.Confidence);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    [InlineData("{ }")]
    [InlineData("""{ "refused": "no", "confidence": 0.5, "analysis": {} }""")]
    [InlineData("""{ "refused": false, "confidence": "high", "analysis": {} }""")]
    [InlineData("""{ "refused": false, "confidence": 1.5, "analysis": {} }""")]
    [InlineData("""{ "refused": false, "confidence": -0.1, "analysis": {} }""")]
    [InlineData("""{ "refused": false, "confidence": 0.5 }""")]
    [InlineData("""{ "refused": false, "confidence": 0.5, "analysis": "text" }""")]
    [InlineData("""{ "refused": true }""")]
    [InlineData("""{ "refused": false, "confidence": 0.5, "limitations": [1], "analysis": {} }""")]
    public void Anything_that_is_not_the_declared_shape_is_refused(string json) =>
        Assert.Throws<AgentSchemaException>(() => AgentEnvelope.Parse(json));

    [Fact]
    public void A_missing_limitations_list_is_read_as_empty_rather_than_rejected()
    {
        var envelope = AgentEnvelope.Parse(
            """{ "refused": false, "confidence": 0.5, "analysis": { } }""");

        Assert.Empty(envelope.Limitations);
    }
}
