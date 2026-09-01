using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;

namespace AI.Investment.Application.Opportunities;

/// <summary>One stored close, kept beside the identity that will cite it.</summary>
/// <remarks>
/// The observation's identifier travels with the number because an opportunity's evidence is a list
/// of claim identifiers that must resolve back to stored observations - that resolution is what
/// Phase 7's guard uses to decide when a prediction became knowable, and a discoverer that minted
/// fresh identifiers would produce opportunities the measurement is obliged to refuse.
/// </remarks>
public sealed record PricedObservation(
    ObservationId Id,
    DateTime SessionCloseUtc,
    decimal Close,
    Provenance Provenance)
{
    /// <summary>The evidence identifier an opportunity cites this observation by.</summary>
    public ClaimId Citation => ClaimId.Create(Id.Value);

    /// <summary>The shape the domain screen reads.</summary>
    public ClosingPrice ToClosingPrice() => new(SessionCloseUtc, Close);
}

/// <summary>
/// A split-adjusted window, or a refusal saying why the series could not be trusted.
/// </summary>
/// <remarks>
/// A result rather than a list, because an empty list and a refusal are the same shape and mean
/// opposite things: "this instrument has no history yet" and "this instrument has history the
/// platform will not screen". A caller that cannot tell them apart will eventually report the
/// second as the first.
/// </remarks>
public sealed record AdjustedPriceSeries
{
    private AdjustedPriceSeries(
        IReadOnlyList<PricedObservation> observations,
        SeriesRefusal refusal,
        string explanation)
    {
        Observations = observations;
        Refusal = refusal;
        Explanation = explanation;
    }

    public IReadOnlyList<PricedObservation> Observations { get; }

    public SeriesRefusal Refusal { get; }

    /// <summary>Why it was refused, in the words an operator reads. Empty when it was not.</summary>
    public string Explanation { get; }

    public bool IsUsable => Refusal == SeriesRefusal.None;

    public static AdjustedPriceSeries Usable(IReadOnlyList<PricedObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return new AdjustedPriceSeries(observations, SeriesRefusal.None, string.Empty);
    }

    public static AdjustedPriceSeries Refused(SeriesRefusal refusal, string explanation) =>
        new([], refusal, explanation);
}

/// <summary>
/// Reads one instrument's closing prices as they stood at a moment, restatements resolved.
/// </summary>
/// <remarks>
/// <para>
/// One place, used by the discoverer that screens the series and by the work plan that scores the
/// candidate, so the two cannot disagree about what the series was. Two readings of the same prices
/// that resolved restatements differently would produce a score computed over evidence the
/// opportunity does not cite, and nothing downstream would be able to tell.
/// </para>
/// <para>
/// <strong>Point-in-time by construction.</strong> The store's own read admits an observation only
/// when it was published at or before the instant asked for, so this class never sees a price the
/// platform could not have known. It adds one thing on top: where several rows describe the same
/// session, the one that was published latest by that instant wins. A figure published and then
/// corrected appears twice, and taking the newest row outright would quietly use a correction that
/// had not been made yet.
/// </para>
/// <para>
/// Non-numeric values under the same attribute are dropped rather than parsed. A close stored as
/// text is a normalisation defect; reading it as a number here would hide the defect behind a
/// plausible series.
/// </para>
/// </remarks>
public sealed class PriceSeriesReader
{
    private readonly IObservationStore _observations;

