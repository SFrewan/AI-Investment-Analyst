using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Analytics;

public sealed class KnowledgeCutoffTests
{
    private static readonly DateTime Cutoff = new(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Information_published_before_the_cutoff_was_knowable() =>
        Assert.True(KnowledgeCutoff.At(Cutoff).Admits(Cutoff.AddDays(-1)));

    [Fact]
    public void Information_published_exactly_at_the_cutoff_was_knowable() =>
        Assert.True(KnowledgeCutoff.At(Cutoff).Admits(Cutoff));

    [Fact]
    public void Information_published_after_the_cutoff_was_not() =>
        Assert.False(KnowledgeCutoff.At(Cutoff).Admits(Cutoff.AddTicks(1)));

    /// <summary>
    /// Publication, not retrieval, decides. A filing that was public before the cutoff was knowable
    /// whether or not this platform had fetched it - otherwise replaying a period after backfilling
    /// a source would give a different answer for reasons unrelated to the world.
    /// </summary>
    [Fact]
    public void Late_retrieval_of_early_information_is_still_admitted()
    {
        var provenance = Provenance.Create(
            "sec-edgar",
            asOfUtc: Cutoff.AddDays(-40),
            publishedAtUtc: Cutoff.AddDays(-10),
            retrievedAtUtc: Cutoff.AddDays(30));

        Assert.True(KnowledgeCutoff.At(Cutoff).Admits(provenance));
    }

    [Fact]
    public void Evidence_published_after_the_cutoff_is_refused()
    {
        var provenance = Provenance.Create(
            "sec-edgar",
            asOfUtc: Cutoff.AddDays(-40),
            publishedAtUtc: Cutoff.AddDays(1),
            retrievedAtUtc: Cutoff.AddDays(2));

        Assert.False(KnowledgeCutoff.At(Cutoff).Admits(provenance));
    }

    [Fact]
    public void A_cutoff_must_be_utc() =>
        Assert.Throws<DomainValidationException>(
            () => KnowledgeCutoff.At(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Local)));

    [Fact]
    public void A_publication_date_must_be_utc() =>
        Assert.Throws<DomainValidationException>(
            () => KnowledgeCutoff.At(Cutoff).Admits(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Unspecified)));
}
