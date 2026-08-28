using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.UnitTests.Autonomy;

/// <summary>
/// Builds the validation report that a promotion warrant would need, so tests can reach the far side
/// of the gate.
/// </summary>
/// <remarks>
/// <para>
/// This is the one piece of test scaffolding in the phase that is duplicated across assemblies, and
/// the duplication is deliberate. Test projects cannot reference one another, and the alternative -
/// a factory in production code that manufactures a justified assessment - would be a backdoor
/// through the exact gate the phase exists to build. A copied fixture is the lesser problem, and it
/// is honest: the only way any test reaches a warrant is by constructing evidence that genuinely
/// clears the bar.
/// </para>
/// <para>
/// Nothing here is used to produce a report the platform publishes. It exists so that the behaviour
/// on the permitted side of the gate is tested at all, rather than being written once and never
/// executed until the day it matters.
/// </para>
/// </remarks>
internal static class JustifiedEvidence
{
    internal static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    internal static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    /// <summary>A report that clears <see cref="PromotionCriteria.Standard"/> on every count.</summary>
    internal static ValidationReport Report(DateTime? generatedAtUtc = null)
    {
        // 120 scored predictions: 90 right of 120 calls to act, which is a hit rate of 0.75.
        var labels = Enumerable.Range(0, 120)
            .Select(index => index % 4 == 0 ? OutcomeLabel.FalsePositive : OutcomeLabel.TruePositive)
            .ToList();

        // The same 120, stated at 0.75 and occurring 75% of the time. Brier 0.1875.
        var calibration = labels
            .Select(label => (StatedRatio: 0.75m, Occurred: label == OutcomeLabel.TruePositive))
            .ToList();

        var shadowLabels = new Dictionary<Guid, OutcomeLabel>();
        var shadow = new List<ShadowDecision>();

        for (var index = 0; index < 40; index++)
        {
            var proposalId = Guid.NewGuid();

            shadowLabels[proposalId] = index % 4 == 0
                ? OutcomeLabel.FalsePositive
                : OutcomeLabel.TruePositive;

            shadow.Add(ShadowDecision.Record(
                Guid.NewGuid(),
                proposalId,
                Capability.SimulatedExecution,
                "execution.simulated-order",
                RiskTier.Low,
                Usd(1_000m),
                AutonomyMode.PrepareForApproval,
                PolicyOutcome.RequireApproval,
                AutonomyMode.AutoExecuteBounded,
                PolicyOutcome.Execute,
                "measurement",
                (generatedAtUtc ?? Now).AddDays(-10)));
        }

        var roundTrips = Enumerable.Range(0, 30)
            .Select(index => new RoundTrip(
                (generatedAtUtc ?? Now).AddDays(-60),
                (generatedAtUtc ?? Now).AddDays(-30),
                index % 4 == 0 ? -0.05m : 0.12m))
            .ToList();

        return ValidationReport.Create(
            Guid.NewGuid(),
            generatedAtUtc ?? Now,
            EvaluationWindow.Create(
                (generatedAtUtc ?? Now).AddDays(-180),
                (generatedAtUtc ?? Now).AddDays(-1),
                TimeSpan.FromDays(30),
                TimeSpan.FromDays(1)),
            Percentage.Zero,
            CalculationVersion.Create(1, 0),
            Benchmark(generatedAtUtc ?? Now),
            ["test-feed"],
            120,
            120,
            0,
            ConfusionMatrix.From(labels),
            CalibrationCurve.From(calibration),
            PerformanceCalculator.MeanRoundTripReturn(roundTrips),
            Measurement.Measured(0.02m, 200, "benchmark"),
            ShadowComparisonResult.From(shadow, shadowLabels),
            [],
            []);
    }

    internal static BenchmarkDefinition Benchmark(DateTime nowUtc) =>
        BenchmarkDefinition.Create(
            "index buy-and-hold",
            IngestionSubject.Create("Security", "SPY"),
            "security.close",
            BenchmarkRule.BuyAndHold,
            Usd(100_000m),
            Percentage.Zero,
            nowUtc.AddDays(-200));

    /// <summary>An assessment that clears the bar, for the capability that has an execution path.</summary>
    internal static PromotionAssessment Assessment(DateTime? nowUtc = null) =>
        PromotionAssessment.Evaluate(
            Capability.SimulatedExecution,
            AutonomyMode.AutoExecuteBounded,
            Report(nowUtc ?? Now),
            PromotionCriteria.Standard,
            nowUtc ?? Now);

    /// <summary>A warrant on that assessment, at the lowest risk tier - the only one permitted.</summary>
    internal static PromotionWarrant Warrant(
        string? actionType = null,
        string environment = "Test",
        decimal maxExposure = 5_000m,
        DateTime? nowUtc = null,
        TimeSpan? validFor = null) =>
        PromotionWarrant.Issue(
            Assessment(nowUtc),
            actionType,
            environment,
            RiskTier.Low,
            Usd(maxExposure),
            "operator@example.test",
            "the measured evidence clears every criterion and the capability is simulated.",
            nowUtc ?? Now,
            validFor ?? TimeSpan.FromDays(7));
}
