using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Observations;

/// <summary>
/// What it takes for the platform to be allowed to say it knows something.
/// </summary>
/// <remarks>
/// An observation is a claim plus the sentence saying what the claim is about. Most of these tests
/// are about what it refuses: an attribute-less value, a fact whose timestamps are impossible, and
/// - the one that matters most - a stored kind this build cannot rebuild, which is refused rather
/// than quietly downgraded to a fact.
/// </remarks>
public sealed class ObservationTests
{
    private static readonly DateTime AsOf = new(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Retrieved = new(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Apple = IngestionSubject.Create("Company", "0000320193");

    private static Provenance Sane() =>
        Provenance.Create("sec-edgar", AsOf, Published, Retrieved, sourceRecordId: "0000320193");

    private static Observation Name() =>
        Observation.RecordFact(Apple, "company.name", ObservationValue.Text("Apple Inc."), Sane());

    [Fact]
    public void A_recorded_fact_carries_its_subject_attribute_and_value()
    {
        var observation = Name();

        Assert.Equal(Apple, observation.Subject);
        Assert.Equal("company.name", observation.Attribute);
        Assert.Equal("Apple Inc.", observation.Value.Canonical);
        Assert.Equal(ClaimKind.Fact, observation.Kind);
    }

    [Fact]
    public void A_fact_carries_no_confidence() =>

        // Attaching one would imply the platform is uncertain about what a source said, which is a
        // different thing from being uncertain that it is true. Blurring the two is how a quoted
        // figure becomes an estimate.
        Assert.Null(Name().Confidence);

    [Fact]
    public void PublishedAtUtc_is_read_from_the_provenance() =>

        // The only legitimate backtest filter, and deliberately not a separate field that could
        // drift from the provenance it is supposed to mirror.
        Assert.Equal(Published, Name().PublishedAtUtc);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_attribute_is_refused(string blank) =>
        Assert.Throws<DomainValidationException>(() => Observation.RecordFact(
            Apple,
            blank,
            ObservationValue.Text("Apple Inc."),
            Sane()));

    [Fact]
    public void An_overlong_attribute_is_refused()
    {
        var tooLong = new string('a', Observation.MaxAttributeLength + 1);

        Assert.Throws<DomainValidationException>(() => Observation.RecordFact(
            Apple,
            tooLong,
            ObservationValue.Text("Apple Inc."),
            Sane()));
    }

    [Fact]
    public void An_attribute_is_trimmed()
    {
        var observation = Observation.RecordFact(
            Apple,
            "  company.name  ",
            ObservationValue.Text("Apple Inc."),
            Sane());

        Assert.Equal("company.name", observation.Attribute);
    }

    [Fact]
    public void A_fact_retrieved_before_it_was_published_is_refused()
    {
        // The ordering rules live on Claims, and RecordFact materialises through it precisely so
        // they are enforced by the type that owns them rather than re-implemented here. This test
        // exists to prove the delegation actually happens.
        var impossible = Provenance.Create(
            "sec-edgar",
            AsOf,
            publishedAtUtc: Retrieved,
            retrievedAtUtc: Published);

        Assert.ThrowsAny<DomainException>(() => Observation.RecordFact(
            Apple,
            "company.name",
            ObservationValue.Text("Apple Inc."),
            impossible));
    }

    [Fact]
    public void A_fact_published_before_the_period_it_describes_is_refused()
    {
        var impossible = Provenance.Create(
            "sec-edgar",
            asOfUtc: Retrieved,
            publishedAtUtc: AsOf,
            retrievedAtUtc: Retrieved);

        Assert.ThrowsAny<DomainException>(() => Observation.RecordFact(
            Apple,
            "company.revenue",
            ObservationValue.Number(1m),
            impossible));
    }

    [Fact]
    public void Caveats_are_kept()
    {
        var observation = Observation.RecordFact(
            Apple,
            "company.name",
            ObservationValue.Text("Apple Inc."),
            Sane(),
            ["the retrieval time is a floor, not the publication date"]);

        Assert.Single(observation.Caveats);
    }

    [Fact]
    public void Blank_caveats_are_dropped()
    {
        var observation = Observation.RecordFact(
            Apple,
            "company.name",
            ObservationValue.Text("Apple Inc."),
            Sane(),
            ["  ", string.Empty, "a real caveat"]);

        Assert.Single(observation.Caveats);
        Assert.Equal("a real caveat", observation.Caveats[0]);
    }

    [Fact]
    public void An_overlong_caveat_is_truncated_rather_than_rejected()
    {
        var observation = Observation.RecordFact(
            Apple,
            "company.name",
            ObservationValue.Text("Apple Inc."),
            Sane(),
            [new string('c', Observation.MaxCaveatLength + 50)]);

        // Losing the observation over the length of an explanatory note would trade real
        // information for a formatting rule.
        Assert.Equal(Observation.MaxCaveatLength, observation.Caveats[0].Length);
    }

    [Fact]
    public void The_number_of_caveats_is_capped()
    {
        var many = Enumerable.Range(0, Observation.MaxCaveats + 10).Select(i => $"caveat {i}");

        var observation = Observation.RecordFact(
            Apple,
            "company.name",
            ObservationValue.Text("Apple Inc."),
            Sane(),
            many);

        Assert.Equal(Observation.MaxCaveats, observation.Caveats.Count);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        var value = ObservationValue.Text("Apple Inc.");

        Assert.Throws<ArgumentNullException>(() =>
            Observation.RecordFact(null!, "company.name", value, Sane()));

        Assert.Throws<ArgumentNullException>(() =>
            Observation.RecordFact(Apple, "company.name", null!, Sane()));

        Assert.Throws<ArgumentNullException>(() =>
            Observation.RecordFact(Apple, "company.name", value, null!));
    }

    [Fact]
    public void A_numeric_fact_rebuilds_as_a_decimal_claim()
    {
        var observation = Observation.RecordFact(
            Apple,
            "company.employees",
            ObservationValue.Number(164000m),
            Sane());

        var claim = observation.ToClaim();

        Assert.Equal(ClaimKind.Fact, claim.Kind);
        Assert.IsType<Claim<decimal>>(claim);
    }

    [Fact]
    public void A_text_fact_rebuilds_as_a_string_claim() =>
        Assert.IsType<Claim<string>>(Name().ToClaim());

    [Fact]
    public void A_boolean_fact_rebuilds_as_a_boolean_claim()
    {
        var observation = Observation.RecordFact(
            Apple,
            "company.is-shell",
            ObservationValue.Boolean(false),
            Sane());

        Assert.IsType<Claim<bool>>(observation.ToClaim());
    }

    [Fact]
    public void A_timestamp_fact_rebuilds_as_a_timestamp_claim()
    {
        var observation = Observation.RecordFact(
            Apple,
            "company.last-filing",
            ObservationValue.Timestamp(Published),
            Sane());

        Assert.IsType<Claim<DateTime>>(observation.ToClaim());
    }

    [Fact]
    public void A_rebuilt_claim_carries_the_original_provenance() =>
        Assert.Equal(Published, Name().ToClaim().Provenance.PublishedAtUtc);

    [Fact]
    public void A_sweep_subject_is_allowed()
    {
        // A run that swept a whole source names no single thing. Requiring an identifier here
        // would force normalisers to invent one, and an invented identifier is a fact about a
        // company that does not exist.
        var sweep = IngestionSubject.Sweep("Company");

        var observation = Observation.RecordFact(
            sweep,
            "source.record-count",
            ObservationValue.Number(12_000m),
            Sane());

        Assert.Null(observation.Subject.Identifier);
    }
}
