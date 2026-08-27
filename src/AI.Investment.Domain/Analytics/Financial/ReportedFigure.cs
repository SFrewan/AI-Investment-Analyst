using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// One line item, its value, its unit and the evidence for it.
/// </summary>
/// <remarks>
/// The unit travels with the figure because the calculators need it: dividing money by money gives
/// a dimensionless ratio, dividing money by a share count gives money, and adding money to a share
/// count is a mistake that must be caught rather than computed.
/// </remarks>
public sealed record ReportedFigure
{
    private ReportedFigure(string attribute, Claim<decimal> evidence, UnitOfMeasure unit)
    {
        Attribute = attribute;
        Evidence = evidence;
        Unit = unit;
    }

    /// <summary>The observation attribute this figure was reported under, lower-cased.</summary>
    public string Attribute { get; }

    public Claim<decimal> Evidence { get; }

    public UnitOfMeasure Unit { get; }

    public decimal Value => Evidence.Value;

    public Provenance Provenance => Evidence.Provenance;

    public static ReportedFigure Create(string attribute, Claim<decimal> evidence, UnitOfMeasure unit)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (string.IsNullOrWhiteSpace(attribute))
        {
            throw new DomainValidationException(
                nameof(attribute),
                "A reported figure must say which line item it is. An unnamed number cannot be " +
                "matched to the term a formula asks for.");
        }

        if (!Enum.IsDefined(unit) || unit == UnitOfMeasure.Unknown)
        {
            throw new DomainValidationException(
                nameof(unit),
                $"'{unit}' is not a unit a reported figure may carry.");
        }

        // Lower-cased so a lookup cannot miss because ingestion wrote 'Revenue' where a calculator
        // asks for 'revenue'. The convention the normalisers follow is already lower-case; this
        // makes the match independent of that continuing to hold.
        return new ReportedFigure(attribute.Trim().ToLowerInvariant(), evidence, unit);
    }

    /// <summary>Money, which is what almost every statement line item is.</summary>
    public static ReportedFigure OfMoney(string attribute, Claim<decimal> evidence) =>
        Create(attribute, evidence, UnitOfMeasure.Money);

    /// <summary>A tally - a share count, most often.</summary>
    public static ReportedFigure OfCount(string attribute, Claim<decimal> evidence) =>
        Create(attribute, evidence, UnitOfMeasure.Count);

    public override string ToString() => $"{Attribute} = {Value}";
}
