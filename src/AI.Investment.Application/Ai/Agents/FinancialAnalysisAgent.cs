using System.Text.Json;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>Reads a company's reported and computed figures and says what they mean together.</summary>
/// <remarks>
/// The classification argument from the architecture report holds here: a ratio is arithmetic and
/// belongs to a deterministic calculator, but the reading of several ratios at once is judgement and
/// belongs to an agent. This agent therefore computes nothing. Everything it quotes must already be
/// in the bundle, put there by Phase 2 ingestion or a Phase 3 calculator.
/// </remarks>
public sealed class FinancialAnalysisAgent : AnalysisAgent<EvidenceBundle, FinancialReading>
{
    public FinancialAnalysisAgent(IChatModel model, IPromptStore prompts)
        : base(model, prompts)
    {
    }

    public override AgentId AgentId { get; } =
        Domain.Ai.AgentId.Create("financial");

    public override string Version => "1.0";

    public override PromptRef Prompt { get; } = PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0);

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
              "required": ["summary", "figures", "strengths", "concerns"],
              "properties": {
                "summary": { "type": "string" },
                "strengths": { "type": "array", "items": { "type": "string" } },
                "concerns": { "type": "array", "items": { "type": "string" } },
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

    protected override FinancialReading ReadAnalysis(JsonElement analysis, EvidenceBundle bundle) =>
        new(
            AnalysisJson.RequiredString(analysis, "summary"),
            AnalysisJson.Figures(analysis, "figures", bundle),
            AnalysisJson.StringArray(analysis, "strengths"),
            AnalysisJson.StringArray(analysis, "concerns"));
}
