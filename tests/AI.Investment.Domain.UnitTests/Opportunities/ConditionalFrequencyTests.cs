using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities.Research;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The engine every research strategy states its probabilities through.
/// </summary>
/// <remarks>
/// <para>
/// One test here matters more than the rest: a case may inform a later decision only once it has
/// <em>resolved</em>. Using cases that had been decided but not yet resolved is the subtlest
/// look-ahead available, because the signal really was public at the time and only the outcome was
/// not. Nothing downstream can see it: the predictions are well formed, the dates are all in order,
/// and the score is simply better than it should be.
/// </para>
/// <para>
/// The rest pin that the buckets do not leak into each other, that the smoothing stops a small
/// bucket stating certainty, and that a strategy cannot speak before it has the fixed minimum of
/// history.
/// </para>
/// </remarks>
public sealed class ConditionalFrequencyTests
{
    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- the point-in-time rule -----------------------------------------------------------------

    /// <summary>
    /// A case decided earlier but resolving later does not inform the decision in between.
    /// </summary>
    /// <remarks>
    /// Twenty-one cases all resolve on day 400. A decision on day 300 therefore has nothing resolved
    /// to learn from, even though every one of those cases had already been decided by then. If the
    /// engine counted decisions rather than resolutions it would happily state a probability here.
    /// </remarks>
    [Fact]
    public void A_case_that_has_not_resolved_yet_informs_nothing()
    {
        var cases = new List<ResearchCase>();

        for (var i = 0; i < 21; i++)
        {
            cases.Add(new ResearchCase("a", Start.AddDays(i), Start.AddDays(400), 1, true));
        }

        cases.Add(new ResearchCase("b", Start.AddDays(300), Start.AddDays(500), 1, true));

        var scored = ConditionalFrequency.Score(cases, minimumPriorCases: 20);

        Assert.Empty(scored);
    }

    /// <summary>And once they have resolved, the very next decision can use them.</summary>
    [Fact]
    public void A_case_that_has_resolved_informs_the_next_decision()
    {
        var cases = new List<ResearchCase>();

        for (var i = 0; i < 20; i++)
        {
            cases.Add(new ResearchCase("a", Start.AddDays(i), Start.AddDays(100 + i), 1, true));
        }

        cases.Add(new ResearchCase("b", Start.AddDays(200), Start.AddDays(300), 1, false));

        var scored = ConditionalFrequency.Score(cases, minimumPriorCases: 20);

        var last = Assert.Single(scored);

        Assert.Equal(20, last.PriorCases);

        // Twenty prior successes, Laplace-smoothed: 21/22.
        Assert.Equal(21m / 22m, last.Probability);
        Assert.False(last.Occurred);
    }

    /// <summary>An outcome known at the moment of the claim is not a prediction, and is refused.</summary>
    [Fact]
    public void A_case_that_resolves_before_it_is_decided_is_refused()
    {
        var cases = new[]
        {
            new ResearchCase("a", Start.AddDays(10), Start.AddDays(5), 1, true),
        };

        var error = Assert.Throws<DomainRuleViolationException>(
            () => ConditionalFrequency.Score(cases));

        Assert.Equal("Research.ResolvedBeforeItWasDecided", error.Rule);
    }

    [Fact]
    public void A_case_that_resolves_at_the_instant_it_is_decided_is_refused() =>
        Assert.Throws<DomainRuleViolationException>(() => ConditionalFrequency.Score(new[]
        {
            new ResearchCase("a", Start, Start, 1, true),
        }));

