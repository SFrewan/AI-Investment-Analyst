using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The first concrete opportunity type: what it must prove, and how its numbers are produced.
/// </summary>
public sealed class EquityOpportunityTests
{
    [Fact]
    public void A_well_formed_payload_round_trips()
    {
        var detail = OpportunityFixtures.Detail(
            instrument: "MSFT",
            quantity: 4m,
            entryPrice: 250.5m,
            targetPrice: 300m,
            successProbability: 0.55m,
            horizonDays: 45);

        var parsed = EquityDetail.Parse(detail);

        Assert.Equal("MSFT", parsed.Instrument);
        Assert.Equal(4m, parsed.Quantity);
        Assert.Equal(250.5m, parsed.EntryPrice);
        Assert.Equal(300m, parsed.TargetPrice);
        Assert.Equal("USD", parsed.CurrencyCode);
        Assert.Equal(0.55m, parsed.SuccessProbability);
        Assert.Equal(45, parsed.HorizonDays);
    }

    [Fact]
    public void Every_problem_with_a_payload_is_reported_not_just_the_first()
    {
        var detail = OpportunityDetail.Create(
            EquityOpportunity.Type,
            """{"quantity":-1,"entryPrice":0,"successProbability":4}""");

        var problems = EquityDetail.TryParse(detail, out var parsed);

        Assert.Null(parsed);
        Assert.Contains(problems, problem => problem.Contains("instrument", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("currency", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("targetPrice", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("horizonDays", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("quantity", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("successProbability", StringComparison.Ordinal));
    }

    [Fact]
    public void A_payload_that_cannot_be_read_throws_rather_than_defaulting()
    {
        var detail = OpportunityDetail.Empty(EquityOpportunity.Type);

        Assert.Throws<DomainValidationException>(() => EquityDetail.Parse(detail));
    }

    [Fact]
    public void The_calculator_derives_profit_and_margin_and_reads_no_stated_figure()
    {
        var opportunity = OpportunityFixtures.Draft(
            detail: OpportunityFixtures.Detail(
                quantity: 10m,
                entryPrice: 100m,
                targetPrice: 130m,
                successProbability: 0.5m));

        var economics = new EquityEconomicsCalculator().Calculate(opportunity, OpportunityFixtures.Now);

        Assert.Equal(1000m, economics.EstimatedCost.Amount);
        Assert.Equal(1300m, economics.EstimatedRevenue.Amount);
        Assert.Equal(300m, economics.EstimatedProfit.Amount);
        Assert.Equal(1000m, economics.RequiredCapital.Amount);
        Assert.Equal(150m, economics.RiskAdjustedReturn.Amount);
        Assert.Equal(300m / 1300m, economics.Margin.Ratio);
        Assert.Equal(Currency.Usd, economics.Currency);
    }

    [Fact]
    public void The_calculator_states_its_type_and_version()
    {
        var calculator = new EquityEconomicsCalculator();

        Assert.Equal(EquityOpportunity.Type, calculator.Type);
        Assert.Equal(1, calculator.Version.Major);
    }

    [Fact]
    public void The_horizon_starts_now_and_runs_for_the_stated_number_of_days()
    {
        var opportunity = OpportunityFixtures.Draft(
            detail: OpportunityFixtures.Detail(horizonDays: 30));

        var economics = new EquityEconomicsCalculator().Calculate(opportunity, OpportunityFixtures.Now);

        Assert.Equal(OpportunityFixtures.Now, economics.TimeHorizon.StartUtc);
        Assert.Equal(OpportunityFixtures.Now.AddDays(30), economics.TimeHorizon.EndUtc);
    }

    [Fact]
    public void A_complete_candidate_has_nothing_missing()
    {
        var opportunity = OpportunityFixtures.Draft();

        Assert.Empty(new EquityEvidenceRequirement().MissingRequirements(opportunity));
    }

    [Fact]
    public void A_candidate_with_no_evidence_is_reported_as_incomplete()
    {
        var opportunity = OpportunityFixtures.Draft(evidence: []);

        var missing = new EquityEvidenceRequirement().MissingRequirements(opportunity);

        Assert.Contains(missing, item => item.Contains("evidence claim", StringComparison.Ordinal));
    }

    [Fact]
    public void A_candidate_with_no_specific_instrument_is_reported_as_incomplete()
    {
        var opportunity = OpportunityFixtures.Draft(
            evidence: [ClaimId.New()],
            subject: IngestionSubject.Sweep("Sector"));

        var missing = new EquityEvidenceRequirement().MissingRequirements(opportunity);

        Assert.Contains(missing, item => item.Contains("specific instrument", StringComparison.Ordinal));
    }

    [Fact]
    public void A_candidate_whose_payload_cannot_be_read_is_reported_rather_than_throwing()
    {
        var opportunity = OpportunityFixtures.Draft(
            detail: OpportunityDetail.Empty(EquityOpportunity.Type));

        var missing = new EquityEvidenceRequirement().MissingRequirements(opportunity);

        Assert.NotEmpty(missing);
        Assert.All(missing, item => Assert.False(string.IsNullOrWhiteSpace(item)));
    }

    [Fact]
    public void The_requirement_states_the_type_it_governs()
    {
        Assert.Equal(EquityOpportunity.Type, new EquityEvidenceRequirement().Type);
    }
}
