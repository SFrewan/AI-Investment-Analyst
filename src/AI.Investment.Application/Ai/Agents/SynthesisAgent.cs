using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>Writes the account a human actually reads, from findings that already survived validation.</summary>
/// <remarks>
/// <para>
/// Stage 5 of the pipeline, and the only agent that sees other agents' work. It sees it after
/// Stage 4, never before: a specialist whose output failed the groundedness check is excluded
/// entirely rather than passed along with a caveat, because a caveat in a prompt is a suggestion and
/// the resulting narrative is what people quote.
/// </para>
/// <para>
/// It is checked against the same bundle as everything else. The agent whose job is to summarise is
/// the one with the most room to round a figure into something tidier, so it gets no relaxation.
/// </para>
/// </remarks>
public sealed class SynthesisAgent : AnalysisAgent<SynthesisInput, AnalysisSynthesis>
{
    public SynthesisAgent(IChatModel model, IPromptStore prompts)
        : base(model, prompts)
    {
    }

    public override AgentId AgentId { get; } =
        Domain.Ai.AgentId.Create("synthesis");

    public override string Version => "1.0";

    public override PromptRef Prompt { get; } = PromptRef.Create("synthesist", "analysis-synthesis", 1, 0);

    protected override int MaxOutputTokens => 1600;

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
              "required": ["narrative", "stance", "key_points", "figures"],
              "properties": {
                "narrative": { "type": "string" },
                "stance": { "enum": ["Negative", "Cautious", "Neutral", "Constructive"] },
                "key_points": { "type": "array", "items": { "type": "string" } },
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

    protected override EvidenceBundle EvidenceFor(SynthesisInput input) => input.Bundle;

    protected override string RenderInput(SynthesisInput input, EvidenceBundle bundle)
    {
        var builder = new StringBuilder();

        builder.Append(EvidenceRenderer.Render(bundle)).Append('\n');
        builder.Append("<findings>\n");
        builder.Append(
            "Each finding below was produced by a specialist agent and has already been checked " +
            "against the evidence above. Use only these findings and that evidence.\n");

        foreach (var finding in input.Findings)
        {
            builder.Append(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"agent={finding.Agent} confidence={finding.Confidence.Value:0.##}\n"));
            builder.Append("  summary: ").Append(finding.Summary).Append('\n');

            foreach (var point in finding.Points)
            {
                builder.Append("  point: ").Append(point).Append('\n');
            }

            foreach (var figure in finding.Figures)
            {
                var cite = figure.CitedLabel ?? "-";

                builder.Append(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  figure: {figure.Name}={figure.Value} cite={cite}\n"));
            }
        }

        builder.Append("</findings>");

        return builder.ToString();
    }

    protected override AnalysisSynthesis ReadAnalysis(JsonElement analysis, EvidenceBundle bundle)
    {
        var stance = AnalysisJson.RequiredEnum<AnalysisStance>(analysis, "stance");

        if (stance == AnalysisStance.Unknown)
        {
            throw new AgentSchemaException(
                "'stance' may not be 'Unknown'. It is the unset value, and reporting it would record " +
                "a position the agent never took.");
        }

        return new AnalysisSynthesis(
            AnalysisJson.RequiredString(analysis, "narrative"),
            stance,
            AnalysisJson.StringArray(analysis, "key_points"),
            AnalysisJson.Figures(analysis, "figures", bundle));
    }
}
