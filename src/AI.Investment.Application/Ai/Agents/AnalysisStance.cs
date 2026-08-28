namespace AI.Investment.Application.Ai.Agents;

/// <summary>How the synthesis reads the picture overall.</summary>
/// <remarks>
/// A stance, not a recommendation, and certainly not an instruction. Nothing downstream may treat
/// this as an input to whether anything executes: opportunities are assembled deterministically and
/// authorised by the policy engine, and a model's overall impression has no standing in either.
/// </remarks>
public enum AnalysisStance
{
    Unknown = 0,
    Negative = 1,
    Cautious = 2,
    Neutral = 3,
    Constructive = 4,
}
