using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using Xunit;

namespace AI.Investment.Application.UnitTests.Opportunities;

/// <summary>
/// The first discoverer: what it produces, and everything it refuses to produce.
/// </summary>
/// <remarks>
/// <para>
/// The negative assertions carry the weight. A discoverer that invented a target price, stated a
/// probability it had not counted, or cited evidence by identifiers nothing holds would produce
/// opportunities that look exactly like real ones and are worthless as measurement - and the
/// validation run would either refuse them silently or, worse, admit them.
/// </para>
/// <para>
/// The point-in-time test is the one that could not be written any other way. A close published
/// after the instant asked about must not reach the screen, and the only way to see that from
/// outside is that the candidate's entry price is the earlier one.
/// </para>
/// </remarks>
public sealed class PriceRecoveryDiscovererTests
{
    private static readonly DateTime FirstSession = new(2026, 1, 2, 21, 0, 0, DateTimeKind.Utc);

    private static readonly decimal[] FallsAndRecovers =
        [100m, 110m, 120m, 115m, 100m, 95m, 130m, 100m, 90m, 100m];

    private static readonly decimal[] AlwaysRising =
        [100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m];

    private static readonly DiscoverySettings Settings = new()
    {
        Rule = new PriceRecoveryParameters(
            MinimumSessions: 5,
            DrawdownRatio: 0.10m,
            HorizonSessions: 2,
            MinimumTrials: 1),
    };

    private readonly SeededObservationStore _observations = new();

    /// <summary>Late enough to admit every seeded close.</summary>
    private static DateTime Now => FirstSession.AddDays(30);

    [Fact]
    public async Task A_candidate_cites_every_close_it_read_by_the_identifiers_the_store_holds()
    {
        Seed(FallsAndRecovers);

        var opportunity = Assert.Single(await Discover());

        Assert.Equal(FallsAndRecovers.Length, opportunity.Evidence.Count);

        var stored = _observations.All.Select(o => ClaimId.Create(o.Id.Value)).ToHashSet();

        // Every citation resolves. This is exactly what the validation run's prediction catalogue
        // checks before it will admit an opportunity, and a discoverer that minted fresh identifiers
        // would fail it there instead of here - months later, as a smaller sample.
        Assert.All(opportunity.Evidence, claim => Assert.Contains(claim, stored));
    }

    [Fact]
    public async Task Every_number_in_the_candidate_is_one_the_rule_measured()
    {
        Seed(FallsAndRecovers);

        var opportunity = Assert.Single(await Discover());
        var detail = EquityDetail.Parse(opportunity.Detail);

        var expected = PriceRecoveryRule.Evaluate(
            FallsAndRecovers
                .Select((close, i) => new ClosingPrice(FirstSession.AddDays(i), close))
                .ToList(),
            Settings.Rule).Candidate!;

        Assert.Equal(expected.EntryPrice, detail.EntryPrice);
        Assert.Equal(expected.TargetPrice, detail.TargetPrice);
        Assert.Equal(expected.SuccessProbability, detail.SuccessProbability);
        Assert.Equal(expected.HorizonDays, detail.HorizonDays);

        // One unit. Sizing is a decision about capital and belongs to whoever approves the action.
        Assert.Equal(1m, detail.Quantity);
        Assert.Equal("AAPL", detail.Instrument);
    }

    /// <summary>
    /// A close published after the instant asked about is not visible, and the entry price says so.
    /// </summary>
    [Fact]
    public async Task A_close_that_had_not_been_published_yet_is_not_read()
    {
        Seed(FallsAndRecovers);

        var subject = MarketObservations.Security("AAPL");
        var future = FirstSession.AddDays(FallsAndRecovers.Length);

        _observations.Seed([
            MarketObservations.Close(subject, future, 42m, publishedAtUtc: Now.AddDays(5)),
        ]);

        var opportunity = Assert.Single(await Discover());

        // 100, the last admissible close - not 42, which nobody had published yet.
        Assert.Equal(100m, EquityDetail.Parse(opportunity.Detail).EntryPrice);
        Assert.Equal(FallsAndRecovers.Length, opportunity.Evidence.Count);
    }

