using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class CalculationInputTests
{
    [Fact]
    public void An_input_carries_its_value_and_the_evidence_for_it_as_one_thing()
    {
        var evidence = AnalyticsEvidence.Fact(94_930m);

        var input = CalculationInput.Create("revenue", evidence, UnitOfMeasure.Money);

        Assert.Equal("revenue", input.Name);
        Assert.Equal(94_930m, input.Value);
        Assert.Equal(evidence.Id, input.EvidenceId);
        Assert.Equal(evidence.Provenance, input.Provenance);
        Assert.Equal(UnitOfMeasure.Money, input.Unit);
    }

    /// <summary>
    /// A calculation may stand on another calculation - free cash flow feeding a margin - so long
    /// as nothing in the chain is a judgement.
    /// </summary>
    [Fact]
    public void A_calculation_may_be_used_as_evidence()
    {
        var fact = AnalyticsEvidence.Fact(100m);

        var input = CalculationInput.Create("freeCashFlow", AnalyticsEvidence.Derived(40m, fact), UnitOfMeasure.Money);

        Assert.Equal(40m, input.Value);
    }

    /// <summary>
    /// The line the whole epistemic model rests on. A deterministic metric part-computed from a
    /// model's opinion is not deterministic, and downstream nothing could tell it apart from one
    /// that was measured.
    /// </summary>
    [Fact]
    public void A_judgement_may_not_be_used_as_evidence()
    {
        var judgement = AnalyticsEvidence.Judgement(0.5m, AnalyticsEvidence.Fact(1m));

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => CalculationInput.Create("estimate", judgement, UnitOfMeasure.Ratio));

        Assert.Contains("deterministic", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_input_must_say_which_term_it_stood_for(string name) =>
        Assert.Throws<DomainValidationException>(
            () => CalculationInput.Create(name, AnalyticsEvidence.Fact(1m), UnitOfMeasure.Money));

    [Fact]
    public void An_input_name_has_a_length_limit() =>
        Assert.Throws<DomainValidationException>(
            () => CalculationInput.Create(
                new string('a', CalculationInput.MaxNameLength + 1),
                AnalyticsEvidence.Fact(1m),
                UnitOfMeasure.Money));

    [Fact]
    public void An_input_with_an_unknown_unit_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => CalculationInput.Create("revenue", AnalyticsEvidence.Fact(1m), UnitOfMeasure.Unknown));

    [Fact]
    public void Evidence_is_required() =>
        Assert.Throws<ArgumentNullException>(
            () => CalculationInput.Create("revenue", null!, UnitOfMeasure.Money));
}
