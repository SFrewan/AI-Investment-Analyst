using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Sources.RegisterKnownSources;

/// <summary>A proposed source registration, as the safety seam sees it.</summary>
/// <remarks>
/// The description written into the audit trail states the terms the source is being admitted
/// under, not merely its name. "Who let this source in, and on what basis?" is the question an
/// audit of the data plane starts with.
/// </remarks>
public sealed record RegisterSourceParameters : IActionParameters
{
    public RegisterSourceParameters(DataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Source = source;
    }

    public DataSource Source { get; }

    public string Describe() =>
        $"Register {Source.Id} ({Source.Authority}/{Source.Type}) for {Source.Region}, " +
        $"{Source.Categories.Count} categories, licensing [{Source.Licensing}], " +
        $"verification [{Source.Verification}], retention {Source.Licensing.Retention}. " +
        "Registered inactive.";
}