    /// <summary>
    /// A restatement resolves to the version that was current at the instant asked about, and the
    /// session it corrects is still cited once rather than twice.
    /// </summary>
    [Fact]
    public async Task A_restated_close_is_read_once_at_the_version_in_force()
    {
        Seed(FallsAndRecovers);

        var subject = MarketObservations.Security("AAPL");
        var session = FirstSession.AddDays(FallsAndRecovers.Length - 1);

        _observations.Seed([
            MarketObservations.Close(subject, session, 105m, publishedAtUtc: session.AddDays(1)),
        ]);

        var opportunity = Assert.Single(await Discover());

        Assert.Equal(105m, EquityDetail.Parse(opportunity.Detail).EntryPrice);
        Assert.Equal(FallsAndRecovers.Length, opportunity.Evidence.Count);
    }

    // ---- what it refuses -----------------------------------------------------------------------

    [Fact]
    public async Task A_series_that_has_not_fallen_produces_nothing()
    {
        Seed(AlwaysRising);

        var discoverer = Discoverer();

        Assert.Empty(await discoverer.DiscoverAsync(MarketObservations.Security("AAPL"), Now));
        Assert.Equal(PriceRecoveryRefusal.NoDrawdown, discoverer.LastRefusal);
    }

    [Fact]
    public async Task An_empty_store_produces_nothing_and_says_the_history_is_short()
    {
        var discoverer = Discoverer();

        Assert.Empty(await discoverer.DiscoverAsync(MarketObservations.Security("AAPL"), Now));
        Assert.Equal(PriceRecoveryRefusal.NotEnoughHistory, discoverer.LastRefusal);
    }

    [Fact]
    public async Task A_sweep_subject_produces_nothing()
    {
        Seed(FallsAndRecovers);

        Assert.Empty(await Discoverer().DiscoverAsync(IngestionSweep(), Now));
    }

    // ---- what it hands on ----------------------------------------------------------------------

    /// <summary>
    /// It produces drafts and nothing else: no economics, no risk, no confidence, no score.
    /// </summary>
    [Fact]
    public async Task It_produces_a_draft_and_nothing_further()
    {
        Seed(FallsAndRecovers);

        var opportunity = Assert.Single(await Discover());

        Assert.Equal(OpportunityStatus.Draft, opportunity.Status);
        Assert.Null(opportunity.Economics);
        Assert.Null(opportunity.Risk);
        Assert.Null(opportunity.Confidence);
        Assert.Null(opportunity.Score);
        Assert.Empty(opportunity.ProposalIds);
        Assert.Equal(EquityOpportunity.Type, opportunity.Type);
        Assert.Equal(PriceRecoveryRule.DiscovererId, opportunity.Source.DiscovererId.Value);
    }

    /// <summary>
    /// What the discoverer produces satisfies the type's own evidence requirement, so the draft can
    /// actually leave Draft. A discoverer whose output the requirement refuses is a discoverer that
    /// produces nothing measurable.
    /// </summary>
    [Fact]
    public async Task The_draft_satisfies_the_evidence_requirement_for_its_type()
    {
        Seed(FallsAndRecovers);

        var opportunity = Assert.Single(await Discover());

        Assert.Empty(new EquityEvidenceRequirement().MissingRequirements(opportunity));
    }

    /// <summary>
    /// The same store and the same instant produce the same candidate, down to the cited evidence.
    /// Replay is what makes a discovered opportunity reproducible evidence rather than an event.
    /// </summary>
    [Fact]
    public async Task Discovery_replays_to_the_same_candidate()
    {
        Seed(FallsAndRecovers);

        var first = Assert.Single(await Discover());
        var second = Assert.Single(await Discover());

        Assert.Equal(first.Detail.Json, second.Detail.Json);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(first.Description, second.Description);
        Assert.Equal(first.Evidence, second.Evidence);
    }

    /// <summary>The discoverer, the normaliser and the validation run read one attribute.</summary>
    [Fact]
    public void The_attribute_read_is_the_attribute_written()
    {
        Assert.Equal(MarketObservations.CloseAttribute, DiscoverySettings.Standard.PriceAttribute);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static IngestionSubject IngestionSweep() => IngestionSubject.Sweep("Security");

    private void Seed(IReadOnlyList<decimal> closes) =>
        _observations.Seed(MarketObservations.Series(
            MarketObservations.Security("AAPL"),
            FirstSession,
            closes));

    private PriceRecoveryDiscoverer Discoverer() =>
        new(new PriceSeriesReader(_observations), Settings);

    private Task<IReadOnlyList<Opportunity>> Discover() =>
        Discoverer().DiscoverAsync(MarketObservations.Security("AAPL"), Now);
}
