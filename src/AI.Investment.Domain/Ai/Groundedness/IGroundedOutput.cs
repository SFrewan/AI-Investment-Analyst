namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// Implemented by every agent output that a groundedness check can be run against.
/// </summary>
/// <remarks>
/// <para>
/// Two members, and the split between them is the whole design. <see cref="AssertedFigures"/> is
/// where every number the agent wants to state must live, named and ideally cited.
/// <see cref="NarrativeFragments"/> is every piece of prose the agent wrote, which the validator
/// scans for numerals that were smuggled past the structured fields.
/// </para>
/// <para>
/// Without the second, the first is trivially bypassed: an agent that puts nothing in its figure
/// list and writes "margins improved to 42%" in its summary passes a structural check while making
/// up a number. The prompts therefore instruct agents to keep prose free of figures, and the
/// scanner enforces it.
/// </para>
/// </remarks>
public interface IGroundedOutput
{
    /// <summary>Every number the agent states, named and optionally cited.</summary>
    IReadOnlyList<AssertedFigure> AssertedFigures();

    /// <summary>Every piece of free text the agent wrote, for the numeral backstop.</summary>
    IReadOnlyList<string> NarrativeFragments();
}
