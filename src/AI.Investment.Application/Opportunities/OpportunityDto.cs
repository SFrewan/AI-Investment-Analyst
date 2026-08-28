using AI.Investment.Domain.Opportunities;

namespace AI.Investment.Application.Opportunities;

/// <summary>
/// The read model for one opportunity: what a dashboard or an escalation queue shows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Uncertainty is not optional in the read model either.</strong> Confidence and the risk
/// summary sit beside the money rather than behind a link, because a recommendation rendered
/// without them reads as certain, and §L.9's whole point is that the honest version cannot be
/// styled away.
/// </para>
/// <para>
/// It carries no detail payload. The per-type JSON is what a type's own tooling reads; a generic
/// dashboard showing it would be showing a schema it cannot validate.
/// </para>
/// </remarks>
public sealed record OpportunityDto(
    Guid OpportunityId,
    string Type,
    string SubjectKind,
    string? SubjectIdentifier,
    string DiscovererId,
    DateTime DiscoveredAtUtc,
    string Title,
    string Description,
    string Status,
    DateTime StatusChangedAtUtc,
    DateTime CreatedAtUtc,
    OpportunityEconomicsDto? Economics,
    OpportunityRiskDto? Risk,
    decimal? Confidence,
    OpportunityScoreDto? Score,
    int EvidenceCount,
    IReadOnlyList<Guid> ProposalIds,
    Guid? ApprovalTokenId,
    Guid? ExecutionId,
    string? Resolution);

/// <summary>The calculated economics. Every figure here is an output of a calculator.</summary>
public sealed record OpportunityEconomicsDto(
    decimal EstimatedCost,
    decimal EstimatedRevenue,
    decimal EstimatedProfit,
    decimal MarginRatio,
    decimal RequiredCapital,
    decimal SuccessProbability,
    decimal RiskAdjustedReturn,
    string Currency,
    DateTime HorizonStartUtc,
    DateTime HorizonEndUtc);

/// <summary>The mandatory risk assessment.</summary>
public sealed record OpportunityRiskDto(
    string Summary,
    string Reversibility,
    IReadOnlyList<string> Factors,
    int EvidenceCount);

/// <summary>The deterministic score, when the opportunity has been ranked.</summary>
public sealed record OpportunityScoreDto(
    string Metric,
    decimal Value,
    string Version,
    DateTime AsOfUtc);

/// <summary>Maps the aggregate to its read model.</summary>
public static class OpportunityMapper
{
    public static OpportunityDto ToDto(Opportunity opportunity)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        return new OpportunityDto(
            opportunity.OpportunityId.Value,
            opportunity.Type.Value,
            opportunity.Subject.Kind,
            opportunity.Subject.Identifier,
            opportunity.Source.DiscovererId.Value,
            opportunity.Source.DiscoveredAtUtc,
            opportunity.Title,
            opportunity.Description,
            opportunity.Status.ToString(),
            opportunity.StatusChangedAtUtc,
            opportunity.CreatedAtUtc,
            ToDto(opportunity.Economics),
            ToDto(opportunity.Risk),
            opportunity.Confidence?.Value,
            ToDto(opportunity.Score),
            opportunity.Evidence.Count,
            opportunity.ProposalIds.ToList(),
            opportunity.ApprovalTokenId,
            opportunity.ExecutionId,
            opportunity.Resolution);
    }

    private static OpportunityEconomicsDto? ToDto(OpportunityEconomics? economics) =>
        economics is null
            ? null
            : new OpportunityEconomicsDto(
                economics.EstimatedCost.Amount,
                economics.EstimatedRevenue.Amount,
                economics.EstimatedProfit.Amount,
                economics.Margin.Ratio,
                economics.RequiredCapital.Amount,
                economics.SuccessProbability.Ratio,
                economics.RiskAdjustedReturn.Amount,
                economics.Currency.Code,
                economics.TimeHorizon.StartUtc,
                economics.TimeHorizon.EndUtc);

    private static OpportunityRiskDto? ToDto(OpportunityRisk? risk) =>
        risk is null
            ? null
            : new OpportunityRiskDto(
                risk.Summary,
                risk.Reversibility.ToString(),
                risk.Factors.ToList(),
                risk.Evidence.Count);

    private static OpportunityScoreDto? ToDto(OpportunityScore? score) =>
        score is null
            ? null
            : new OpportunityScoreDto(
                score.Metric.Value,
                score.Value,
                score.Version.ToString(),
                score.AsOfUtc);
}
