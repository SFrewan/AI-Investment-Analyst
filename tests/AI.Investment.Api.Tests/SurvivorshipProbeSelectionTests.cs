using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The selection rule and the arithmetic on its result, pinned before any filing is read.
/// </summary>
/// <remarks>
/// These run in the ordinary suite and touch nothing. Their point is that the rule which decides
/// <em>which</em> ten members get read, and the bar which decides whether the answer justifies
/// spending two hundred more calls, are both fixed in a diff before the first request goes out.
/// A probe that can choose its sample or move its bar after seeing results is not evidence.
/// </remarks>
public sealed class SurvivorshipProbeSelectionTests
{
    private static SurvivorshipProbeSelection.Candidate Member(
        string cik, string cohort, string status) =>
        new(cik, "Member " + cik, cohort, status, "acc-" + cik, "10-K", "2023-01-01", "doc.htm");

    [Fact]
    public void The_sample_rotates_across_death_years_and_identity_categories()
    {
        var candidates = new List<SurvivorshipProbeSelection.Candidate>();

        // Four death years, three categories, ten members in each cell: 120 candidates, of which
        // the rule must pick ten that span the range rather than ten from one corner.
        foreach (var year in new[] { "2021-08-31", "2022-08-31", "2023-08-31", "2024-08-31" })
        {
            foreach (var status in new[] { "ambiguous", "eodhd-only-provisional", "unmatched" })
            {
                for (var i = 0; i < 10; i++)
                {
                    candidates.Add(Member($"{year[..4]}{status[..3]}{i:D2}", year, status));
                }
            }
        }

        var chosen = SurvivorshipProbeSelection.Choose(candidates);

        Assert.Equal(SurvivorshipProbeSelection.Size, chosen.Count);

        // Ten picks over twelve strata: every pick is from a different stratum, so no year and no
        // category is asked about twice before all of them have been asked once.
        Assert.Equal(chosen.Count, chosen.Select(c => c.Stratum).Distinct(StringComparer.Ordinal).Count());

        // All four death years are represented.
        Assert.Equal(
            4,
            chosen.Select(c => c.LastCohort[..4]).Distinct(StringComparer.Ordinal).Count());

        // And all three identity categories.
        Assert.Equal(3, chosen.Select(c => c.Status).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_rule_is_deterministic_and_independent_of_the_order_it_is_given()
    {
        var candidates = new List<SurvivorshipProbeSelection.Candidate>
        {
            Member("0000000005", "2022-08-31", "unmatched"),
            Member("0000000001", "2021-08-31", "ambiguous"),
            Member("0000000004", "2022-08-31", "unmatched"),
            Member("0000000003", "2021-08-31", "ambiguous"),
            Member("0000000002", "2023-08-31", "eodhd-only-provisional"),
        };

        var forwards = SurvivorshipProbeSelection.Choose(candidates);
        var backwards = SurvivorshipProbeSelection.Choose(Enumerable.Reverse(candidates));

        Assert.Equal(
            forwards.Select(c => c.Cik).ToList(),
            backwards.Select(c => c.Cik).ToList());

        // Fewer candidates than asked for is not an error: it takes what exists.
        Assert.Equal(candidates.Count, forwards.Count);

        // Within a stratum the lower CIK is read first, so the choice is reproducible rather than
        // whatever order the database happened to return.
        var ambiguous = forwards.Where(c => c.Status == "ambiguous").Select(c => c.Cik).ToList();

        Assert.Equal(["0000000001", "0000000003"], ambiguous);
    }

    /// <summary>
    /// A request that never reached SEC says nothing about whether the filing tagged its cover page.
    /// </summary>
    [Fact]
    public void Transport_failures_never_move_the_recovery_rate()
    {
        // Six readable filings, four recovered. The rate is four in six however many requests
        // failed to connect, because those requests measured the network and not the filings.
        Assert.Equal(4m / 6m, SurvivorshipProbeSelection.Rate(4, 2));

        // Nothing readable is a rate of zero rather than a division by zero, and the interval
        // below says the honest thing about it.
        Assert.Equal(0m, SurvivorshipProbeSelection.Rate(0, 0));

        var (low, high) = SurvivorshipProbeSelection.Interval(0, 0);

        Assert.Equal(0d, low);
        Assert.Equal(1d, high);
    }

    /// <summary>
    /// The interval is what stops ten filings being read as a measurement of a hundred and ten.
    /// </summary>
    [Fact]
    public void The_interval_shows_how_little_a_small_sample_settles()
    {
        // The evidence as it stood after two probes: two of four readable.
        var (low, high) = SurvivorshipProbeSelection.Interval(2, 4);

        Assert.InRange(low, 0.10d, 0.25d);
        Assert.InRange(high, 0.75d, 0.90d);

        // Half the sample recovered, and the interval still spans nearly everything - which is
        // exactly why a rate alone would have been misleading.
        Assert.True(high - low > 0.5d);

        // Ten filings narrow it, and still do not pin it.
        var (tenLow, tenHigh) = SurvivorshipProbeSelection.Interval(5, 10);

        Assert.True(tenHigh - tenLow < high - low);
        Assert.True(tenHigh - tenLow > 0.4d);

        // Bounds stay inside [0,1] at the extremes, which the textbook normal interval does not.
        Assert.InRange(SurvivorshipProbeSelection.Interval(0, 10).Low, 0d, 0.01d);
        Assert.InRange(SurvivorshipProbeSelection.Interval(10, 10).High, 0.99d, 1d);
    }

    /// <summary>
    /// The bar for committing to the full recovery, fixed before the number is known.
    /// </summary>
    [Fact]
    public void The_bar_for_a_full_recovery_is_set_now_and_cannot_move_later()
    {
        // Ten out of ten clears it.
        Assert.True(SurvivorshipProbeSelection.JustifiesFullRecovery(10, 10));

        // A bare majority does not: the lower bound has to clear a half, not the point estimate,
        // because it is the lower bound that says what the evidence supports.
        Assert.False(SurvivorshipProbeSelection.JustifiesFullRecovery(6, 10));
        Assert.False(SurvivorshipProbeSelection.JustifiesFullRecovery(5, 10));

        // Nine of ten does.
        Assert.True(SurvivorshipProbeSelection.JustifiesFullRecovery(9, 10));

        // And too few readable filings never clears it, however well they went - a rate of one on
        // a sample of three is not a finding.
        Assert.False(SurvivorshipProbeSelection.JustifiesFullRecovery(3, 3));
    }
}
