using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Sources.ActivateSource;

/// <summary>A proposed source activation, as the safety seam sees it.</summary>
/// <remarks>
/// Activation is the moment a source becomes usable, so the audit record states what is being
/// switched on and under which terms - the same facts an operator would want when asking why data
/// from somewhere unexpected started arriving.
/// </remarks>
public sealed record ActivateSourceParameters : IActionParameters
{
    public ActivateSourceParameters(DataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Source = source;
    }

    public DataSource Source { get; }

    public string Describe() =>
        $"Activate {Source.Id} ({Source.Authority}/{Source.Type}) for {Source.Region}; " +
        $"licensing [{Source.Licensing}], retention {Source.Licensing.Retention}.";
}
