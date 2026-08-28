using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.UnitTests.Fakes;

/// <summary>An opportunity store that keeps what it was handed, and says how often.</summary>
public sealed class InMemoryOpportunityRepository : IOpportunityRepository
{
    private readonly Dictionary<OpportunityId, Opportunity> _opportunities = new();

    public int Saves { get; private set; }

    public Task AddAsync(Opportunity opportunity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        _opportunities[opportunity.OpportunityId] = opportunity;
        Saves++;

        return Task.CompletedTask;
    }

    public Task<Opportunity?> GetAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Opportunity?>(_opportunities.GetValueOrDefault(opportunityId));

    public Task<IReadOnlyList<Opportunity>> ListAsync(
        OpportunityStatus status,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Opportunity>>(
            _opportunities.Values.Where(o => o.Status == status).Take(limit).ToList());
}

/// <summary>An approval store with no concurrency of its own; the workflow tests do not need one.</summary>
public sealed class InMemoryApprovalTokenStore : IApprovalTokenStore
{
    private readonly Dictionary<Guid, ApprovalToken> _tokens = new();

    public IReadOnlyCollection<ApprovalToken> Tokens => _tokens.Values;

    public Task AddAsync(ApprovalToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        _tokens[token.ApprovalTokenId] = token;

        return Task.CompletedTask;
    }

    public Task<ApprovalToken?> GetAsync(Guid approvalTokenId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ApprovalToken?>(_tokens.GetValueOrDefault(approvalTokenId));

    public Task<ApprovalRefusal> ConsumeAsync(
        Guid approvalTokenId,
        OpportunityId opportunityId,
        ActionProposal proposal,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (!_tokens.TryGetValue(approvalTokenId, out var token))
        {
            return Task.FromResult(ApprovalRefusal.Revoked);
        }

        var refusal = token.Check(opportunityId, proposal, nowUtc);

        if (refusal == ApprovalRefusal.None)
        {
            token.Consume(opportunityId, proposal, nowUtc);
        }

        return Task.FromResult(refusal);
    }
}

/// <summary>Builds the dimensionless score an opportunity may be ranked on.</summary>
public static class Phase5Scores
{
    public static MetricResult Ratio(DateTime nowUtc, decimal value = 0.82m)
    {
        var published = nowUtc.AddDays(-1);

        var provenance = Provenance.Create(
            SourceId.Create("scoring-engine"),
            published,
            published,
            nowUtc);

        var context = CalculationContext.Create(
            IngestionSubject.Create("Security", "AAPL"),
            KnowledgeCutoff.At(nowUtc),
            nowUtc);

        return MetricResult.Create(
            context,
            MetricId.Create("opportunity.composite-score"),
            MetricValue.Ratio(value),
            "the shipped scoring specification",
            SourceId.Create("scoring-engine"),
            CalculationVersion.Create(1, 0),
            published,
            [CalculationInput.Create("financial-health", Claims.Fact(value, provenance), UnitOfMeasure.Ratio)]);
    }
}
