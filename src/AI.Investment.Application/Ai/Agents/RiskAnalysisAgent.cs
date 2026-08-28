using System.Text.Json;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>Enumerates what could go wrong, from the evidence and nothing else.</summary>
/// <remarks>
/// Identification is judgement over unstructured evidence, which is what an agent is for.
/// Authorisation is not: the risk tier that decides whether an action may run is computed by
/// <c>RiskTierCalculator</c> from economics and reversibility, and this agent contributes nothing to
/// it. That separation is why a model calling a risk "low" can never make anything easier to do.
/// </remarks>
public sealed class RiskAnalysisAgent : AnalysisAgent<EvidenceBundle, RiskAssessment>
{
    public RiskAnalysisAgent(IChatModel model, IPromptStore prompts)
        : base(model, prompts)
    {
    }

    public override AgentId AgentId { get; } =
        Domain.Ai.AgentId.Create("risk");

    public override string Version => "1.0";

    public override PromptRef Prompt { get; } = PromptRef.Create("risk-analyst", "risk-identification", 1, 0);

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
              "required": ["summary", "risks", "figures"],
              "properties": {
                "summary": { "type": "string" },
                "risks": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["description", "severity"],
                    "properties": {
                      "description": { "type": "string" },
                      "severity": { "enum": ["Low", "Medium", "High", "Critical"] }
                    }
                  }
                },
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

    protected override RiskAssessment ReadAnalysis(JsonElement analysis, EvidenceBundle bundle) =>
        new(
            AnalysisJson.RequiredString(analysis, "summary"),
            ReadRisks(analysis),
            AnalysisJson.Figures(analysis, "figures", bundle));

    private static List<IdentifiedRisk> ReadRisks(JsonElement analysis)
    {
        var risks = new List<IdentifiedRisk>();

        if (!analysis.TryGetProperty("risks", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new AgentSchemaException("'risks' is required and must be an array.");
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new AgentSchemaException("'risks' must contain only objects.");
            }

            var severity = AnalysisJson.RequiredEnum<RiskSeverity>(entry, "severity");

            if (severity == RiskSeverity.Unknown)
            {
                throw new AgentSchemaException(
                    "A risk severity may not be 'Unknown'. It is the unset value, and a risk stored " +
                    "without a severity sorts as though it were the mildest.");
            }

            risks.Add(IdentifiedRisk.Create(AnalysisJson.RequiredString(entry, "description"), severity));
        }

        return risks;
    }
}
