using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Opportunities;

/// <summary>
/// Moves an opportunity from a raw candidate to something rankable and then proposable.
/// </summary>
/// <remarks>
/// <para>
/// The per-type behaviour is injected rather than known here. This class enforces the order -
/// requirements before economics, economics before ranking, ranking before proposing - and the
/// type's own calculator and requirement decide what those mean. That split is what lets a second
/// opportunity type arrive without touching the lifecycle everything else depends on.
/// </para>
/// <para>
/// Nothing in this class causes an effect. It produces an opportunity in a state where an action
/// <em>may</em> be proposed; whether that action happens is the policy engine's decision, made
/// elsewhere.
/// </para>
/// </remarks>
public sealed class OpportunityWorkflow
{
    private readonly IOpportunityRepository _repository;
    private readonly IClock _clock;
    private readonly Dictionary<string, IOpportunityEconomicsCalculator> _calculators;
    private readonly Dictionary<string, IEvidenceRequirement> _requirements;

    public OpportunityWorkflow(
        IOpportunityRepository repository,
        IEnumerable<IOpportunityEconomicsCalculator> calculators,
        IEnumerable<IEvidenceRequirement> requirements,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(calculators);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _clock = clock;
        _calculators = calculators.ToDictionary(c => c.Type.Value, StringComparer.Ordinal);
        _requirements = requirements.ToDictionary(r => r.Type.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Checks the type's evidence requirements, computes its economics, and evaluates it.
    /// </summary>
    /// <returns>
    /// The requirements that are still missing. An empty list means the opportunity was evaluated.
    /// </returns>
    public async Task<IReadOnlyList<string>> EvaluateAsync(
        Opportunity opportunity,
        OpportunityRisk risk,
        Confidence confidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(confidence);

        var requirement = Requirement(opportunity.Type);
        var missing = requirement.MissingRequirements(opportunity);

        if (missing.Count > 0)
        {
            return missing;
        }

        var calculator = Calculator(opportunity.Type);
        var nowUtc = _clock.UtcNow;

        opportunity.Evaluate(calculator.Calculate(opportunity, nowUtc), risk, confidence, nowUtc);

        await _repository.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);

        return [];
    }

    /// <summary>Records the deterministic score an opportunity is ranked by.</summary>
    /// <remarks>
    /// The score arrives as a Phase 3 <see cref="MetricResult"/> rather than a number, so an
    /// opportunity can only be ranked by something a versioned calculator produced. Ranking on a
    /// loose decimal would make this week's ordering incomparable with last week's for reasons
    /// invisible in the data.
    /// </remarks>
    public async Task RankAsync(
        Opportunity opportunity,
        MetricResult score,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(score);

        opportunity.Rank(OpportunityScore.From(score), _clock.UtcNow);

        await _repository.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that an action has been proposed for a ranked opportunity.</summary>
    public async Task RecordProposalAsync(
        Opportunity opportunity,
        Guid proposalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        opportunity.RecordProposal(proposalId, _clock.UtcNow);

        await _repository.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refuses an opportunity, with a reason that is kept.</summary>
    public async Task RejectAsync(
        Opportunity opportunity,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        opportunity.Reject(reason, _clock.UtcNow);

        await _repository.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Expires every ranked or proposed opportunity whose horizon has passed.</summary>
    /// <remarks>
    /// Separate from rejection because the two mean different things when the hit rate is measured:
    /// one is a decision, and the other is a decision nobody made in time.
    /// </remarks>
    public async Task<int> ExpireOverdueAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNow;
        var expired = 0;

        foreach (var status in new[] { OpportunityStatus.Evaluated, OpportunityStatus.Ranked, OpportunityStatus.Proposed })
        {
            foreach (var opportunity in await _repository
                .ListAsync(status, int.MaxValue, cancellationToken)
                .ConfigureAwait(false))
            {
                if (opportunity.Economics is { } economics && economics.TimeHorizon.EndUtc <= nowUtc)
                {
                    opportunity.Expire(nowUtc);

                    await _repository.AddAsync(opportunity, cancellationToken).ConfigureAwait(false);

                    expired++;
                }
            }
        }

        return expired;
    }

    private IOpportunityEconomicsCalculator Calculator(OpportunityType type) =>
        _calculators.TryGetValue(type.Value, out var calculator)
            ? calculator
            : throw new DomainRuleViolationException(
                "OpportunityWorkflow.NoCalculator",
                $"No economics calculator is registered for opportunity type '{type}'. Its numbers " +
                "would have to come from somewhere else, and the one rule this platform will not bend " +
                "is that profit is calculated rather than stated.");

    private IEvidenceRequirement Requirement(OpportunityType type) =>
        _requirements.TryGetValue(type.Value, out var requirement)
            ? requirement
            : throw new DomainRuleViolationException(
                "OpportunityWorkflow.NoEvidenceRequirement",
                $"No evidence requirement is registered for opportunity type '{type}'. Without one " +
                "there is nothing to stop a half-formed candidate reaching a ranking list, and an " +
                "unregistered type would be the least checked rather than the most.");
}
