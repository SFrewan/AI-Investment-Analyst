using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// The exact model that produced an output - provider, model name and pinned version.
/// </summary>
/// <remarks>
/// <para>
/// Pinning matters more than it looks. Providers revise models behind a stable alias, and an
/// unannounced revision is indistinguishable, in outcome data, from a change in the world or a
/// drift in strategy. Recording the pinned version is what makes it possible to say afterwards
/// which of the three happened.
/// </para>
/// <para>
/// This type names a model. It does not know how to call one: that is a port in the application
/// layer, implemented outside the domain, precisely so nothing in here can reach a provider.
/// </para>
/// </remarks>
public sealed record ModelRef
{
    public const int MaxSegmentLength = 80;

    /// <summary>
    /// The model identity used when no provider is configured. Present so that a run without a
    /// provider still records what it did <em>not</em> use, rather than leaving a null that reads
    /// like an omission.
    /// </summary>
    public static ModelRef None { get; } = new("none", "none", "none");

    private ModelRef(string provider, string model, string version)
    {
        Provider = provider;
        Model = model;
        Version = version;
    }

    /// <summary>The provider, such as <c>anthropic</c> or <c>openai</c>.</summary>
    public string Provider { get; }

    /// <summary>The model family or name as the provider states it.</summary>
    public string Model { get; }

    /// <summary>The pinned version or snapshot identifier.</summary>
    public string Version { get; }

    /// <summary>True when this names no model, because no provider was configured.</summary>
    public bool IsNone => this == None;

    public static ModelRef Create(string provider, string model, string version) =>
        new(
            Validate(provider, nameof(provider)),
            Validate(model, nameof(model)),
            Validate(version, nameof(version)));

    public override string ToString() => $"{Provider}/{Model}@{Version}";

    private static string Validate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                parameterName,
                "A model reference must name the provider, the model and the pinned version. " +
                "An unpinned model makes a provider-side revision indistinguishable from strategy drift.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxSegmentLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"A model reference segment may not exceed {MaxSegmentLength} characters. Received '{value}'.");
        }

        return trimmed;
    }
}