    public PriceSeriesReader(IObservationStore observations) =>
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));

    /// <summary>
    /// The most recent <paramref name="maxSessions"/> closes that were public at
    /// <paramref name="asAtUtc"/>, oldest first, exactly as stored.
    /// </summary>
    /// <remarks>
    /// Raw, in the shares each session was quoted in. Correct for the portfolio's use, which wants
    /// the latest price and nothing historical; wrong for anything that compares two sessions
    /// across a split. Use <see cref="ReadAdjustedAsync"/> for that.
    /// </remarks>
    public async Task<IReadOnlyList<PricedObservation>> ReadAsync(
        IngestionSubject subject,
        string attribute,
        int maxSessions,
        DateTime asAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);

        if (maxSessions < 1)
        {
            return [];
        }

        var stored = await _observations
            .ForSubjectAsync(subject, asAtUtc, cancellationToken)
            .ConfigureAwait(false);

        return Resolve(stored, attribute.Trim(), maxSessions);
    }

    /// <summary>
    /// The same window, restated in the shares in issue at the end of it, or a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What any comparison between two sessions has to read. The stored close is the raw one, so a
    /// split leaves a step in the series that the price-recovery screen would score as a
    /// spectacular fall - the one failure in this platform that produces a confident wrong number
    /// rather than a refusal.
    /// </para>
    /// <para>
    /// Splits are read from the observation store under their own attribute, through the same
    /// point-in-time read as the prices, so a split the platform did not yet know about on the
    /// instant being asked about is not used to restate that instant's history. That matters for
    /// replay: a backtest run as at a past date must see the series that was visible then.
    /// </para>
    /// <para>
    /// <strong>The caller cannot ignore a refusal.</strong> It comes back as a result rather than
    /// an empty list precisely so that "we will not screen this" is distinguishable from "there is
    /// nothing here", which are the same shape and mean opposite things.
    /// </para>
    /// </remarks>
    public async Task<AdjustedPriceSeries> ReadAdjustedAsync(
        IngestionSubject subject,
        string priceAttribute,
        string splitAttribute,
        int maxSessions,
        DateTime asAtUtc,
        decimal maxUnexplainedMove,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceAttribute);
        ArgumentException.ThrowIfNullOrWhiteSpace(splitAttribute);

        if (maxSessions < 1)
        {
            return AdjustedPriceSeries.Usable([]);
        }

        var stored = await _observations
            .ForSubjectAsync(subject, asAtUtc, cancellationToken)
            .ConfigureAwait(false);

        var observations = Resolve(stored, priceAttribute.Trim(), maxSessions);
        var splits = Splits(stored, splitAttribute.Trim());

        var adjustment = SplitAdjustment.Apply(
            observations.Select(o => o.ToClosingPrice()).ToList(),
            splits,
            maxUnexplainedMove);

        if (!adjustment.IsUsable)
        {
            return AdjustedPriceSeries.Refused(adjustment.Refusal, adjustment.Explanation);
        }

        // Re-attach each adjusted close to the observation it came from, so the evidence an
        // opportunity cites still resolves to a stored row. The screen sees a restated number; the
        // audit trail still points at the raw one it was derived from.
        var restated = new List<PricedObservation>(observations.Count);

        for (var i = 0; i < observations.Count; i++)
        {
            restated.Add(observations[i] with { Close = adjustment.Prices[i].Close });
        }

        return AdjustedPriceSeries.Usable(restated);
    }

    /// <summary>
    /// Every split the platform knew about at the instant asked for, oldest first.
    /// </summary>
    /// <remarks>
    /// Restatements resolved the same way prices are: a corrected ratio published later wins over
    /// the original, and a correction that had not been published by the instant in question is
    /// not visible at all.
    /// </remarks>
    private static List<ShareSplit> Splits(IReadOnlyList<Observation> stored, string attribute) =>
        stored
            .Where(o => string.Equals(o.Attribute, attribute, StringComparison.Ordinal))
            .Where(o => o.Value.Kind == ObservationValueKind.Number)
            .GroupBy(o => o.Provenance.AsOfUtc)
            .Select(group => group
                .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                .ThenByDescending(o => o.Provenance.RetrievedAtUtc)
                .First())
            .OrderBy(o => o.Provenance.AsOfUtc)
            .Select(o => new ShareSplit(o.Provenance.AsOfUtc, o.Value.AsNumber()))
            .ToList();

    private static List<PricedObservation> Resolve(
        IReadOnlyList<Observation> stored,
        string attribute,
        int maxSessions)
    {
        var series = stored
            .Where(o => string.Equals(o.Attribute, attribute, StringComparison.Ordinal))
            .Where(o => o.Value.Kind == ObservationValueKind.Number)
            .GroupBy(o => o.Provenance.AsOfUtc)
            .Select(group => group
                .OrderByDescending(o => o.Provenance.PublishedAtUtc)
                .ThenByDescending(o => o.Provenance.RetrievedAtUtc)
                .First())
            .OrderBy(o => o.Provenance.AsOfUtc)
            .Select(o => new PricedObservation(o.Id, o.Provenance.AsOfUtc, o.Value.AsNumber(), o.Provenance))
            .ToList();

        return series.Count <= maxSessions
            ? series
            : series.GetRange(series.Count - maxSessions, maxSessions);
    }
}
