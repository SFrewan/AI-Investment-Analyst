using System.Text.Json;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>Reads what was published about a subject and says what it amounts to.</summary>
/// <remarks>
/// The agent with the most adversarial input in the system. News text is written by parties with an
/// interest in what this platform concludes, and it arrives inside the evidence block where a model
/// can read it. The containment is layered rather than clever: the evidence is framed as data on
/// both sides, the answer is constrained to a schema, every figure is checked against a claim, and
/// nothing this agent says can start an action.
/// </remarks>
public sealed class NewsAnalysisAgent : AnalysisAgent<EvidenceBundle, NewsReading>
{
    public NewsAnalysisAgent(IChatModel model, IPromptStore prompts)
        : base(model, prompts)
    {
    }

    public override AgentId AgentId { get; } =
        Domain.Ai.AgentId.Create("news");

    public override string Version => "1.0";

    public override PromptRef Prompt { get; } = PromptRef.Create("news-analyst", "coverage-interpretation", 1, 0);

    protected override string ResponseSchema => Schema;

    internal const string Schema =
        """
        {
          "type": "object",
          "required": ["refused", "confidence", "limitations", "analysis"],
          "properties": {
            "refused": { "type": "boolean" },
            "refusal_reason": { "type": ["string", "null"] },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "limitations": { "type": "array", "items": { "type": "string" } },
            "analysis": {
              "type": "object",
              "required": ["summary", "sentiment", "themes", "figures"],
              "properties": {
                "summary": { "type": "string" },
                "sentiment": { "enum": ["Negative", "Mixed", "Neutral", "Positive"] },
                "themes": { "type": "array", "items": { "type": "string" } },
                "figures": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["name", "value", "cite"],
                    "properties": {
                      "name": { "type": "string" },
                      "value": { "type": "number" },
                      "cite": { "type": "string" },
                      "is_percentage": { "type": "boolean" }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    protected override EvidenceBundle EvidenceFor(EvidenceBundle input) => input;

    protected override NewsReading ReadAnalysis(JsonElement analysis, EvidenceBundle bundle)
    {
        var sentiment = AnalysisJson.RequiredEnum<NewsSentiment>(analysis, "sentiment");

        if (sentiment == NewsSentiment.Unknown)
        {
            throw new AgentSchemaException(
                "'sentiment' may not be 'Unknown'. It is the unset value, and reporting it would " +
                "record an answer the agent never gave.");
        }

        return new NewsReading(
            AnalysisJson.RequiredString(analysis, "summary"),
            sentiment,
            AnalysisJson.StringArray(analysis, "themes"),
            AnalysisJson.Figures(analysis, "figures", bundle));
    }
}
