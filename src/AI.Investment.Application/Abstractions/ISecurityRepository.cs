using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Abstractions;

/// <summary>Reads and writes securities and the identifier assertions they carry.</summary>
/// <remarks>
/// <para>
/// <strong>There is no <c>GetByTickerAsync</c>, and there never will be.</strong> The Security
/// Master's invariant is that a ticker is never a key, and a repository method taking one would
/// become that key within a release because it is the lookup everything already wants.
/// <see cref="FindByIdentifierAsync"/> takes a kind as well as a value, so a caller must say which
/// identifier system it is asking about and cannot silently mean "the ticker".
/// </para>
/// <para>
/// The lookup is what makes population idempotent: an identifier that already exists says the
/// security it belongs to already exists, so a second run finds it rather than creating a twin.
/// </para>
/// </remarks>
public interface ISecurityRepository
{
    Task<Security?> GetByIdAsync(SecurityId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The security carrying an assertion of this kind and value, or null when none does.
    /// </summary>
    /// <remarks>
    /// Returns a single security because the database refuses a second one: the identifier table
    /// is unique on (kind, value). Two issuers that genuinely shared a symbol at different times
    /// would be a real conflict, and it is better refused at the constraint than resolved by
    /// whichever row a query happened to return first.
    /// </remarks>
    Task<Security?> FindByIdentifierAsync(
        SecurityIdentifierKind kind,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The securities carrying an assertion of this kind, value and source that covers
    /// <paramref name="asOf"/>, in ascending id order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns every match rather than a single security, because more than one is a real state of
    /// the evidence that the caller has to be told about. The ordering is by id so that a repeated
    /// query returns the same list in the same order - a candidate list whose order came from the
    /// database's scan order would make an ambiguous answer non-reproducible.
    /// </para>
    /// <para>
    /// The interval test is applied in the database, not in memory: an implementation that loaded
    /// the identifier table and filtered it here would be correct and would stop being so at the
    /// first million rows.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<SecurityId>> FindByIdentifierAsAtAsync(
        SecurityIdentifierKind kind,
        string value,
        SourceId source,
        DateOnly asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many assertions of this kind are held, and how many of them carry this value.
    /// </summary>
    /// <remarks>
    /// Asked so that an empty resolution can say <em>which</em> kind of empty it is. No assertion of
    /// the kind at all means the platform holds no dated evidence for that identifier system -
    /// which is exactly the ticker position D6 records - while assertions existing but not for this
    /// value is an ordinary miss. Conflating them would report a structural gap as a lookup failure.
    /// </remarks>
    Task<(int OfKind, int OfKindAndValue)> CountIdentifierAssertionsAsync(
        SecurityIdentifierKind kind,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="source"/> asserts this kind and value over any interval at all.
    /// </summary>
    /// <remarks>
    /// Asked this way round on purpose. "Does someone else assert it" cannot separate the case where
    /// this source asserts it outside the instant from the case where this source never asserted it
    /// and another did - both would answer yes. "Does this source assert it at all" separates them.
    /// </remarks>
    Task<bool> AnyIdentifierFromSourceAsync(
        SecurityIdentifierKind kind,
        string value,
        SourceId source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a new security. Nothing is persisted until <see cref="IUnitOfWork.SaveChangesAsync"/>,
    /// which itself requires an authorised execution to be in progress.
    /// </summary>
    void Add(Security security);
}
