using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Evidence;

/// <summary>
/// Where a value came from, and the three separate times that matter about it.
/// </summary>
/// <remarks>
/// <para>
/// The three timestamps are the most consequential design decision in this file, and possibly
/// in the whole data model:
/// </para>
/// <list type="bullet">
/// <item><see cref="AsOfUtc"/> - the period the value DESCRIBES. For a Q4 revenue figure, the
/// end of Q4.</item>
/// <item><see cref="PublishedAtUtc"/> - when it BECAME PUBLIC. For that same Q4 figure, the day
/// the filing was released, which may be months later.</item>
/// <item><see cref="RetrievedAtUtc"/> - when THIS SYSTEM fetched it.</item>
/// </list>
/// <para>
/// Every historical query - backtest, outcome measurement, shadow-mode comparison - must filter
/// on <see cref="PublishedAtUtc"/> and never on <see cref="AsOfUtc"/>. Filtering on the wrong
/// one produces look-ahead bias: an evaluation of a decision made in January silently uses
/// figures that were not public until March, and every strategy appears profitable. It cannot
/// be corrected afterwards, because by then the history has been stored without the distinction.
/// That is why all three exist from the first version of this type rather than being added when
/// backtesting is built.
/// </para>
/// <para>
/// Note that this type does NOT enforce an ordering between the three. The ordering rule is
/// real but applies only to observations of the world, so it is enforced in <see cref="Claim"/>
/// for <see cref="Enums.ClaimKind.Fact"/>. A prediction legitimately has an
/// <see cref="AsOfUtc"/> in the future, which would fail any ordering rule imposed here.
/// </para>
/// <para>
/// <strong>Origin and locator are separate</strong> (Phase 2 stage 2). <see cref="SourceId"/>
/// names a <em>registered</em> source - <c>sec-edgar</c> - and is the key into the source
/// registry, where authority, licensing and cadence live. <see cref="SourceRecordId"/> is the
/// locator <em>within</em> that source - a filing accession number, an article identifier, a
/// vendor's row id. Before this split both lived in one free-form string, which meant
/// <c>"sec-edgar:0000320193-26-000001"</c> could not be looked up in the registry, and two
/// claims from the same source did not compare equal on their origin.
/// </para>
/// </remarks>
public sealed record Provenance
{
    /// <summary>
    /// Upper bound on <see cref="SourceRecordId"/>. Filing accession numbers, article GUIDs and
    /// vendor row identifiers all sit far below this; it exists to bound the column, not to
    /// express a rule.
    /// </summary>
    /// <remarks>
    /// This constant used to bound the whole source identifier at 200 characters. That
    /// responsibility moved to <see cref="Sources.SourceId"/>, which enforces a 64-character
    /// slug with a restricted character set, so the registry key is now a checkable identity
    /// rather than arbitrary text.
    /// </remarks>
    public const int MaxSourceRecordIdLength = 200;

    private Provenance(
        SourceId sourceId,
        string? sourceRecordId,
        Uri? sourceUrl,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        DateTime retrievedAtUtc)
    {
        SourceId = sourceId;
        SourceRecordId = sourceRecordId;
        SourceUrl = sourceUrl;
        AsOfUtc = asOfUtc;
        PublishedAtUtc = publishedAtUtc;
        RetrievedAtUtc = retrievedAtUtc;
    }

    /// <summary>
    /// The registered origin. A key into the source registry, not free text.
    /// </summary>
    public SourceId SourceId { get; }

    /// <summary>
    /// The locator within that source - filing accession number, article identifier, vendor row
    /// id - when the source has a meaningful one. Null when it does not.
    /// </summary>
    public string? SourceRecordId { get; }

    /// <summary>Where a human can go to check it, when such a place exists.</summary>
    public Uri? SourceUrl { get; }

    /// <summary>The period or instant the value describes.</summary>
    public DateTime AsOfUtc { get; }

    /// <summary>When the value became public knowledge. The only legitimate backtest filter.</summary>
    public DateTime PublishedAtUtc { get; }

    /// <summary>When this system fetched or produced it.</summary>
    public DateTime RetrievedAtUtc { get; }

    public static Provenance Create(
        SourceId sourceId,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        DateTime retrievedAtUtc,
        string? sourceRecordId = null,
        Uri? sourceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        DateRange.EnsureUtc(asOfUtc, nameof(asOfUtc));
        DateRange.EnsureUtc(publishedAtUtc, nameof(publishedAtUtc));
        DateRange.EnsureUtc(retrievedAtUtc, nameof(retrievedAtUtc));

        return new Provenance(
            sourceId,
            NormaliseRecordId(sourceRecordId),
            sourceUrl,
            asOfUtc,
            publishedAtUtc,
            retrievedAtUtc);
    }

    /// <summary>
    /// Convenience overload that parses the source identifier.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="Sources.SourceId.Create"/>, so an identifier that the registry
    /// could never hold is rejected here rather than becoming an unresolvable origin. A value
    /// with no stated origin cannot be audited; a value whose stated origin cannot be looked up
    /// is barely better.
    /// </remarks>
    public static Provenance Create(
        string sourceId,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        DateTime retrievedAtUtc,
        string? sourceRecordId = null,
        Uri? sourceUrl = null) =>
        Create(
            Sources.SourceId.Create(sourceId),
            asOfUtc,
            publishedAtUtc,
            retrievedAtUtc,
            sourceRecordId,
            sourceUrl);

    /// <summary>
    /// Provenance for a value this system produced itself - a calculation, an interpretation or
    /// a prediction - rather than observed from outside.
    /// </summary>
    /// <remarks>
    /// The producer is still a <see cref="Sources.SourceId"/>, and is expected to be registered
    /// like any other origin (see <see cref="SourceType.InternalDerivation"/>). An AI
    /// interpretation whose producer cannot be identified is exactly the kind of value that
    /// later becomes impossible to explain.
    /// </remarks>
    public static Provenance FromSystem(SourceId producerId, DateTime asOfUtc, DateTime producedAtUtc) =>
        Create(producerId, asOfUtc, producedAtUtc, producedAtUtc);

    /// <inheritdoc cref="FromSystem(SourceId, DateTime, DateTime)"/>
    public static Provenance FromSystem(string producerId, DateTime asOfUtc, DateTime producedAtUtc) =>
        Create(producerId, asOfUtc, producedAtUtc, producedAtUtc);

    public override string ToString() =>
        SourceRecordId is null ? SourceId.Value : $"{SourceId.Value}/{SourceRecordId}";

    private static string? NormaliseRecordId(string? sourceRecordId)
    {
        if (string.IsNullOrWhiteSpace(sourceRecordId))
        {
            return null;
        }

        var trimmed = sourceRecordId.Trim();

        if (trimmed.Length > MaxSourceRecordIdLength)
        {
            throw new DomainValidationException(
                nameof(sourceRecordId),
                $"A source record identifier may not exceed {MaxSourceRecordIdLength} characters.");
        }

        return trimmed;
    }
}
