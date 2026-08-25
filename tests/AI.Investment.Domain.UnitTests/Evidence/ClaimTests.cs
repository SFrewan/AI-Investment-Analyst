using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Evidence;

public sealed class ClaimTests
{
    private static readonly DateTime AsOf = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Retrieved = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    // Origin and locator are separate: "sec-edgar" is the registered source, the accession
    // number is the record within it. Before Phase 2 stage 2 both lived in one string, which
    // meant the origin could not be looked up in the registry.
    private static Provenance FilingProvenance() =>
        Provenance.Create(
            "sec-edgar",
            AsOf,
            Published,
            Retrieved,
            sourceRecordId: "0000320193-26-000001");

    private static Provenance SystemProvenance() =>
        Provenance.FromSystem("internal.test-service", AsOf, Retrieved);

    [Fact]
    public void A_fact_carries_provenance_and_no_confidence()
    {
        var claim = Claims.Fact(1_234m, FilingProvenance());

        Assert.Equal(ClaimKind.Fact, claim.Kind);
        Assert.Null(claim.Confidence);
        Assert.Empty(claim.DerivedFrom);
        Assert.True(claim.IsFact);
        Assert.False(claim.IsJudgement);
    }

    [Fact]
    public void Provenance_keeps_the_three_timestamps_separate()
    {
        var provenance = FilingProvenance();

        Assert.Equal(AsOf, provenance.AsOfUtc);
        Assert.Equal(Published, provenance.PublishedAtUtc);
        Assert.Equal(Retrieved, provenance.RetrievedAtUtc);
        Assert.NotEqual(provenance.AsOfUtc, provenance.PublishedAtUtc);
    }

    [Fact]
    public void Provenance_requires_a_source() =>
        Assert.Throws<DomainValidationException>(() => Provenance.Create("  ", AsOf, Published, Retrieved));

    [Fact]
    public void Provenance_requires_utc_timestamps() =>
        Assert.Throws<DomainValidationException>(() =>
            Provenance.Create("source", DateTime.Now, Published, Retrieved));

    /// <summary>
    /// A value retrieved before it was published is the signature of look-ahead bias, which is
    /// the single most common way this class of project produces a beautiful, meaningless track
    /// record.
    /// </summary>
    [Fact]
    public void A_fact_cannot_be_retrieved_before_it_was_published()
    {
        var impossible = Provenance.Create("source", AsOf, Published, Published.AddDays(-1));

        Assert.Throws<DomainRuleViolationException>(() => Claims.Fact(1m, impossible));
    }

    [Fact]
    public void A_fact_cannot_be_published_before_the_period_it_describes()
    {
        var impossible = Provenance.Create("source", Published, AsOf, Retrieved);

        Assert.Throws<DomainRuleViolationException>(() => Claims.Fact(1m, impossible));
    }

    [Fact]
    public void A_calculation_must_identify_its_inputs() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            Claims.Calculation(42m, SystemProvenance(), []));

    [Fact]
    public void A_calculation_records_what_it_derives_from()
    {
        var source = Claims.Fact(10m, FilingProvenance());
        var calculation = Claims.Calculation(20m, SystemProvenance(), [source.Id]);

        Assert.Equal(ClaimKind.Calculation, calculation.Kind);
        Assert.Equal(source.Id, Assert.Single(calculation.DerivedFrom));
        Assert.Null(calculation.Confidence);
    }

    [Fact]
    public void An_ai_interpretation_must_state_confidence()
    {
        var source = Claims.Fact(10m, FilingProvenance());

        Assert.Throws<DomainRuleViolationException>(() =>
            new Claim<string>(
                ClaimId.New(),
                "margins are improving",
                ClaimKind.AiInterpretation,
                SystemProvenance(),
                [source.Id],
                null,
                null));
    }

    [Fact]
    public void An_ai_interpretation_must_cite_evidence() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            Claims.AiInterpretation("margins are improving", SystemProvenance(), [], Confidence.Create(0.7m)));

    [Fact]
    public void A_prediction_must_cite_evidence_and_state_confidence()
    {
        var source = Claims.Fact(10m, FilingProvenance());
        var prediction = Claims.Prediction(12m, SystemProvenance(), [source.Id], Confidence.Create(0.6m));

        Assert.Equal(ClaimKind.Prediction, prediction.Kind);
        Assert.True(prediction.IsJudgement);
        Assert.NotNull(prediction.Confidence);
    }

    /// <summary>
    /// The rule that keeps a model's guess from being presented the way a filed figure is.
    /// </summary>
    [Fact]
    public void Confidence_cannot_be_attached_to_a_fact() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            new Claim<decimal>(
                ClaimId.New(),
                1m,
                ClaimKind.Fact,
                FilingProvenance(),
                null,
                Confidence.Create(0.9m),
                null));

    /// <summary>
    /// The explicit gate that stops a prediction being consumed as though it were measured.
    /// </summary>
    [Fact]
    public void RequireFactValue_refuses_a_prediction()
    {
        var source = Claims.Fact(10m, FilingProvenance());
        var prediction = Claims.Prediction(12m, SystemProvenance(), [source.Id], Confidence.Create(0.6m));

        Assert.Throws<DomainRuleViolationException>(() => prediction.RequireFactValue());
    }

    [Fact]
    public void RequireFactValue_returns_the_value_of_a_fact() =>
        Assert.Equal(10m, Claims.Fact(10m, FilingProvenance()).RequireFactValue());

    [Fact]
    public void Caveats_are_trimmed_and_blanks_dropped()
    {
        var claim = Claims.Fact(1m, FilingProvenance(), ["  restated  ", "   ", ""]);

        Assert.Equal("restated", Assert.Single(claim.Caveats));
    }

    [Fact]
    public void Claims_have_distinct_identities() =>
        Assert.NotEqual(Claims.Fact(1m, FilingProvenance()).Id, Claims.Fact(1m, FilingProvenance()).Id);
}
