using System.Globalization;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ai;

public sealed class GroundednessValidatorTests
{
    private static readonly EvidenceBundle Bundle = AiFixtures.Bundle();

    private static ClaimId ClaimFor(string name) =>
        Bundle.Items.Single(item => item.Name == name).Claim.Id;

    private static GroundednessReport Validate(
        IEnumerable<AssertedFigure>? figures = null,
        IEnumerable<string>? narrative = null,
        GroundednessPolicy policy = GroundednessPolicy.Strict) =>
        GroundednessValidator.Validate(
            Bundle,
            new AiFixtures.TestOutput(figures ?? [], narrative ?? []),
            tolerance: null,
            policy);

    [Fact]
    public void A_cited_figure_that_matches_its_claim_is_grounded()
    {
        var report = Validate(
            [AssertedFigure.Create("net-margin", 0.1m, ClaimFor("financial.net-margin"))]);

        Assert.True(report.IsGrounded);
        Assert.Equal(ClaimFor("financial.net-margin"), Assert.Single(report.MatchedClaims));
    }

    /// <summary>
    /// The evidence list on a result is what the validator matched, never what the agent said it
    /// read. An agent's own account of its sources is exactly the thing a model embellishes.
    /// </summary>
    [Fact]
    public void The_matched_claims_are_derived_rather_than_taken_on_trust()
    {
        var report = Validate(
        [
            AssertedFigure.Create("revenue", 1000m, ClaimFor("financials.revenue")),
            AssertedFigure.Create("net-income", 100m, ClaimFor("financials.net-income")),
        ]);

        Assert.Equal(2, report.MatchedClaims.Count);
    }

    [Fact]
    public void A_cited_figure_whose_value_disagrees_with_its_claim_is_ungrounded()
    {
        var report = Validate([AssertedFigure.Create("revenue", 1200m, ClaimFor("financials.revenue"))]);

        Assert.False(report.IsGrounded);
        Assert.Contains("1200", report.Explain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// "Cited nothing" and "cited something that does not exist" are different findings, and the
    /// second says more about the answer than any figure in it.
    /// </summary>
    [Fact]
    public void A_figure_citing_a_label_that_is_not_in_the_bundle_is_ungrounded()
    {
        var report = Validate(
            [AssertedFigure.Create("revenue", 1000m, citedClaimId: null, isPercentage: false, citedLabel: "C99")]);

        Assert.False(report.IsGrounded);
        Assert.Contains("C99", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_uncited_figure_may_still_match_any_claim_in_the_bundle()
    {
        var report = Validate([AssertedFigure.Create("something", 1000m)]);

        Assert.True(report.IsGrounded);
    }

    [Fact]
    public void An_uncited_figure_that_matches_nothing_is_ungrounded() =>
        Assert.False(Validate([AssertedFigure.Create("invented", 4242m)]).IsGrounded);

    /// <summary>Display rounding must pass; anything wider is a fabricated figure landing inside a window.</summary>
    [Theory]
    [InlineData("1000.4", true)]
    [InlineData("1004", true)]
    [InlineData("1010", false)]
    [InlineData("900", false)]
    public void Tolerance_accommodates_rounding_and_nothing_else(string quoted, bool expected)
    {
        var report = GroundednessValidator.Validate(
            Bundle,
            new AiFixtures.TestOutput(
                [
                    AssertedFigure.Create(
                        "revenue",
                        decimal.Parse(quoted, CultureInfo.InvariantCulture),
                        ClaimFor("financials.revenue")),
                ],
                []));

        Assert.Equal(expected, report.IsGrounded);
    }

    [Fact]
    public void A_percentage_figure_matches_the_ratio_it_was_stored_as()
    {
        var report = Validate(
            [AssertedFigure.Create("net-margin", 10m, ClaimFor("financial.net-margin"), isPercentage: true)]);

        Assert.True(report.IsGrounded);
    }

    /// <summary>
    /// The backstop. An agent that puts nothing in its figure list and writes the number into a
    /// sentence instead passes a structural check while making one up.
    /// </summary>
    [Fact]
    public void A_number_smuggled_into_prose_is_caught()
    {
        var report = Validate(narrative: ["margins improved to 42% this period"]);

        Assert.False(report.IsGrounded);
        Assert.Contains("42%", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_in_prose_that_traces_to_a_claim_is_admissible() =>
        Assert.True(Validate(narrative: ["revenue of 1000 was reported"]).IsGrounded);

    /// <summary>
    /// A sentence mentioning the period a filing covers is quoting the bundle's own provenance, not
    /// inventing a figure.
    /// </summary>
    [Theory]
    [InlineData("the filing was published in 2026")]
    [InlineData("the period ended on 31 December")]
    [InlineData("published 2026-02-10")]
    public void Calendar_components_of_the_evidence_dates_are_admissible(string prose) =>
        Assert.True(Validate(narrative: [prose]).IsGrounded);

    [Fact]
    public void The_structural_policy_does_not_scan_prose()
    {
        var report = Validate(
            narrative: ["margins improved to 42% this period"],
            policy: GroundednessPolicy.Structural);

        Assert.True(report.IsGrounded);
        Assert.Empty(report.UngroundedMentions);
    }

    [Fact]
    public void The_structural_policy_still_checks_the_figures()
    {
        var report = Validate(
            [AssertedFigure.Create("invented", 4242m)],
            policy: GroundednessPolicy.Structural);

        Assert.False(report.IsGrounded);
    }

    [Fact]
    public void An_output_with_no_figures_and_no_prose_is_vacuously_grounded_and_matches_nothing()
    {
        var report = Validate();

        Assert.True(report.IsGrounded);
        Assert.Empty(report.MatchedClaims);
    }

    [Fact]
    public void An_asserted_figure_must_be_named() =>
        Assert.Throws<DomainValidationException>(() => AssertedFigure.Create("  ", 1m));

    /// <summary>
    /// Beyond a few per cent the check stops distinguishing a rounded figure from an invented one,
    /// so the tolerance itself is bounded rather than left to a caller's judgement.
    /// </summary>
    [Theory]
    [InlineData("0.06")]
    [InlineData("-0.01")]
    public void A_tolerance_wide_enough_to_be_meaningless_is_refused(string relative) =>
        Assert.Throws<DomainValidationException>(() =>
            GroundednessTolerance.Create(decimal.Parse(relative, CultureInfo.InvariantCulture), 0m));

    [Fact]
    public void An_exact_tolerance_admits_only_the_exact_value()
    {
        Assert.True(GroundednessTolerance.Exact.Matches(1000m, 1000m));
        Assert.False(GroundednessTolerance.Exact.Matches(1000.0001m, 1000m));
    }
}
