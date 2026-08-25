namespace AI.Investment.Domain.Sources;

/// <summary>
/// How much weight a source's claim carries on its own.
/// </summary>
/// <remarks>
/// <para>
/// The platform must never treat every place information came from as equivalent. A figure in a
/// regulatory filing and the same figure in a forum post are not the same kind of thing, and a
/// system that flattens the difference will eventually act on the forum post.
/// </para>
/// <para>
/// Ordering is meaningful and is relied on by <see cref="SourceRanking"/>: higher values
/// outrank lower ones when sources disagree.
/// </para>
/// </remarks>
public enum SourceAuthority
{
    /// <summary>
    /// Provenance is unknown or the source has not been assessed. Cannot produce a fact on its
    /// own - see <see cref="VerificationPolicy"/>. The default for anything not explicitly
    /// registered, because absence of assessment must never read as trust.
    /// </summary>
    Unverified = 0,

    /// <summary>
    /// A reputable organisation reporting on, or derived from, someone else's primary record:
    /// financial news, research providers, data vendors that redistribute exchange data.
    /// Accurate most of the time, and one transcription step removed from the truth.
    /// </summary>
    Secondary = 1,

    /// <summary>
    /// The originating record itself: a regulatory filing, a government or central-bank release,
    /// an exchange's own market data, a company's own investor-relations publication. The thing
    /// a secondary source would be quoting.
    /// </summary>
    Primary = 2,
}
