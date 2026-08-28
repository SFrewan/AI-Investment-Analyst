using System.Globalization;
using System.Text.Json;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Ai;

/// <summary>
/// The part of every agent's answer that is the same whatever the agent analyses.
/// </summary>
/// <remarks>
/// <para>
/// Every response carries a refusal flag, a confidence, a limitations list and an
/// agent-specific <c>analysis</c> object. Putting the first three in a shared envelope means the
/// refusal path is written once and cannot be forgotten by a new agent - and an agent that could
/// forget how to say "I don't know" is an agent that will invent something instead.
/// </para>
/// <para>
/// Parsed by hand out of a <see cref="JsonElement"/> rather than deserialised onto a DTO. The
/// parser <em>is</em> the schema enforcement in this phase: it states exactly which fields are
/// required and what they may contain, and it fails loudly rather than leaving a missing field as a
/// default that reads like an answer.
/// </para>
/// </remarks>
public sealed record AgentEnvelope
{
    private readonly List<string> _limitations;

    private AgentEnvelope(
        bool refused,
        string? refusalReason,
        Confidence? confidence,
        List<string> limitations,
        JsonElement analysis)
    {
        Refused = refused;
        RefusalReason = refusalReason;
        Confidence = confidence;
        _limitations = limitations;
        Analysis = analysis;
    }

    public bool Refused { get; }

    public string? RefusalReason { get; }

    /// <summary>Present unless the agent refused.</summary>
    public Confidence? Confidence { get; }

    public IReadOnlyList<string> Limitations => _limitations;

    /// <summary>The agent-specific payload, for the derived agent to read.</summary>
    public JsonElement Analysis { get; }

    public static AgentEnvelope Parse(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new AgentSchemaException("The answer was not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AgentSchemaException(
                    $"The answer must be a JSON object; received {root.ValueKind}.");
            }

            var refused = ReadBoolean(root, "refused");
            var reason = ReadOptionalString(root, "refusal_reason");
            var limitations = ReadStringArray(root, "limitations");

            if (refused)
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new AgentSchemaException(
                        "A refusal must state a reason. An unexplained refusal cannot be acted on and " +
                        "cannot be told apart from a defect.");
                }

                return new AgentEnvelope(true, reason.Trim(), null, limitations, default);
            }

            var confidence = ReadConfidence(root);

            if (!root.TryGetProperty("analysis", out var analysis) ||
                analysis.ValueKind != JsonValueKind.Object)
            {
                throw new AgentSchemaException("The answer must carry an 'analysis' object.");
            }

            return new AgentEnvelope(false, null, confidence, limitations, analysis.Clone());
        }
    }

    private static bool ReadBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            throw new AgentSchemaException($"The answer is missing the required '{name}' field.");
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new AgentSchemaException($"'{name}' must be a boolean; received {element.ValueKind}."),
        };
    }

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static Confidence ReadConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out var element))
        {
            throw new AgentSchemaException(
                "The answer is missing 'confidence'. A judgement presented without stated uncertainty " +
                "is indistinguishable downstream from a measured fact.");
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
        {
            throw new AgentSchemaException(
                $"'confidence' must be a number between 0 and 1; received {element.ValueKind}.");
        }

        if (value is < 0m or > 1m)
        {
            throw new AgentSchemaException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'confidence' must be between 0 and 1; received {value}."));
        }

        return Confidence.Create(value);
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var values = new List<string>();

        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return values;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new AgentSchemaException($"'{name}' must be an array of strings.");
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                throw new AgentSchemaException($"'{name}' must contain only strings.");
            }

            var text = entry.GetString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text.Trim());
            }
        }

        return values;
    }
}
