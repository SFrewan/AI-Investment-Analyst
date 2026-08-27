using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.UnitTests.Analytics.Financial;

/// <summary>Two reporting periods of one filer, with dates that satisfy the Phase 1 evidence rules.</summary>
internal static class Financials
{
    internal static readonly DateTime CurrentPeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime PriorPeriodEnd = new(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime CurrentPublished = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime PriorPublished = new(2025, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static IngestionSubject Subject { get; } = IngestionSubject.Create("company", "AAPL");

    internal static CalculationContext Context(DateTime? cutoffUtc = null) =>
        CalculationContext.Create(Subject, KnowledgeCutoff.At(cutoffUtc ?? Now), Now);

    internal static Claim<decimal> Fact(decimal value, DateTime periodEndUtc, DateTime publishedUtc) =>
        Claims.Fact(value, Provenance.Create("sec-edgar", periodEndUtc, publishedUtc, publishedUtc));

    internal static ReportedFigure Money(
        string attribute,
        decimal value,
        DateTime? periodEndUtc = null,
        DateTime? publishedUtc = null) =>
        ReportedFigure.OfMoney(
            attribute,
            Fact(value, periodEndUtc ?? CurrentPeriodEnd, publishedUtc ?? CurrentPublished));

    internal static ReportedFigure PriorMoney(string attribute, decimal value) =>
        Money(attribute, value, PriorPeriodEnd, PriorPublished);

    internal static ReportedFigure Shares(string attribute, decimal value) =>
        ReportedFigure.OfCount(attribute, Fact(value, CurrentPeriodEnd, CurrentPublished));

    internal static ReportedFigures Current(params ReportedFigure[] figures) =>
        ReportedFigures.Create(Subject, CurrentPeriodEnd, Currency.Usd, figures);

    internal static ReportedFigures Prior(params ReportedFigure[] figures) =>
        ReportedFigures.Create(Subject, PriorPeriodEnd, Currency.Usd, figures);
}
