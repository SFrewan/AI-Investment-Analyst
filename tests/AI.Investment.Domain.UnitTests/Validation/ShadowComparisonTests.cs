using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Validation;

/// <summary>
/// Matching Phase 6's shadow measurements against what actually happened.
/// </summary>
/// <remarks>
/// The comparison is arithmetic over records that were inert when they were written. What these
/// tests are mostly about is the refusal to let a divergence count read as an argument: "a higher
/// level would have acted more often" describes the policy, and only the outcomes of those extra
/// actions bear on whether it should.
/// </remarks>
public sealed class ShadowComparisonTests
{
    private static readonly DateTime Recorded = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ShadowDecision Decision(
        Guid proposalId,
        PolicyOutcome actual,
        PolicyOutcome shadow) =>
        ShadowDecision.Record(
            Guid.NewGuid(),
            proposalId,
            Capability.SimulatedExecution,
            "execution.simulated-order",
            RiskTier.Medium,
            Money.Create(1_000m, Currency.Usd),
            AutonomyMode.PrepareForApproval,
            actual,
            AutonomyMode.AutoExecuteBounded,
            shadow,
            "measurement",
            Recorded);

    [Fact]
    public void Agreement_and_divergence_are_counted_against_the_whole_period()
    {
        var decisions = Enumerable.Range(0, 30)
            .Select(index => Decision(
                Guid.NewGuid(),
                PolicyOutcome.RequireApproval,
                index % 3 == 0 ? PolicyOutcome.Execute : PolicyOutcome.RequireApproval))
            .ToList();

        var result = ShadowComparisonResult.From(decisions, new Dictionary<Guid, OutcomeLabel>());

        Assert.Equal(30, result.Total);
        Assert.Equal(20, result.Agreements);
        Assert.Equal(10, result.DivergenceCount);
        Assert.Equal(10, result.ShadowWouldHaveExecutedAndActualDidNot);
        Assert.Equal(0, result.ActualExecutedAndShadowWouldNot);
        Assert.Equal(20m / 30m, result.AgreementRate.Value);
        Assert.Equal(10m / 30m, result.DivergenceRate.Value);
    }

    /// <summary>
    /// The point of the whole section: extra actions with no known outcome prove nothing, and the
    /// result says so instead of reporting a rate.
    /// </summary>
    [Fact]
    public void Divergences_without_outcomes_are_not_evidence_and_are_reported_as_such()
    {
        var decisions = Enumerable.Range(0, 25)
            .Select(_ => Decision(Guid.NewGuid(), PolicyOutcome.RequireApproval, PolicyOutcome.Execute))
            .ToList();

        var result = ShadowComparisonResult.From(decisions, new Dictionary<Guid, OutcomeLabel>());

        Assert.Equal(25, result.ShadowWouldHaveExecutedAndActualDidNot);
        Assert.False(result.DivergenceHitRate.IsMeasured);
        Assert.Contains(
            "description of the policy rather than evidence",
            result.DivergenceHitRate.Explanation,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// With outcomes, the extra actions can finally be judged - and the rate is over those alone.
    /// </summary>
    [Fact]
    public void The_extra_actions_are_judged_only_where_their_outcomes_are_known()
    {
        var labels = new Dictionary<Guid, OutcomeLabel>();
        var decisions = new List<ShadowDecision>();

        for (var index = 0; index < 40; index++)
        {
            var proposalId = Guid.NewGuid();

            decisions.Add(Decision(proposalId, PolicyOutcome.RequireApproval, PolicyOutcome.Execute));

            labels[proposalId] = index % 4 == 0 ? OutcomeLabel.FalsePositive : OutcomeLabel.TruePositive;
        }

        // Ten more with no outcome at all. They must not dilute the rate, and must not inflate it.
        for (var index = 0; index < 10; index++)
        {
            decisions.Add(Decision(Guid.NewGuid(), PolicyOutcome.RequireApproval, PolicyOutcome.Execute));
        }

        var result = ShadowComparisonResult.From(decisions, labels);

        Assert.Equal(50, result.ShadowWouldHaveExecutedAndActualDidNot);
        Assert.True(result.DivergenceHitRate.IsMeasured);
        Assert.Equal(40, result.DivergenceHitRate.SampleSize);
        Assert.Equal(0.75m, result.DivergenceHitRate.Value);
    }

    /// <summary>
    /// The rarer direction is counted too: the platform acted where a higher level would not have.
    /// </summary>
    [Fact]
    public void The_platform_acting_where_a_higher_level_would_not_is_counted_separately()
    {
        var result = ShadowComparisonResult.From(
            [Decision(Guid.NewGuid(), PolicyOutcome.Execute, PolicyOutcome.Deny)],
            new Dictionary<Guid, OutcomeLabel>(),
            minimumSample: 1);

        Assert.Equal(1, result.ActualExecutedAndShadowWouldNot);
        Assert.Equal(0, result.ShadowWouldHaveExecutedAndActualDidNot);
        Assert.Equal(1, result.DivergenceCount);
    }

    [Fact]
    public void No_measurements_in_the_window_is_unavailable_rather_than_perfect_agreement()
    {
        var result = ShadowComparisonResult.From([], new Dictionary<Guid, OutcomeLabel>());

        Assert.Equal(0, result.Total);
        Assert.Equal(MetricAvailability.Unavailable, result.AgreementRate.Availability);
        Assert.Null(result.AgreementRate.Value);
        Assert.Empty(result.Divergences);
    }

    [Fact]
    public void Too_few_measurements_withholds_the_rates()
    {
        var result = ShadowComparisonResult.From(
            [Decision(Guid.NewGuid(), PolicyOutcome.RequireApproval, PolicyOutcome.RequireApproval)],
            new Dictionary<Guid, OutcomeLabel>());

        Assert.Equal(MetricAvailability.Insufficient, result.AgreementRate.Availability);
        Assert.Null(result.AgreementRate.Value);
    }

    /// <summary>
    /// Every divergence keeps the proposal it was about, so a reader can walk from a rate back to
    /// the individual records behind it.
    /// </summary>
    [Fact]
    public void Every_divergence_stays_traceable_to_its_proposal()
    {
        var proposalId = Guid.NewGuid();

        var result = ShadowComparisonResult.From(
            [Decision(proposalId, PolicyOutcome.RequireApproval, PolicyOutcome.Execute)],
            new Dictionary<Guid, OutcomeLabel> { [proposalId] = OutcomeLabel.TruePositive },
            minimumSample: 1);

        var divergence = Assert.Single(result.Divergences);

        Assert.Equal(proposalId, divergence.ProposalId);
        Assert.Equal(PolicyOutcome.RequireApproval, divergence.ActualOutcome);
        Assert.Equal(PolicyOutcome.Execute, divergence.ShadowOutcome);
        Assert.Equal(OutcomeLabel.TruePositive, divergence.Label);
        Assert.Equal(Recorded, divergence.RecordedAtUtc);
    }
}
