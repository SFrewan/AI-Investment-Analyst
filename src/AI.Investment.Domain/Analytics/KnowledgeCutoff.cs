using System.Globalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// The instant beyond which the platform is not permitted to know anything.
/// </summary>
/// <remarks>
/// <para>
/// Every calculation carries one. In live operation it is the present; in a backtest it is a date
/// in the past, and the whole value of that backtest depends on the calculation being unable to
/// see past it. Look-ahead bias is not a bug that announces itself - it makes results better, which
/// is why it has to be structurally impossible rather than merely discouraged.
/// </para>
/// <para>
/// <strong>Publication, not retrieval, is the test.</strong> A filing published before the cutoff
/// was knowable to anyone at the cutoff, whether or not this platform had fetched it yet. Judging
/// admissibility by <c>RetrievedAtUtc</c> would make a historical result depend on the platform's
/// own fetch history, so replaying the same period after backfilling a source would produce a
/// different answer for reasons that have nothing to do with the world.
/// </para>
/// </remarks>
public sealed record KnowledgeCutoff
{
    private KnowledgeCutoff(DateTime asOfUtc) => AsOfUtc = asOfUtc;

    public DateTime AsOfUtc { get; }

    public static KnowledgeCutoff At(DateTime asOfUtc)
    {
        DateRange.EnsureUtc(asOfUtc, nameof(asOfUtc));

        return new KnowledgeCutoff(asOfUtc);
    }

    /// <summary>Whether information published at <paramref name="publishedAtUtc"/> was knowable.</summary>
    public bool Admits(DateTime publishedAtUtc)
    {
        DateRange.EnsureUtc(publishedAtUtc, nameof(publishedAtUtc));

        return publishedAtUtc <= AsOfUtc;
    }

    /// <summary>Whether the evidence behind <paramref name="provenance"/> was knowable.</summary>
    public bool Admits(Provenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        return Admits(provenance.PublishedAtUtc);
    }

    public override string ToString() => AsOfUtc.ToString("O", CultureInfo.InvariantCulture);
}
