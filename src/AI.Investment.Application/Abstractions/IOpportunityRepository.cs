using AI.Investment.Domain.Opportunities;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores and retrieves opportunities.</summary>
/// <remarks>
/// No delete. An opportunity that was discovered, evaluated and then refused is exactly the record
/// the validation phase needs in order to measure whether the refusals were right - and a hit rate
/// computed only over the ones that were acted on measures nothing.
/// </remarks>
public interface IOpportunityRepository
{
    Task AddAsync(Opportunity opportunity, CancellationToken cancellationToken = default);

    Task<Opportunity?> GetAsync(OpportunityId opportunityId, CancellationToken cancellationToken = default);

    /// <summary>Opportunities in a given state, most recently changed first.</summary>
    Task<IReadOnlyList<Opportunity>> ListAsync(
        OpportunityStatus status,
        int limit = 50,
        CancellationToken cancellationToken = default);
}
