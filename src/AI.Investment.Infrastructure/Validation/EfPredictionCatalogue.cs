using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Validation;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Validation;

/// <summary>
/// Turns the opportunities the platform produced into the predictions a validation run measures.
/// </summary>
/// <remarks>
/// <para>
/// An opportunity is a prediction whether or not it was ever called one: the platform found
/// something, said how likely it was to work, and then either acted on it or did not. Reading them
/// this way is what makes the refusals measurable as well as the actions - and a hit rate computed
/// only over the opportunities that were acted on measures the approval process rather than the
/// analysis.
/// </para>
/// <para>
/// <strong>The decision time is when the opportunity was discovered, not when it was approved.</strong>
/// The aggregate keeps one creation time and one status-change time, so the later transitions cannot
/// be dated individually. Taking the earliest defensible moment admits the least evidence, which is
/// the direction a measurement should err in; taking the latest would let months of subsequent
/// publication count as knowable.
/// </para>
/// <para>
/// <strong>Admissibility is established from the cited evidence, or not at all.</strong> An
/// opportunity cites its evidence by claim identifier. Where those identifiers resolve to stored
/// observations, the latest publication time among them is when the opportunity became knowable.
/// Where even one of them does not resolve, this returns no time at all, and the run refuses the
/// prediction rather than assuming the missing evidence was old. That refusal is the honest outcome
/// for a discoverer that mints fresh claim identifiers instead of citing what it read, and it shows
/// up in the report as a data gap rather than as a smaller, quietly better sample.
/// </para>
/// </remarks>
public sealed class EfPredictionCatalogue : IPredictionCatalogue
{
    private readonly AppDbContext _dbContext;

    public EfPredictionCatalogue(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>The version reported for an opportunity that was never scored.</summary>
    public static CalculationVersion UnscoredMethodology { get; } = CalculationVersion.Create(1, 0);

    public async Task<IReadOnlyList<PredictionCandidate>> GetAsync(
        EvaluationWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        var opportunities = await _dbContext.Opportunities
            .AsNoTracking()
            .Where(o => o.CreatedAtUtc >= window.FromUtc && o.CreatedAtUtc <= window.ToUtc)
            .OrderBy(o => o.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = new List<PredictionCandidate>(opportunities.Count);

        foreach (var opportunity in opportunities)
        {
            candidates.Add(new PredictionCandidate(
                opportunity.OpportunityId.Value,
                opportunity.Subject,
                opportunity.CreatedAtUtc,
                opportunity.Economics?.TimeHorizon.EndUtc ?? opportunity.CreatedAtUtc,
                DirectionOf(opportunity.Status),
                opportunity.Score?.Version ?? UnscoredMethodology,
                $"opportunity/{opportunity.OpportunityId}",
                await EvidenceAvailableAtAsync(opportunity, cancellationToken).ConfigureAwait(false),
                opportunity.Economics?.SuccessProbability,
                opportunity.Confidence,
                opportunity.ProposalIds.Count > 0 ? opportunity.ProposalIds[0] : null));
        }

        return candidates;
    }

    /// <summary>
    /// Which way the platform called it, read from where the opportunity ended up.
    /// </summary>
    /// <remarks>
    /// Proposed and everything after it is a call to act: the platform put the action forward. Rejected
    /// and expired are calls not to. Draft, evaluated and ranked are abstentions - the platform looked
    /// and did not commit - and are counted as such rather than folded into either side, because a
    /// system that mostly abstains has told you something and the rates must not hide it.
    /// </remarks>
    private static PredictionDirection DirectionOf(OpportunityStatus status) => status switch
    {
        OpportunityStatus.Proposed or OpportunityStatus.Approved or OpportunityStatus.Executing
            or OpportunityStatus.Active or OpportunityStatus.Closed => PredictionDirection.Positive,

        OpportunityStatus.Rejected or OpportunityStatus.Expired => PredictionDirection.Negative,

        OpportunityStatus.Draft or OpportunityStatus.Evaluated or OpportunityStatus.Ranked =>
            PredictionDirection.Abstain,

        _ => PredictionDirection.Unknown,
    };

    private async Task<DateTime?> EvidenceAvailableAtAsync(
        Opportunity opportunity,
        CancellationToken cancellationToken)
    {
        if (opportunity.Evidence.Count == 0)
        {
            // No cited evidence is not the same as evidence with no timestamp, but it fails the same
            // way and for the same reason: nothing establishes what this opportunity could have known.
            return null;
        }

        var ids = opportunity.Evidence
            .Select(claimId => ObservationId.Create(claimId.Value))
            .ToList();

        var published = await _dbContext.Observations
            .AsNoTracking()
            .Where(o => ids.Contains(o.Id))
            .Select(o => o.Provenance.PublishedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every cited piece, or none. A latest-publication time computed over the subset that happened
        // to resolve is an understatement of what the opportunity saw, and understating it is exactly
        // what would let an inadmissible prediction through.
        return published.Count == ids.Count && published.Count > 0 ? published.Max() : null;
    }
}
