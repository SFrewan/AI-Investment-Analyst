using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// Builders for the opportunity tests.
/// </summary>
/// <remarks>
/// Every builder produces a valid object, so a test that wants an invalid one has to say which
/// field it broke. Tests that construct everything inline end up asserting on incidental values
/// nobody chose.
/// </remarks>
internal static class OpportunityFixtures
{
    internal static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    internal static IngestionSubject Subject(string ticker = "AAPL") =>
        IngestionSubject.Create("Security", ticker);

    internal static OpportunitySource Source(DateTime? discoveredAtUtc = null) =>
        OpportunitySource.Create("equity-screener", discoveredAtUtc ?? Now);

    internal static OpportunityDetail Detail(
        string instrument = "AAPL",
        decimal quantity = 10m,
        decimal entryPrice = 100m,
        decimal targetPrice = 120m,
        string currency = "USD",
        decimal successProbability = 0.6m,
        int horizonDays = 90) =>
        OpportunityDetail.Create(
            EquityOpportunity.Type,
            EquityDetail.ToJson(
                instrument,
                quantity,
                entryPrice,
                targetPrice,
                currency,
                successProbability,
                horizonDays));

    internal static Opportunity Draft(
        DateTime? nowUtc = null,
        IEnumerable<ClaimId>? evidence = null,
        OpportunityDetail? detail = null,
        IngestionSubject? subject = null)
    {
        var at = nowUtc ?? Now;

        return Opportunity.Draft(
            EquityOpportunity.Type,
            subject ?? Subject(),
            Source(at),
            "Buy 10 AAPL",
            "The screener found a gap between the entry price and the analyst target.",
            detail ?? Detail(),
            at,
            evidence ?? [ClaimId.New()]);
    }

    internal static OpportunityEconomics Economics(
        decimal cost = 1000m,
        decimal revenue = 1200m,
        decimal capital = 1000m,
        decimal probability = 0.6m,
        DateTime? startUtc = null,
        int horizonDays = 90)
    {
        var start = startUtc ?? Now;

        return OpportunityEconomics.Create(
            Money.Create(cost, Currency.Usd),
            Money.Create(revenue, Currency.Usd),
            Money.Create(capital, Currency.Usd),
            Percentage.FromRatio(probability),
            DateRange.Create(start, start.AddDays(horizonDays)));
    }

    internal static OpportunityRisk Risk(
        ReversibilityClass reversibility = ReversibilityClass.ReversibleWithCost) =>
        OpportunityRisk.Create(
            "A single-name equity position carries issuer and market risk.",
            reversibility,
            [ClaimId.New()],
            ["concentration", "earnings surprise"]);

    internal static Confidence ConfidenceOf(decimal value = 0.7m) =>
        Confidence.Create(value);

    /// <summary>A ranked-score input: a dimensionless ratio, published before the cutoff.</summary>
    internal static MetricResult ScoreResult(decimal value = 0.82m, DateTime? nowUtc = null)
    {
        var at = nowUtc ?? Now;
        var published = at.AddDays(-1);

        var provenance = Provenance.Create(
            SourceId.Create("scoring-engine"),
            published,
            published,
            at);

        var input = CalculationInput.Create(
            "financial-health",
            Claims.Fact(value, provenance),
            UnitOfMeasure.Ratio);

        var context = CalculationContext.Create(
            Subject(),
            KnowledgeCutoff.At(at),
            at);

        return MetricResult.Create(
            context,
            MetricId.Create("opportunity.composite-score"),
            MetricValue.Ratio(value),
            "the shipped scoring specification",
            SourceId.Create("scoring-engine"),
            CalculationVersion.Create(1, 0),
            published,
            [input]);
    }

    internal static OpportunityScore Score(decimal value = 0.82m, DateTime? nowUtc = null) =>
        OpportunityScore.From(ScoreResult(value, nowUtc));

    /// <summary>An opportunity that has been evaluated, ranked, proposed and approved.</summary>
    internal static Opportunity Approved(DateTime? nowUtc = null, Guid? approvalTokenId = null)
    {
        var at = nowUtc ?? Now;
        var opportunity = Draft(at);

        opportunity.Evaluate(Economics(startUtc: at), Risk(), ConfidenceOf(), at);
        opportunity.Rank(Score(nowUtc: at), at);
        opportunity.RecordProposal(Guid.NewGuid(), at);
        opportunity.Approve(approvalTokenId ?? Guid.NewGuid(), at);

        return opportunity;
    }
}
