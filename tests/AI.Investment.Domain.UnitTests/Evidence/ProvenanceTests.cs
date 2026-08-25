using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Evidence;

/// <summary>
/// Phase 2 stage 2 split the origin from the locator. Before it, a filing's provenance was the
/// single string "sec-edgar:0000320193-26-000001", which could not be looked up in the source
/// registry and did not compare equal to another claim from the same source.
/// </summary>
public sealed class ProvenanceTests
{
    private static readonly DateTime AsOf = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Retrieved = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_origin_is_a_registry_key_and_the_locator_is_separate()
    {
        var provenance = Provenance.Create(
            "sec-edgar",
            AsOf,
            Published,
            Retrieved,
            sourceRecordId: "0000320193-26-000001");

        Assert.Equal(SourceId.Create("sec-edgar"), provenance.SourceId);
        Assert.Equal("0000320193-26-000001", provenance.SourceRecordId);
    }

    /// <summary>
    /// The point of the split: two values from the same source share an origin, whatever record
    /// each came from.
    /// </summary>
    [Fact]
    public void Two_records_from_the_same_source_share_an_origin()
    {
        var first = Provenance.Create("sec-edgar", AsOf, Published, Retrieved, sourceRecordId: "a");
        var second = Provenance.Create("sec-edgar", AsOf, Published, Retrieved, sourceRecordId: "b");

        Assert.Equal(first.SourceId, second.SourceId);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_locator_is_optional()
    {
        var provenance = Provenance.Create("fred", AsOf, Published, Retrieved);

        Assert.Null(provenance.SourceRecordId);
    }

    [Fact]
    public void A_blank_locator_is_normalised_to_none() =>
        Assert.Null(Provenance.Create("fred", AsOf, Published, Retrieved, sourceRecordId: "   ")
            .SourceRecordId);

    /// <summary>
    /// An identifier the registry could never hold is rejected at the point the claim is made,
    /// rather than becoming an origin nothing can resolve.
    /// </summary>
    [Theory]
    [InlineData("sec-edgar:0000320193")]
    [InlineData("SEC EDGAR")]
    [InlineData("")]
    public void An_origin_that_is_not_a_valid_source_identifier_is_rejected(string sourceId) =>
        Assert.Throws<DomainValidationException>(() =>
            Provenance.Create(sourceId, AsOf, Published, Retrieved));

    [Fact]
    public void An_over_long_locator_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            Provenance.Create(
                "fred",
                AsOf,
                Published,
                Retrieved,
                sourceRecordId: new string('x', Provenance.MaxSourceRecordIdLength + 1)));

    /// <summary>
    /// A value the platform produced has an origin like any other. A derived value whose
    /// producer cannot be identified is the kind that becomes impossible to explain later.
    /// </summary>
    [Fact]
    public void A_system_produced_value_names_its_producer()
    {
        var provenance = Provenance.FromSystem("internal.analysis-engine", AsOf, Retrieved);

        Assert.Equal(SourceId.Create("internal.analysis-engine"), provenance.SourceId);
        Assert.Equal(Retrieved, provenance.PublishedAtUtc);
        Assert.Equal(Retrieved, provenance.RetrievedAtUtc);
    }

    [Fact]
    public void Timestamps_must_be_utc()
    {
        Assert.Throws<DomainValidationException>(() =>
            Provenance.Create("fred", DateTime.Now, Published, Retrieved));

        Assert.Throws<DomainValidationException>(() =>
            Provenance.Create("fred", AsOf, DateTime.Now, Retrieved));

        Assert.Throws<DomainValidationException>(() =>
            Provenance.Create("fred", AsOf, Published, DateTime.Now));
    }

    [Fact]
    public void ToString_shows_the_origin_and_locator()
    {
        Assert.Equal("fred", Provenance.Create("fred", AsOf, Published, Retrieved).ToString());

        Assert.Equal(
            "sec-edgar/0000320193",
            Provenance.Create("sec-edgar", AsOf, Published, Retrieved, sourceRecordId: "0000320193")
                .ToString());
    }
}
