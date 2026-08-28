namespace AI.Investment.Domain.Operations;

/// <summary>Whether a cycle is still moving, and if not, why it stopped.</summary>
/// <remarks>
/// Separate from <see cref="CycleStage"/> on purpose. The stage says how far the work got; the
/// status says whether the work is still happening. Folding them together would make "suspended at
/// Analyze" and "completed at Analyze" the same value, and those call for opposite responses.
/// </remarks>
public enum CycleStatus
{
    /// <summary>Not determined. A cycle in this state is not eligible to be picked up.</summary>
    Unknown = 0,

    /// <summary>Moving, or waiting for a worker to pick it up.</summary>
    Running = 1,

    /// <summary>
    /// Stopped on a budget, a limit or an escalation. It does not resume on its own: something a
    /// human decides has to change first.
    /// </summary>
    Suspended = 2,

    /// <summary>Reached <see cref="CycleStage.Record"/> and finished.</summary>
    Completed = 3,

    /// <summary>Stopped on an error it could not recover from.</summary>
    Failed = 4,
}
