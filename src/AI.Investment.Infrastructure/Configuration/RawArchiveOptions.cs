using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>Where archived provider responses are kept.</summary>
/// <remarks>
/// <para>
/// <strong>Retention is deliberately absent from this type.</strong> The archive currently deletes
/// nothing, because storing every response is what makes the phase's exit criterion - any analysis
/// replays byte-identically - achievable at all. That is also unbounded growth, and some provider
/// licences cap how long data may be kept, so a retention policy is a real decision with legal as
/// well as storage consequences. It is being taken deliberately rather than defaulted here; until
/// then, nothing is deleted, which is the direction that cannot lose evidence.
/// </para>
/// </remarks>
public sealed class RawArchiveOptions
{
    public const string SectionName = "RawArchive";

    /// <summary>
    /// Root directory for the content-addressed store.
    /// </summary>
    /// <remarks>
    /// A local path suits a single instance. A shared or object store is the obvious later
    /// substitution and needs no change above <c>IRawResponseArchive</c>.
    /// </remarks>
    [Required]
    [MinLength(1)]
    public string RootPath { get; init; } = "archive";
}
