namespace AI.Investment.Domain.Sources;

/// <summary>
/// How well corroborated a piece of information is.
/// </summary>
/// <remarks>
/// <para>
/// Orthogonal to the epistemic kind carried by <c>Claim</c>. A claim's KIND says how the system
/// came to believe it - observed, calculated, interpreted, predicted. This says how many
/// independent places agree. A fact can be <see cref="Unverified"/>; an interpretation can rest
/// on <see cref="Confirmed"/> evidence.
/// </para>
/// <para>
/// Phase 2 models the states and nothing more. Deciding which source wins a
/// <see cref="Conflicting"/> pair is a resolution problem, and building a clever resolver before
/// there is real conflicting data to study would be guessing. The deterministic ordering in
/// <see cref="SourceRanking"/> is the foundation that resolver will use.
/// </para>
/// </remarks>
public enum ConfirmationState
{
    /// <summary>
    /// Reported by one source that cannot confirm on its own. Must never be silently promoted to
    /// a trusted fact.
    /// </summary>
    Unverified = 0,

    /// <summary>Corroborated, but by fewer independent sources than the policy requires.</summary>
    PartiallyConfirmed = 1,

    /// <summary>Satisfies the verification policy of the sources involved.</summary>
    Confirmed = 2,

    /// <summary>
    /// Sources disagree. Never silently resolved by picking one - a conflict is information, and
    /// often the most valuable information available.
    /// </summary>
    Conflicting = 3,

    /// <summary>
    /// Replaced by a later revision - a restated figure, an amended filing. Retained rather than
    /// deleted: knowing what the system believed at the time is what makes a historical decision
    /// reconstructable.
    /// </summary>
    Superseded = 4,
}
