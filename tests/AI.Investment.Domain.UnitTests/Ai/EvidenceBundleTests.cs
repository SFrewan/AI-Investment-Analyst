using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ai;

public sealed class EvidenceBundleTests
{
    [Fact]
    public void A_bundle_keeps_its_subject_cutoff_and_items()
    {
        var bundle = AiFixtures.Bundle();

        Assert.Equal(AiFixtures.Subject, bundle.Subject);
        Assert.Equal(AiFixtures.Cutoff, bundle.Cutoff);
        Assert.Equal(3, bundle.Count);
    }

    /// <summary>
    /// An agent given nothing to read answers from the model's memory, which is the failure this
    /// whole layer exists to prevent.
    /// </summary>
    [Fact]
    public void An_empty_bundle_is_refused() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            EvidenceBundle.Create(AiFixtures.Subject, AiFixtures.Cutoff, []));

    /// <summary>
    /// Feeding one agent's opinion to the next is how a single invented figure becomes an apparent
    /// consensus.
    /// </summary>
    [Fact]
    public void A_judgement_may_not_enter_a_bundle()
    {
        var fact = AiFixtures.Fact(1000m);

        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            EvidenceBundle.Create(
                AiFixtures.Subject,
                AiFixtures.Cutoff,
                [
                    EvidenceItem.Create("financials.revenue", fact),
                    EvidenceItem.Create("agent.opinion", AiFixtures.Judgement(0.5m, fact.Id)),
                ]));

        Assert.Equal("EvidenceBundle.JudgementIsNotEvidence", exception.Rule);
    }

    [Fact]
    public void A_calculation_is_admissible_because_it_is_not_a_judgement()
    {
        var fact = AiFixtures.Fact(1000m);

        var bundle = EvidenceBundle.Create(
            AiFixtures.Subject,
            AiFixtures.Cutoff,
            [
                EvidenceItem.Create("financials.revenue", fact),
                EvidenceItem.Create("financial.net-margin", AiFixtures.Calculation(0.1m, fact.Id)),
            ]);

        Assert.Equal(2, bundle.Count);
    }

    /// <summary>
    /// Admissibility is judged on publication, never on when this system happened to fetch it.
    /// </summary>
    [Fact]
    public void Evidence_published_after_the_cutoff_is_refused()
    {
        var replay = KnowledgeCutoff.At(AiFixtures.Published.AddDays(-1));

        var exception = Assert.Throws<DomainRuleViolationException>(() =>
            EvidenceBundle.Create(AiFixtures.Subject, replay, [AiFixtures.Item("financials.revenue", 1000m)]));

        Assert.Equal("EvidenceBundle.LookAhead", exception.Rule);
    }

    /// <summary>
    /// The hash answers one question - is this the same evidence as last time? - so it must be
    /// computed from what the claims say, not from the identities they were handed in memory.
    /// </summary>
    [Fact]
    public void Two_bundles_built_from_identical_data_have_the_same_hash() =>
        Assert.Equal(AiFixtures.Bundle().Hash, AiFixtures.Bundle().Hash);

    [Fact]
    public void Changing_one_value_changes_the_hash()
    {
        var altered = EvidenceBundle.Create(
            AiFixtures.Subject,
            AiFixtures.Cutoff,
            [
                AiFixtures.Item("financials.revenue", 1001m),
                AiFixtures.Item("financials.net-income", 100m),
                AiFixtures.Item("financial.net-margin", 0.1m),
            ]);

        Assert.NotEqual(AiFixtures.Bundle().Hash, altered.Hash);
    }

    [Fact]
    public void Changing_only_the_name_of_an_item_changes_the_hash()
    {
        var renamed = EvidenceBundle.Create(
            AiFixtures.Subject,
            AiFixtures.Cutoff,
            [
                AiFixtures.Item("financials.turnover", 1000m),
                AiFixtures.Item("financials.net-income", 100m),
                AiFixtures.Item("financial.net-margin", 0.1m),
            ]);

        Assert.NotEqual(AiFixtures.Bundle().Hash, renamed.Hash);
    }

    /// <summary>Order of assembly must not change the fingerprint, or the hash tracks the caller.</summary>
    [Fact]
    public void The_order_items_are_supplied_in_does_not_affect_the_hash()
    {
        var reversed = EvidenceBundle.Create(
            AiFixtures.Subject,
            AiFixtures.Cutoff,
            [
                AiFixtures.Item("financial.net-margin", 0.1m),
                AiFixtures.Item("financials.net-income", 100m),
                AiFixtures.Item("financials.revenue", 1000m),
            ]);

        Assert.Equal(AiFixtures.Bundle().Hash, reversed.Hash);
    }

    [Fact]
    public void Labels_are_stable_and_resolve_back_to_their_item()
    {
        var bundle = AiFixtures.Bundle();

        for (var index = 0; index < bundle.Count; index++)
        {
            var label = EvidenceBundle.LabelAt(index);

            Assert.True(bundle.TryResolveLabel(label, out var item));
            Assert.Equal(bundle.Items[index], item);
            Assert.Equal(label, bundle.LabelOf(bundle.Items[index]));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X1")]
    [InlineData("C0")]
    [InlineData("C99")]
    [InlineData("Cabc")]
    public void A_label_that_names_nothing_does_not_resolve(string? label)
    {
        Assert.False(AiFixtures.Bundle().TryResolveLabel(label, out var item));
        Assert.Null(item);
    }

    [Fact]
    public void Only_numeric_claims_are_offered_for_groundedness_checking()
    {
        var bundle = AiFixtures.Bundle();

        Assert.Equal(bundle.Count, bundle.NumericClaims().Count);
    }

    [Fact]
    public void An_item_must_be_named() =>
        Assert.Throws<DomainValidationException>(() => EvidenceItem.Create("  ", AiFixtures.Fact(1m)));
}
