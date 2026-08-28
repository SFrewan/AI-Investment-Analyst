using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Equity;

/// <summary>
/// Computes the economics of an equity opportunity from its detail payload. Deterministic.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Profit and margin are calculated here and nowhere else.</strong> The architecture's one
/// firm rule about opportunities is that an agent may supply an input claim - an estimated target
/// price, with provenance and a confidence - and the arithmetic is the system's. Nothing in this
/// class reads a stated profit, and there is no field for one to arrive in.
/// </para>
/// <para>
/// Commission is deliberately absent from the estimate. What a fill actually costs is decided by
/// the venue and posted to the ledger from the fill; putting a second, guessed fee here would
/// produce two numbers for one thing and no way to tell which a later comparison used.
/// </para>
/// </remarks>
public sealed class EquityEconomicsCalculator : IOpportunityEconomicsCalculator
{
    public OpportunityType Type => EquityOpportunity.Type;

    public CalculationVersion Version { get; } = CalculationVersion.Create(1, 0);

    public OpportunityEconomics Calculate(Opportunity opportunity, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var detail = EquityDetail.Parse(opportunity.Detail);
        var currency = Currency.Create(detail.CurrencyCode);

        var cost = Money.Create(detail.EntryPrice * detail.Quantity, currency);
        var revenue = Money.Create(detail.TargetPrice * detail.Quantity, currency);

        return OpportunityEconomics.Create(
            cost,
            revenue,
            cost,
            Percentage.FromRatio(detail.SuccessProbability),
            DateRange.Create(nowUtc, nowUtc.AddDays(detail.HorizonDays)));
    }
}
