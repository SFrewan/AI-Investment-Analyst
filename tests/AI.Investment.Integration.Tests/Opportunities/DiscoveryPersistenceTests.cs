using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence.Repositories;
using AI.Investment.Infrastructure.Validation;
using Xunit;

namespace AI.Investment.Integration.Tests.Opportunities;

/// <summary>
/// The whole prerequisite chain against a real PostgreSQL: stored closes, a discovered candidate,
/// and a validation run that can admit it.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the observation window exists for, and it could not be written before now. Phase
/// 7 measured an empty repository and Phase 8 refused promotion for want of evidence; both were
/// correct and neither could say whether the machinery would work when there was something in it.
/// What is established here is that it does: closes written as the normaliser writes them are read
/// back point-in-time, a discoverer turns them into a candidate that cites them by the identifiers
/// the store holds, and the validation run's own catalogue resolves every one of those citations and
/// dates the prediction from them.
/// </para>
/// <para>
/// The last of those is the link that would fail silently. An opportunity whose evidence does not
/// resolve is refused by the catalogue rather than rejected loudly, and the symptom months later is
/// a smaller sample that looks like a quiet week.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class DiscoveryPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime FirstSession = new(2026, 1, 2, 21, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = FirstSession.AddDays(60);

    /// <summary>
    /// A series whose drawdown begins at its last session.
    /// </summary>
    /// <remarks>
    /// The last close is the first one that is ten per cent below the running peak: the session
    /// before it is at the peak. That matters since the screen became episode-aware. It now raises a
    /// candidate for the session a drawdown opens on and refuses the sessions that continue it, so a
    /// fixture ending in the middle of a fall - which this one used to, at 90 then 100 - is an
    /// arrangement in which the correct answer is no candidate at all. It ends where an opportunity
    /// is actually raised.
    /// </remarks>
    private static readonly decimal[] FallsAndRecovers =
        [100m, 110m, 120m, 115m, 100m, 95m, 130m, 100m];

    private static readonly DiscoverySettings Settings = new()
    {
        Rule = new PriceRecoveryParameters(
            MinimumSessions: 5,
            DrawdownRatio: 0.10m,
            HorizonSessions: 2,
            MinimumTrials: 1),
    };

    /// <summary>
    /// A fresh subject each time, not a shared instance. An owned entity belongs to exactly one
    /// owner, and sharing one arrives as a not-null violation on <c>subject_kind</c> rather than as
    /// anything that names the cause.
    /// </summary>
    private static IngestionSubject Apple() => IngestionSubject.Create("Security", "AAPL");

    private readonly PostgresFixture _fixture;

    public DiscoveryPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Closes written the way the normaliser writes them read back as a point-in-time price series.
    /// </summary>
    [SkippableFact]
    public async Task Stored_closes_read_back_as_an_admissible_price_series()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync();

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var series = await new EfValidationHistory(context).GetPriceSeriesAsync(
            Apple(),
            DailyClosePriceNormalizer.CloseAttribute,
            FirstSession.AddDays(-1),
            Now,
            KnowledgeCutoff.At(Now));

        Assert.Equal(FallsAndRecovers.Length, series.Count);
        Assert.Equal(100m, series[0].Price);
        Assert.Equal(100m, series[^1].Price);

        // Published fifteen minutes after each session, which is the gap the whole point-in-time
        // machinery turns on.
        Assert.All(series, point => Assert.True(point.PublishedAtUtc > point.AtUtc));
    }

    /// <summary>
    /// The full chain: stored closes to a discovered candidate to a prediction the validation run
    /// can date from its own evidence.
    /// </summary>
    [SkippableFact]
    public async Task A_discovered_candidate_is_admissible_evidence_for_the_validation_run()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync();

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var discoverer = new PriceRecoveryDiscoverer(
            new PriceSeriesReader(new EfObservationStore(context)),
            Settings);

        var candidate = Assert.Single(await discoverer.DiscoverAsync(Apple(), Now));

        // Evaluated and ranked exactly as the work plan does it, then written through the seam.
        var economics = new EquityEconomicsCalculator().Calculate(candidate, Now);

        candidate.Evaluate(
            economics,
            OpportunityRisk.Create(
                "A reversible position in a listed equity.",
                ReversibilityClass.ReversibleWithCost,
                candidate.Evidence),
            Confidence.Create(0.5m),
            Now);

        candidate.Rank(Score(candidate), Now);
        candidate.RecordProposal(Guid.NewGuid(), Now);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfOpportunityRepository(context).AddAsync(candidate);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var predictions = await new EfPredictionCatalogue(verification).GetAsync(
            EvaluationWindow.Create(
                FirstSession.AddDays(-1), Now.AddDays(1), TimeSpan.FromDays(30), TimeSpan.FromDays(1)));

        var prediction = Assert.Single(predictions);

        Assert.Equal(candidate.OpportunityId.Value, prediction.PredictionId);
        Assert.Equal(PredictionDirection.Positive, prediction.Direction);

        // The one that matters: every cited claim resolved to a stored observation, so the run can
        // say when this prediction became knowable rather than refusing it.
        Assert.NotNull(prediction.EvidenceAvailableAtUtc);
        Assert.True(prediction.EvidenceAvailableAtUtc <= Now);
        Assert.Equal(FallsAndRecovers.Length, candidate.Evidence.Count);
    }

    /// <summary>
    /// An opportunity citing evidence nothing holds is refused rather than dated, which is what
    /// makes the assertion above meaningful.
    /// </summary>
    [SkippableFact]
    public async Task A_candidate_citing_evidence_nothing_holds_is_refused_by_the_catalogue()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync();

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var invented = Opportunity.Draft(
            EquityOpportunity.Type,
            Apple(),
            OpportunitySource.Create(PriceRecoveryRule.DiscovererId, Now),
            "A candidate resting on nothing",
            "Cites a claim identifier no observation answers to.",
            OpportunityDetail.Create(
                EquityOpportunity.Type,
                EquityDetail.ToJson("AAPL", 1m, 100m, 130m, "USD", 0.5m, 30)),
            Now,
            [ClaimId.New()]);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfOpportunityRepository(context).AddAsync(invented);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var predictions = await new EfPredictionCatalogue(verification).GetAsync(
            EvaluationWindow.Create(
                FirstSession.AddDays(-1), Now.AddDays(1), TimeSpan.FromDays(30), TimeSpan.FromDays(1)));

        Assert.Null(Assert.Single(predictions).EvidenceAvailableAtUtc);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static OpportunityScore Score(Opportunity candidate) =>
        OpportunityScore.From(MetricResult.Create(
            CalculationContext.Create(Apple(), KnowledgeCutoff.At(Now), Now),
            PriceRecoveryRule.Metric,
            MetricValue.Ratio(EquityDetail.Parse(candidate.Detail).SuccessProbability),
            "successes / trials, counted over the cited closes",
            candidate.Source.DiscovererId,
            PriceRecoveryRule.Version,
            FirstSession,
            [CalculationInput.Create(
                "close-0001",
                Claims.Fact(100m, Provenance.Create(
                    "operator-price-history", FirstSession, FirstSession.AddMinutes(15), Now)),
                UnitOfMeasure.Money)]));

    /// <summary>
    /// Writes the closes through the seam, shaped exactly as the market-data normaliser shapes them.
    /// </summary>
    private async Task SeedAsync()
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var observations = new List<Observation>(FallsAndRecovers.Length);

        for (var i = 0; i < FallsAndRecovers.Length; i++)
        {
            var session = FirstSession.AddDays(i);

            observations.Add(Observation.RecordFact(
                Apple(),
                DailyClosePriceNormalizer.CloseAttribute,
                ObservationValue.Number(FallsAndRecovers[i]),
                Provenance.Create(
                    "operator-price-history",
                    session,
                    session.AddMinutes(15),
                    session.AddHours(1),
                    "AAPL")));
        }

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(FirstSession)))
        {
            await context.Observations.AddRangeAsync(observations);
            await context.SaveChangesAsync();
        }
    }
}
