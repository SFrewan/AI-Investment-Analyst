using System.Globalization;
using System.Text.Json;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai;

/// <summary>
/// Reads the fields of an agent's answer, refusing anything that is not exactly what was asked for.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written readers rather than attribute-driven deserialisation, and the difference is not
/// stylistic. A deserialiser fills a missing field with a default, and a default is a value: an
/// absent margin becomes zero, an absent list becomes empty, and both read downstream as findings
/// rather than omissions. These readers make absence an error.
/// </para>
/// <para>
/// This is also where the schema is genuinely enforced. The JSON schema each agent publishes is
/// sent to the provider so that a capable one can constrain generation, but this platform never
/// relies on that: the provider is outside the trust boundary and the schema it was handed may not
/// be the schema it applied.
/// </para>
/// </remarks>
public static class AnalysisJson
{
    public static string RequiredString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            throw new AgentSchemaException($"'{name}' is required and must be a string.");
        }

        var value = element.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AgentSchemaException($"'{name}' may not be empty.");
        }

        return value.Trim();
    }

    public static List<string> StringArray(JsonElement owner, string name, int minimumCount = 0)
    {
        var values = new List<string>();

        if (owner.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array)
        {
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
        }
        else if (element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new AgentSchemaException($"'{name}' must be an array of strings.");
        }

        if (values.Count < minimumCount)
        {
            throw new AgentSchemaException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' must contain at least {minimumCount} entries; received {values.Count}."));
        }

        return values;
    }

    /// <summary>
    /// Reads a named enum value, refusing anything outside the permitted set.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on the way in, because a model writing <c>"Positive"</c> where the enum says
    /// <c>positive</c> is not a disagreement about meaning. An unrecognised word is a schema failure
    /// rather than a fallback to the default member, since the default of every enum in this
    /// codebase is the one that means "unset".
    /// </remarks>
    public static TEnum RequiredEnum<TEnum>(JsonElement owner, string name)
        where TEnum : struct, Enum
    {
        var raw = RequiredString(owner, name);

        if (!Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            throw new AgentSchemaException(
                $"'{name}' must be one of {string.Join(", ", Enum.GetNames<TEnum>())}; received '{raw}'.");
        }

        return value;
    }

    /// <summary>
    /// Reads the list of figures the agent states, resolving each citation to the claim it names.
    /// </summary>
    /// <remarks>
    /// A citation that does not resolve is carried through as an unresolved label rather than
    /// dropped. Dropping it would turn "cited a claim that does not exist" into "cited nothing",
    /// which is a materially weaker finding about the answer.
    /// </remarks>
    public static List<AssertedFigure> Figures(JsonElement owner, string name, EvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var figures = new List<AssertedFigure>();

        if (!owner.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return figures;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new AgentSchemaException($"'{name}' must be an array of figure objects.");
        }

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new AgentSchemaException($"'{name}' must contain only objects.");
            }

            figures.Add(ReadFigure(entry, bundle));
        }

        return figures;
    }

    private static AssertedFigure ReadFigure(JsonElement entry, EvidenceBundle bundle)
    {
        var figureName = RequiredString(entry, "name");

        if (!entry.TryGetProperty("value", out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.Number ||
            !valueElement.TryGetDecimal(out var value))
        {
            throw new AgentSchemaException($"Figure '{figureName}' must carry a numeric 'value'.");
        }

        var isPercentage = entry.TryGetProperty("is_percentage", out var percentageElement) &&
                           percentageElement.ValueKind == JsonValueKind.True;

        var label = entry.TryGetProperty("cite", out var citeElement) &&
                    citeElement.ValueKind == JsonValueKind.String
            ? citeElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(label))
        {
            return AssertedFigure.Create(figureName, value, null, isPercentage);
        }

        return bundle.TryResolveLabel(label, out var item) && item is not null
            ? AssertedFigure.Create(figureName, value, item.Claim.Id, isPercentage, label)
            : AssertedFigure.Create(figureName, value, null, isPercentage, label);
    }
}
