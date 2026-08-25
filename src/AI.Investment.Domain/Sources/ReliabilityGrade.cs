namespace AI.Investment.Domain.Sources;

/// <summary>
/// How well a source has performed in practice.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Distinct from <see cref="SourceAuthority"/>, and the distinction matters.</strong>
/// Authority is what a source IS - a regulator is primary by definition. Reliability is what a
/// source has DONE: does its feed arrive on time, are its figures later restated, does it
/// contradict the filing it claims to summarise. A primary source with a broken feed is
/// authoritative and unreliable at once.
/// </para>
/// <para>
/// <see cref="Unrated"/> is the default and it is not a soft "probably fine". A grade is earned
/// by measurement against outcomes - the same principle applied to autonomy, for the same reason:
/// an assessment nobody has checked is an assertion. The measurement machinery arrives with the
/// evaluation phase; until then almost every source is legitimately unrated, and code that needs
/// a guarantee should read <see cref="SourceAuthority"/> and
/// <see cref="VerificationPolicy"/> instead.
/// </para>
/// </remarks>
public enum ReliabilityGrade
{
    /// <summary>Not yet measured. The honest default.</summary>
    Unrated = 0,

    /// <summary>Frequent gaps, delays or corrections. Corroborate before relying on it.</summary>
    Poor = 1,

    /// <summary>Usable with known limitations.</summary>
    Fair = 2,

    /// <summary>Consistent, timely, rarely restated.</summary>
    Good = 3,

    /// <summary>Measured over a meaningful period with no material defect.</summary>
    Excellent = 4,
}
