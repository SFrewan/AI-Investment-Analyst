using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class CalculationContextTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Subject = IngestionSubject.Create("company", "AAPL");

    /// <summary>Live operation: the platform may know everything up to the present.</summary>
    [Fact]
    public void A_live_calculation_has_its_cutoff_at_the_moment_it_runs()
    {
        var context = CalculationContext.Create(Subject, KnowledgeCutoff.At(Now), Now);

        Assert.Equal(Now, context.Cutoff.AsOfUtc);
        Assert.Equal(Now, context.CalculatedAtUtc);
    }

    /// <summary>
    /// The shape that makes backtesting expressible: the calculation happens now, but is only
    /// permitted to know what was public five years ago.
    /// </summary>
    [Fact]
    public void A_backtest_calculates_now_about_a_cutoff_in_the_past()
    {
        var cutoff = new DateTime(2021, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        var context = CalculationContext.Create(Subject, KnowledgeCutoff.At(cutoff), Now);

        Assert.Equal(cutoff, context.Cutoff.AsOfUtc);
        Assert.Equal(Now, context.CalculatedAtUtc);
        Assert.NotEqual(context.Cutoff.AsOfUtc, context.CalculatedAtUtc);
    }

    /// <summary>Permitting knowledge from after the calculation is look-ahead written as config.</summary>
    [Fact]
    public void A_cutoff_may_not_sit_in_the_future_of_the_calculation()
    {
        var exception = Assert.Throws<DomainRuleViolationException>(
            () => CalculationContext.Create(Subject, KnowledgeCutoff.At(Now.AddDays(1)), Now));

        Assert.Contains("look-ahead", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_moment_of_calculation_must_be_utc() =>
        Assert.Throws<DomainValidationException>(
            () => CalculationContext.Create(
                Subject,
                KnowledgeCutoff.At(Now),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void A_context_defers_admissibility_to_its_cutoff()
    {
        var context = CalculationContext.Create(Subject, KnowledgeCutoff.At(Now), Now);

        Assert.True(context.Admits(AnalyticsEvidence.Fact(1m).Provenance));
        Assert.False(context.Admits(AnalyticsEvidence.Fact(1m, Now.AddDays(1)).Provenance));
    }
}
