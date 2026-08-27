using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.UnitTests.Analytics;

/// <summary>
/// The evidence these tests measure things from.
/// </summary>
/// <remarks>
/// Shared because every analytics test needs a claim that is genuinely a claim - with a source, a
/// period, a publication date and a retrieval date that satisfy the Phase 1 rules. Hand-rolling
/// that per test file would drift, and a test-only drift in what counts as evidence is exactly the
/// kind that makes a green suite meaningless.
/// </remarks>
internal static class AnalyticsEvidence
{
    /// <summary>The quarter these figures describe.</summary>
    internal static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>When the filing carrying them became public.</summary>
    internal static readonly DateTime Published = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>When the measuring happens.</summary>
    internal static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static IngestionSubject Subject { get; } = IngestionSubject.Create("company", "AAPL");

    internal static SourceId Calculator { get; } = SourceId.Create("calc.financial.revenue-growth");

    internal static CalculationVersion Version { get; } = CalculationVersion.Create(1, 0);

    internal static MetricId Metric { get; } = MetricId.Create("financial.revenue.growth");

    internal static Claim<decimal> Fact(decimal value, DateTime? publishedAtUtc = null)
    {
        var published = publishedAtUtc ?? Published;

        return Claims.Fact(
            value,
            Provenance.Create("sec-edgar", PeriodEnd, published, published, "0000320193-26-000001"));
    }

    internal static Claim<decimal> Derived(decimal value, Claim<decimal> from) =>
        Claims.Calculation(
            value,
            Provenance.FromSystem("calc.test", PeriodEnd, Published),
            [from.Id]);

    internal static Claim<decimal> Judgement(decimal value, Claim<decimal> from) =>
        Claims.AiInterpretation(
            value,
            Provenance.FromSystem("model.test", PeriodEnd, Published),
            [from.Id],
            Confidence.Create(0.7m));

    /// <summary>A context that can see the filing above.</summary>
    internal static CalculationContext Context(DateTime? cutoffUtc = null, DateTime? calculatedAtUtc = null)
    {
        var calculatedAt = calculatedAtUtc ?? Now;

        return CalculationContext.Create(
            Subject,
            KnowledgeCutoff.At(cutoffUtc ?? calculatedAt),
            calculatedAt);
    }

    internal static CalculationInput Input(string name, decimal value, DateTime? publishedAtUtc = null) =>
        CalculationInput.Create(name, Fact(value, publishedAtUtc), UnitOfMeasure.Money);
}
