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
    /// <paramref name="asAtUtc"/>, oldest first.
    /// </summary>
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
