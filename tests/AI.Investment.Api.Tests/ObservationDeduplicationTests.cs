using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The de-duplication rule, and the blind spot that let five thousand duplicates through.
/// </summary>
/// <remarks>
/// Gate 12 read PASS for months while the store held 5,249 duplicate closing-price rows, because
/// the gate counted the financial-statement namespace and prices are not in it. Nothing was
/// broken; the gate was simply pointed at half the data. These facts pin the rule and the coverage
/// separately, because it was the coverage that failed.
/// </remarks>
public sealed class ObservationDeduplicationTests
{
    private const string Company = "Company";
    private const string Security = "Security";

    private static readonly DateTime AsOf = new(2023, 1, 17, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2023, 1, 18, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Two_rows_agreeing_on_all_five_parts_are_one_observation()
    {
        var first = ObservationDeduplication.Key(
            Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92");

        var second = ObservationDeduplication.Key(
            Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92");

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Each part of the identity is load-bearing: change any one and it is a different fact.
    /// </summary>
    [Fact]
    public void Changing_any_one_part_makes_it_a_different_observation()
    {
        var baseline = ObservationDeduplication.Key(
            Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92");

        var variants = new[]
        {
            ObservationDeduplication.Key(Company, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92"),
            ObservationDeduplication.Key(Security, "MSFT.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92"),
            ObservationDeduplication.Key(Security, "AAPL.US", ObservationDeduplication.FinancialPrefix + "revenue.annual", AsOf, Published, "185.92"),
            ObservationDeduplication.Key(Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf.AddHours(1), Published, "185.92"),
            ObservationDeduplication.Key(Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published.AddHours(1), "185.92"),
            ObservationDeduplication.Key(Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.93"),
        };

        Assert.All(variants, v => Assert.NotEqual(baseline, v));

        // And all six are distinct from each other: no two parts collide into one key.
        Assert.Equal(variants.Length, variants.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A re-published price is a different observation; a re-fetched one is the same.
    /// </summary>
    /// <remarks>
    /// This is the distinction the price duplicates turned on. A genuine restatement carries a
    /// later publication instant and must be kept. A trading date fetched twice re-normalises to
    /// the same session close and the same publication instant, so the second copy carries no new
    /// information - and the store had 5,249 of those.
    /// </remarks>
    [Fact]
    public void A_republication_is_kept_and_a_refetch_is_a_duplicate()
    {
        var original = ObservationDeduplication.Key(
            Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92");

        var refetched = ObservationDeduplication.Key(
            Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published, "185.92");

        var republished = ObservationDeduplication.Key(
            Security, "AAPL.US", ObservationDeduplication.PriceAttribute, AsOf, Published.AddDays(1), "185.92");

        Assert.Equal(original, refetched);
        Assert.NotEqual(original, republished);
    }

    /// <summary>
    /// The blind spot, named. Prices and splits are governed now; they were not before.
    /// </summary>
    [Fact]
    public void The_gate_governs_the_price_namespace_as_well_as_the_financial_one()
    {
        Assert.True(ObservationDeduplication.IsGoverned(
            ObservationDeduplication.FinancialPrefix + "revenue.annual"));
        Assert.True(ObservationDeduplication.IsGoverned(
            ObservationDeduplication.FinancialPrefix + "assets.quarterly"));

        // The two that were invisible to the gate.
        Assert.True(ObservationDeduplication.IsGoverned(ObservationDeduplication.PriceAttribute));
        Assert.True(ObservationDeduplication.IsGoverned(ObservationDeduplication.SplitAttribute));

        Assert.Equal(
            "financial",
            ObservationDeduplication.Namespace(ObservationDeduplication.FinancialPrefix + "revenue.annual"));

        // The prefix is the real one, taken from the type that owns it. Spelled by hand it was
        // one letter short, matched nothing, and made the gate pass over an empty set.
        Assert.Equal("financials.", ObservationDeduplication.FinancialPrefix);
        Assert.Equal("security.close", ObservationDeduplication.PriceAttribute);
        Assert.Equal("security.split-ratio", ObservationDeduplication.SplitAttribute);
        Assert.Equal("market-data", ObservationDeduplication.Namespace(ObservationDeduplication.PriceAttribute));
        Assert.Equal("market-data", ObservationDeduplication.Namespace(ObservationDeduplication.SplitAttribute));

        // A namespace the gate has never judged is not silently swept in: failing a gate on data
        // whose repetition rules nobody has stated would teach an operator to ignore the gate.
        Assert.False(ObservationDeduplication.IsGoverned("company.name"));
        Assert.Equal("other", ObservationDeduplication.Namespace("company.name"));
    }

    /// <summary>Zero tolerance, and the same zero for both namespaces.</summary>
    [Fact]
    public void The_only_passing_answer_is_zero_excess_rows()
    {
        Assert.True(ObservationDeduplication.Passes(649597, 649597));
        Assert.Equal(0, ObservationDeduplication.Excess(649597, 649597));

        // The price namespace as it was found: 30,330 rows over 25,081 identities.
        Assert.False(ObservationDeduplication.Passes(30330, 25081));
        Assert.Equal(5249, ObservationDeduplication.Excess(30330, 25081));

        // One duplicate is a failure. There is no tolerance to argue about.
        Assert.False(ObservationDeduplication.Passes(2, 1));
        Assert.Equal(1, ObservationDeduplication.Excess(2, 1));

        // Fewer rows than identities cannot happen, and is reported as zero rather than negative.
        Assert.Equal(0, ObservationDeduplication.Excess(1, 2));
    }
}