    [Fact]
    public void A_local_timestamp_is_refused() =>
        Assert.Throws<DomainValidationException>(() => ConditionalFrequency.Score(new[]
        {
            new ResearchCase(
                "a",
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local),
                Start.AddDays(1),
                1,
                true),
        }));

    // ---- the buckets ----------------------------------------------------------------------------

    /// <summary>
    /// One bucket's history never reaches another bucket's decisions.
    /// </summary>
    /// <remarks>
    /// The whole point of conditioning. If the buckets pooled, every strategy in this stage would
    /// state the same base rate and the separation between them - the only place skill can live -
    /// would be invisible.
    /// </remarks>
    [Fact]
    public void Buckets_do_not_borrow_each_others_history()
    {
        var cases = new List<ResearchCase>();

        // Bucket 1: forty resolved cases, all true.
        for (var i = 0; i < 40; i++)
        {
            cases.Add(new ResearchCase("a", Start.AddDays(i), Start.AddDays(50 + i), 1, true));
        }

        // Bucket 2: forty resolved cases, all false.
        for (var i = 0; i < 40; i++)
        {
            cases.Add(new ResearchCase("b", Start.AddDays(i), Start.AddDays(50 + i), 2, false));
        }

        cases.Add(new ResearchCase("c", Start.AddDays(300), Start.AddDays(400), 1, true));
        cases.Add(new ResearchCase("d", Start.AddDays(300), Start.AddDays(400), 2, true));

        var scored = ConditionalFrequency.Score(cases, minimumPriorCases: 40);

        var inOne = Assert.Single(scored, p => p.SignalBucket == 1);
        var inTwo = Assert.Single(scored, p => p.SignalBucket == 2);

        Assert.Equal(41m / 42m, inOne.Probability);
        Assert.Equal(1m / 42m, inTwo.Probability);
    }

    /// <summary>Smoothing keeps a bucket that has never been wrong from stating certainty.</summary>
    [Fact]
    public void A_bucket_that_has_never_been_wrong_still_does_not_state_certainty()
    {
        var cases = new List<ResearchCase>();

        for (var i = 0; i < 25; i++)
        {
            cases.Add(new ResearchCase("a", Start.AddDays(i), Start.AddDays(50 + i), 1, true));
        }

        cases.Add(new ResearchCase("b", Start.AddDays(300), Start.AddDays(400), 1, true));

        var scored = ConditionalFrequency.Score(cases, minimumPriorCases: 20);
        var last = scored[^1];

        Assert.True(last.Probability < 1m);
        Assert.Equal(26m / 27m, last.Probability);
    }

    /// <summary>The strategy stays silent until the fixed minimum of history exists.</summary>
    [Fact]
    public void The_minimum_history_is_required_before_speaking()
    {
        var cases = new List<ResearchCase>();

        for (var i = 0; i < 30; i++)
        {
            cases.Add(new ResearchCase("a", Start.AddDays(i * 10), Start.AddDays((i * 10) + 1), 1, true));
        }

        var scored = ConditionalFrequency.Score(cases, minimumPriorCases: 20);

        // The first twenty resolutions land before the twenty-first decision, so twenty-one through
        // thirty speak and the first twenty do not.
        Assert.Equal(10, scored.Count);
        Assert.All(scored, p => Assert.True(p.PriorCases >= 20));
    }

    [Fact]
    public void A_minimum_of_zero_is_refused() =>
        Assert.Throws<DomainValidationException>(() =>
            ConditionalFrequency.Score(Array.Empty<ResearchCase>(), minimumPriorCases: 0));

    [Fact]
    public void No_cases_produce_no_predictions() =>
        Assert.Empty(ConditionalFrequency.Score(Array.Empty<ResearchCase>()));

    // ---- separation -----------------------------------------------------------------------------

    /// <summary>
    /// Separation is the gap between the buckets' realised rates, and it is where skill lives.
    /// </summary>
    [Fact]
    public void Separation_is_the_gap_between_the_buckets()
    {
        var predictions = new[]
        {
            new ResearchPrediction("a", Start, Start.AddDays(1), 1, 0.5m, true, 20),
            new ResearchPrediction("a", Start, Start.AddDays(1), 1, 0.5m, true, 20),
            new ResearchPrediction("a", Start, Start.AddDays(1), 2, 0.5m, false, 20),
            new ResearchPrediction("a", Start, Start.AddDays(1), 2, 0.5m, false, 20),
        };

        Assert.Equal(1m, ConditionalFrequency.BucketSeparation(predictions));
    }

    /// <summary>A strategy whose buckets behave identically has conditioned on nothing.</summary>
    [Fact]
    public void Identical_buckets_have_no_separation()
    {
        var predictions = new[]
        {
            new ResearchPrediction("a", Start, Start.AddDays(1), 1, 0.5m, true, 20),
            new ResearchPrediction("a", Start, Start.AddDays(1), 1, 0.5m, false, 20),
            new ResearchPrediction("a", Start, Start.AddDays(1), 2, 0.5m, true, 20),
            new ResearchPrediction("a", Start, Start.AddDays(1), 2, 0.5m, false, 20),
        };

        Assert.Equal(0m, ConditionalFrequency.BucketSeparation(predictions));
    }

    [Fact]
    public void One_bucket_alone_has_no_separation() =>
        Assert.Equal(0m, ConditionalFrequency.BucketSeparation(new[]
        {
            new ResearchPrediction("a", Start, Start.AddDays(1), 1, 0.5m, true, 20),
        }));
}
