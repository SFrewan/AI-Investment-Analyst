using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.UnitTests.Ai;

/// <summary>One filer, two periods, and dates that satisfy the Phase 1 evidence rules.</summary>
internal static class AiFixtures
{
    internal static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Published = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static IngestionSubject Subject { get; } = IngestionSubject.Create("Company", "AAPL");

    internal static KnowledgeCutoff Cutoff => KnowledgeCutoff.At(Now);

    internal static Claim<decimal> Fact(decimal value, DateTime? publishedUtc = null) =>
        Claims.Fact(
            value,
            Provenance.Create("sec-edgar", PeriodEnd, publishedUtc ?? Published, publishedUtc ?? Published));

    internal static Claim<decimal> Calculation(decimal value, params ClaimId[] derivedFrom) =>
        Claims.Calculation(
            value,
            Provenance.FromSystem("calc.test", PeriodEnd, Published),
            derivedFrom);

    internal static Claim<decimal> Judgement(decimal value, ClaimId derivedFrom) =>
        Claims.AiInterpretation(
            value,
            Provenance.FromSystem("agent.test", PeriodEnd, Published),
            [derivedFrom],
            Confidence.Create(0.5m));

    internal static EvidenceItem Item(string name, decimal value, DateTime? publishedUtc = null) =>
        EvidenceItem.Create(name, Fact(value, publishedUtc));

    /// <summary>Revenue 1000, net income 100, net margin 0.1 - numbers chosen so arithmetic is exact.</summary>
    internal static EvidenceBundle Bundle() =>
        EvidenceBundle.Create(
            Subject,
            Cutoff,
            [
                Item("financials.revenue", 1000m),
                Item("financials.net-income", 100m),
                Item("financial.net-margin", 0.1m),
            ]);

    internal static AgentId Agent => AgentId.Create("financial");

    internal static PromptRef Prompt => PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0);

    internal static ModelRef Model => ModelRef.Create("test", "scripted", "2026-01-01");

    internal static AgentDiagnostics Diagnostics =>
        AgentDiagnostics.Create(Model, Prompt, 100, 50, 0.001m, 25, 1);

    /// <summary>An output that states the figures and prose it is given, for validator tests.</summary>
    internal sealed class TestOutput : IGroundedOutput
    {
        private readonly List<AssertedFigure> _figures;
        private readonly List<string> _narrative;

        internal TestOutput(IEnumerable<AssertedFigure> figures, IEnumerable<string> narrative)
        {
            _figures = figures.ToList();
            _narrative = narrative.ToList();
        }

        public IReadOnlyList<AssertedFigure> AssertedFigures() => _figures;

        public IReadOnlyList<string> NarrativeFragments() => _narrative;
    }
}
