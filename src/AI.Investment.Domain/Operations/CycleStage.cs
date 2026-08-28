namespace AI.Investment.Domain.Operations;

/// <summary>
/// The stages of one pass of the operating loop, in the order they must occur.
/// </summary>
/// <remarks>
/// <para>
/// The numeric order is the transition rule. A cycle advances to the next stage or stays where it
/// is; it never skips and never goes back. That is what makes a persisted stage a resumption point:
/// a worker picking up a crashed cycle knows exactly what has happened and exactly what has not,
/// without having to reconstruct it from side effects.
/// </para>
/// <para>
/// <see cref="Unknown"/> is zero so that a default-initialised or badly deserialised cycle is not
/// mistaken for one that has finished discovering and is ready to act.
/// </para>
/// </remarks>
public enum CycleStage
{
    Unknown = 0,
    Discover = 1,
    Collect = 2,
    Validate = 3,
    Analyze = 4,
    Identify = 5,
    Calculate = 6,
    AssessRisk = 7,
    Rank = 8,
    ProposeAction = 9,
    PolicyGate = 10,
    ExecuteOrEscalate = 11,
    Monitor = 12,
    MeasureOutcome = 13,
    Record = 14,
}

/// <summary>The declared order of <see cref="CycleStage"/>, and the only permitted transitions.</summary>
public static class CycleStages
{
    private static readonly CycleStage[] OrderedStages =
    [
        CycleStage.Discover,
        CycleStage.Collect,
        CycleStage.Validate,
        CycleStage.Analyze,
        CycleStage.Identify,
        CycleStage.Calculate,
        CycleStage.AssessRisk,
        CycleStage.Rank,
        CycleStage.ProposeAction,
        CycleStage.PolicyGate,
        CycleStage.ExecuteOrEscalate,
        CycleStage.Monitor,
        CycleStage.MeasureOutcome,
        CycleStage.Record,
    ];

    /// <summary>The stages in order. The first is where a cycle starts, the last where it ends.</summary>
    public static IReadOnlyList<CycleStage> Ordered => OrderedStages;

    public static CycleStage First => CycleStage.Discover;

    public static CycleStage Last => CycleStage.Record;

    /// <summary>The stage that follows, or null when there is none.</summary>
    public static CycleStage? Next(CycleStage stage)
    {
        if (stage == CycleStage.Unknown || stage == Last)
        {
            return null;
        }

        var index = Array.IndexOf(OrderedStages, stage);

        return index < 0 || index + 1 >= OrderedStages.Length ? null : OrderedStages[index + 1];
    }
}
