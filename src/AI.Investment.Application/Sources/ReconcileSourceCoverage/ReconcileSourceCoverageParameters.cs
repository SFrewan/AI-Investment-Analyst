using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Sources.ReconcileSourceCoverage;

/// <summary>A proposed widening of a source's declared coverage, as the safety seam sees it.</summary>
/// <remarks>
/// <para>
/// The description names the categories being <em>added</em> rather than only the resulting set.
/// An audit record that listed the outcome alone would make a reader diff two lists to find out
/// what changed, and the question anyone asks of this record is which capability the platform
/// started trusting this source for, and when.
/// </para>
/// <para>
/// Both lists are captured here rather than read off <see cref="Source"/> when the description is
/// built. The source is the live aggregate and the execution mutates it, so a description that
/// counted its categories would report the state after the change as though it were the state
/// before - an audit trail that quietly agrees with whatever happened.
/// </para>
/// </remarks>
public sealed record ReconcileSourceCoverageParameters : IActionParameters
{
    public ReconcileSourceCoverageParameters(
        DataSource source,
        IReadOnlyList<DataCategory> added,
        IReadOnlyList<DataCategory> result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(result);

        Source = source;
        Added = added;
        Result = result;
    }

    public DataSource Source { get; }

    /// <summary>The categories this reconciliation adds. Never empty; nothing is ever removed.</summary>
    public IReadOnlyList<DataCategory> Added { get; }

    /// <summary>Everything the source will supply once this executes.</summary>
    public IReadOnlyList<DataCategory> Result { get; }

    public string Describe() =>
        $"Widen {Source.Id} ({Source.Authority}/{Source.Type}) for {Source.Region} to also supply " +
        $"[{string.Join(", ", Added)}], leaving {Result.Count} declared categories. " +
        "Licensing, activation, verification and reliability are unchanged.";
}
