using System.Text.Json;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// Converts the two composite parts of an opportunity to and from JSON for storage.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only the inputs of the economics block are stored.</strong> Profit, margin and
/// risk-adjusted return are recomputed by the domain factory when the row is read, exactly as they
/// were when it was written. That is a deliberate use of storage to enforce a domain rule: a stored
/// profit figure could drift from the inputs it claims to come from, and nothing in the data would
/// say which was wrong. One that is always recomputed cannot.
/// </para>
/// <para>
/// JSON rather than a column per field because these are composite value objects with six money
/// amounts between them, and because nothing safety-relevant is queried from either. The score,
/// which <em>is</em> ordered on, is mapped to real columns instead.
/// </para>
/// </remarks>
internal static class OpportunityJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    internal static string SerializeEconomics(OpportunityEconomics economics) =>
        JsonSerializer.Serialize(
            new EconomicsRow(
                economics.EstimatedCost.Amount,
                economics.EstimatedRevenue.Amount,
                economics.RequiredCapital.Amount,
                economics.SuccessProbability.Ratio,
                economics.Currency.Code,
                economics.TimeHorizon.StartUtc,
                economics.TimeHorizon.EndUtc),
            Options);

    internal static OpportunityEconomics DeserializeEconomics(string json)
    {
        var row = JsonSerializer.Deserialize<EconomicsRow>(json, Options)
            ?? throw new InvalidOperationException(
                "A stored economics block could not be read. Rather than substituting a default - " +
                "which would present zero cost and zero exposure as fact - the read fails.");

        var currency = Currency.Create(row.Currency);

        return OpportunityEconomics.Create(
            Money.Create(row.Cost, currency),
            Money.Create(row.Revenue, currency),
            Money.Create(row.RequiredCapital, currency),
            Percentage.FromRatio(row.SuccessProbability),
            DateRange.Create(row.HorizonStartUtc, row.HorizonEndUtc));
    }

    internal static string SerializeRisk(OpportunityRisk risk) =>
        JsonSerializer.Serialize(
            new RiskRow(
                risk.Summary,
                risk.Reversibility.ToString(),
                risk.Factors.ToList(),
                risk.Evidence.Select(id => id.Value).ToList()),
            Options);

    internal static OpportunityRisk DeserializeRisk(string json)
    {
        var row = JsonSerializer.Deserialize<RiskRow>(json, Options)
            ?? throw new InvalidOperationException(
                "A stored risk assessment could not be read. It is a mandatory field, so a missing " +
                "one is a defect rather than something to default.");

        return OpportunityRisk.Create(
            row.Summary,
            Enum.Parse<ReversibilityClass>(row.Reversibility),
            row.Evidence.Select(ClaimId.Create),
            row.Factors);
    }

    /// <summary>The inputs of an economics block. The derived figures are deliberately absent.</summary>
    private sealed record EconomicsRow(
        decimal Cost,
        decimal Revenue,
        decimal RequiredCapital,
        decimal SuccessProbability,
        string Currency,
        DateTime HorizonStartUtc,
        DateTime HorizonEndUtc);

    private sealed record RiskRow(
        string Summary,
        string Reversibility,
        List<string> Factors,
        List<Guid> Evidence);
}
