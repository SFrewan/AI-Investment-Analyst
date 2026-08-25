using AI.Investment.Domain.Exceptions;
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
/// </remarks>
public sealed record Provenance
{
    public const int MaxSourceIdLength = 200;

    private Provenance(
        string sourceId,
        Uri? sourceUrl,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        DateTime retrievedAtUtc)
    {
        SourceId = sourceId;
        SourceUrl = sourceUrl;
        AsOfUtc = asOfUtc;
        PublishedAtUtc = publishedAtUtc;
        RetrievedAtUtc = retrievedAtUtc;
    }

    /// <summary>
    /// Stable identifier of the origin: a provider name, a filing accession number, an article
    /// identifier, or - for values the system itself produced - an agent or service identifier.
    /// </summary>
    public string SourceId { get; }

    /// <summary>Where a human can go to check it, when such a place exists.</summary>
    public Uri? SourceUrl { get; }

    /// <summary>The period or instant the value describes.</summary>
    public DateTime AsOfUtc { get; }

    /// <summary>When the value became public knowledge. The only legitimate backtest filter.</summary>
    public DateTime PublishedAtUtc { get; }

    /// <summary>When this system fetched or produced it.</summary>
    public DateTime RetrievedAtUtc { get; }

    public static Provenance Create(
        string sourceId,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        DateTime retrievedAtUtc,
        Uri? sourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new DomainValidationException(
                nameof(sourceId),
                "Provenance requires a source identifier. A value with no stated origin cannot be audited.");
        }

        var trimmedSourceId = sourceId.Trim();

        if (trimmedSourceId.Length > MaxSourceIdLength)
        {
            throw new DomainValidationException(
                nameof(sourceId),
                $"A source identifier may not exceed {MaxSourceIdLength} characters.");
        }

        DateRange.EnsureUtc(asOfUtc, nameof(asOfUtc));
        DateRange.EnsureUtc(publishedAtUtc, nameof(publishedAtUtc));
        DateRange.EnsureUtc(retrievedAtUtc, nameof(retrievedAtUtc));

        return new Provenance(trimmedSourceId, sourceUrl, asOfUtc, publishedAtUtc, retrievedAtUtc);
    }

    /// <summary>
    /// Provenance for a value this system produced itself - a calculation, an interpretation or
    /// a prediction - rather than observed from outside.
    /// </summary>
    public static Provenance FromSystem(string producerId, DateTime asOfUtc, DateTime producedAtUtc) =>
        Create(producerId, asOfUtc, producedAtUtc, producedAtUtc);
}
