using System.Globalization;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.UnitTests.Ai;

/// <summary>One filer, three figures, and the labels an agent would cite them by.</summary>
internal static class AiTestBundles
{
    internal static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Published = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static IngestionSubject Subject { get; } = IngestionSubject.Create("Company", "AAPL");

    internal static EvidenceBundle Standard { get; } = EvidenceBundle.Create(
        Subject,
        KnowledgeCutoff.At(Now),
        [
            Item("financials.revenue", 1000m),
            Item("financials.net-income", 100m),
            Item("financial.net-margin", 0.1m),
        ]);

    /// <summary>The label the standard bundle gives a named item, for building scripted answers.</summary>
    internal static string LabelOf(string name) =>
        Standard.LabelOf(Standard.Items.Single(item => item.Name == name))!;

    internal static decimal ValueOf(string name) =>
        (decimal)Standard.Items.Single(item => item.Name == name).Claim.UntypedValue!;

    internal static string Number(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static EvidenceItem Item(string name, decimal value) =>
        EvidenceItem.Create(
            name,
            Claims.Fact(value, Provenance.Create("sec-edgar", PeriodEnd, Published, Published)));
}
