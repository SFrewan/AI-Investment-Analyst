using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Domain.Opportunities;

/// <summary>Finds candidate opportunities of one type.</summary>
/// <remarks>
/// <para>
/// The first of the three per-type interfaces. Screening is a query, not a judgement: a discoverer
/// answers "which of these meet the stated criteria", deterministically, and an agent's ranking
/// rationale is a separate and later thing.
/// </para>
/// <para>
/// A discoverer produces drafts and nothing else. It cannot evaluate, score, propose or approve -
/// those are the core's, and keeping them there is what lets a new opportunity type arrive without
/// its own safety review.
/// </para>
/// </remarks>
public interface IOpportunityDiscoverer
{
    OpportunityType Type { get; }

    /// <summary>The producer identity written into every opportunity this discoverer finds.</summary>
    Sources.SourceId DiscovererId { get; }

    /// <summary>Candidates found for the subject, as drafts.</summary>
    Task<IReadOnlyList<Opportunity>> DiscoverAsync(
        IngestionSubject subject,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}
