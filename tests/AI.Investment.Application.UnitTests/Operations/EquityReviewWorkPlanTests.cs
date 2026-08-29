using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Operations;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.UnitTests.Autonomy;
using AI.Investment.Application.UnitTests.Opportunities;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>
/// The first cycle work plan: what a pass produces, and what it refuses to produce.
/// </summary>
/// <remarks>
/// <para>
/// The plan is driven stage by stage here rather than through the runner. The runner's own
/// behaviour - leases, budgets, the gate, escalation - is established by the Phase 6 tests and is
/// not what changed; what changed is that there is now something for it to run, and the questions
/// worth asking are about that: does a pass produce an evidence-backed candidate, does it produce
/// nothing when the evidence does not support one, and does it write nothing at all until the
/// gateway says so.
/// </para>
/// <para>
/// The last of those is the one that would be expensive to get wrong. Every stage before the gate
/// runs outside an authorisation window, and the runner saves the cycle's own progress between
/// stages - so a plan that touched the repository early would either write a candidate the policy
/// engine went on to refuse, or make the cycle's next progress save fail as an unauthorised write.
/// </para>
/// </remarks>
public sealed class EquityReviewWorkPlanTests
{
    private static readonly DateTime FirstSession = new(2026, 1, 2, 21, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = FirstSession.AddDays(30);

    private static readonly decimal[] FallsAndRecovers =
        [100m, 110m, 120m, 115m, 100m, 95m, 130m, 100m, 90m, 100m];

    private static readonly decimal[] AlwaysRising =
        [100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m];

    /// <summary>
    /// Blank price source: these tests seed the observation store directly, which is exactly the
    /// "data arrives some other way" arrangement the setting describes. The acquisition path has
    /// its own tests below.
    /// </summary>
    private static readonly DiscoverySettings Settings = new()
    {
        PriceSourceId = "",
        Rule = new PriceRecoveryParameters(
            MinimumSessions: 5,
            DrawdownRatio: 0.10m,
            HorizonSessions: 2,
            MinimumTrials: 1),
    };

    private readonly SeededObservationStore _observations = new();
    private readonly InMemoryCycleStore _cycles = new();
    private readonly InMemoryWatchStore _watches = new();
    private readonly RecordingOpportunityRepository _repository = new();
    private readonly NoOpUnitOfWork _unitOfWork = new();
    private readonly FakeClock _clock = new(Now);
    private readonly RecordingAcquisition _acquisition = new();

    // ---- a pass that finds something -----------------------------------------------------------

    /// <summary>
    /// The whole point of the plan: a pass over a series that has fallen produces a ranked,
    /// evidence-backed candidate and proposes recording it.
    /// </summary>
    [Fact]
    public async Task A_pass_over_a_fallen_series_proposes_recording_a_ranked_candidate()
    {
        Seed(FallsAndRecovers);

        var plan = Plan();
        var cycle = StartCycle();

        var proposal = await DriveAsync(plan, cycle);

        Assert.NotNull(proposal);
        Assert.Equal(Capability.OpportunityManagement, proposal!.Capability);
        Assert.Equal(EquityReviewWorkPlan.RecordCandidate, proposal.ActionType);
        Assert.Equal("AAPL", proposal.Target.Identifier);

        // No money, no venue, no order. The observation window is measurement.
        Assert.Equal(0m, proposal.Economics.EstimatedExposure.Amount);
        Assert.Equal(0m, proposal.Economics.EstimatedCost.Amount);
        Assert.NotEqual(Capability.FinancialExecution, proposal.Capability);
        Assert.NotEqual(Capability.SimulatedExecution, proposal.Capability);

        // The proposal carries the same evidence the candidate cites, so the audit record of the
        // decision names what the decision was made on.
        Assert.Equal(FallsAndRecovers.Length, proposal.Evidence.Count);
        Assert.NotNull(proposal.Confidence);
    }

    /// <summary>Nothing reaches the repository until the gateway invokes the effect.</summary>
    [Fact]
    public async Task Nothing_is_written_until_the_gateway_authorises_it()
    {
        Seed(FallsAndRecovers);

        var plan = Plan();
        var cycle = StartCycle();

        var proposal = await DriveAsync(plan, cycle);

        Assert.NotNull(proposal);
        Assert.Empty(_repository.All);
        Assert.Equal(0, _unitOfWork.SaveCount);

        var summary = await plan.ExecuteAsync(proposal!, Resolution());

        var stored = Assert.Single(_repository.All);

        Assert.Equal(OpportunityStatus.Proposed, stored.Status);
        Assert.Contains(proposal!.ProposalId, stored.ProposalIds);
        Assert.Equal(1, _unitOfWork.SaveCount);
        Assert.Contains("Recorded opportunity", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stored candidate is what the validation run will read: a positive prediction, dated when
    /// it was discovered, citing stored observations.
    /// </summary>
    [Fact]
    public async Task The_recorded_candidate_is_what_the_validation_run_measures()
    {
        Seed(FallsAndRecovers);

        var plan = Plan();
        var cycle = StartCycle();

        var proposal = await DriveAsync(plan, cycle);

        await plan.ExecuteAsync(proposal!, Resolution());

        var stored = Assert.Single(_repository.All);

        Assert.NotNull(stored.Economics);
        Assert.NotNull(stored.Risk);
        Assert.NotNull(stored.Confidence);
        Assert.NotNull(stored.Score);
        Assert.Equal(PriceRecoveryRule.Metric, stored.Score!.Metric);
        Assert.Equal(PriceRecoveryRule.Version, stored.Score.Version);

        // The score is the measured base rate, not a restatement of the price.
        Assert.Equal(
            EquityDetail.Parse(stored.Detail).SuccessProbability,
            stored.Score.Value);

        Assert.Equal(FallsAndRecovers.Length, stored.Evidence.Count);
        Assert.Equal(ReversibilityClass.ReversibleWithCost, stored.Risk!.Reversibility);
    }

    /// <summary>
    /// Recording the same cycle's candidate twice is one action, so a re-run of a crashed cycle
    /// cannot double it. The key is checked by the seam; what is asserted here is that the plan
    /// gives the seam something to check.
    /// </summary>
    [Fact]
    public async Task The_proposal_is_keyed_on_the_cycle()
    {
        Seed(FallsAndRecovers);

        var cycle = StartCycle();

        var first = await DriveAsync(Plan(), cycle);
        var second = await DriveAsync(Plan(), cycle);

        Assert.Equal(first!.IdempotencyKey, second!.IdempotencyKey);
        Assert.Contains(cycle.CycleId.ToString(), first.IdempotencyKey, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(first.ProposalId, second.ProposalId);
    }

    // ---- passes that find nothing --------------------------------------------------------------

    /// <summary>
    /// Most passes of a monitoring loop should find nothing, and finding nothing is not an
    /// escalation. What matters is that the reason is recorded rather than silence.
    /// </summary>
    [Fact]
    public async Task A_series_that_has_not_fallen_proposes_nothing_and_says_why()
    {
        Seed(AlwaysRising);

        var plan = Plan();

        var proposal = await DriveAsync(plan, StartCycle());

        Assert.Null(proposal);
        Assert.Empty(_repository.All);
        Assert.Contains("below the highest close", plan.Obstacle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_store_proposes_nothing_and_says_the_history_is_short()
    {
        var plan = Plan();

        Assert.Null(await DriveAsync(plan, StartCycle()));
        Assert.Contains("shorter than the history", plan.Obstacle, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cycle with no watch behind it is a configuration mistake, and it is reported as a failed
    /// step rather than as an ordinary quiet pass.
    /// </summary>
    [Fact]
    public async Task A_cycle_with_no_watch_reports_a_failed_step()
    {
        Seed(FallsAndRecovers);

        var cycle = OperatingCycle.Start(
            CorrelationId.Create("cycle-no-watch"),
            Capability.OpportunityManagement,
            EquityReviewWorkPlan.Template,
            "trigger-no-watch",
            Budget(),
            Currency.Usd,
            Now);

        await _cycles.TryAddAsync(cycle);

        var plan = Plan();

        var result = await plan.RunStageAsync(Context(cycle, CycleStage.Discover));

        Assert.True(result.ProviderFailed);
        Assert.Contains("not started by a watch", plan.Obstacle, StringComparison.Ordinal);
        Assert.Null(await DriveAsync(plan, cycle, fromDiscover: false));
    }

    /// <summary>
    /// The gateway authorising an effect the plan is not holding is a contradiction, and the plan
    /// fails rather than writing something else.
    /// </summary>
    [Fact]
    public async Task Executing_without_a_candidate_fails_rather_than_inventing_one()
    {
        Seed(AlwaysRising);

        var plan = Plan();
        var cycle = StartCycle();

        await DriveAsync(plan, cycle);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plan.ExecuteAsync(SomeProposal(), Resolution()));

        Assert.Empty(_repository.All);
    }

    // ---- determinism ---------------------------------------------------------------------------

    /// <summary>
    /// Two passes over the same evidence produce the same candidate. Every read is pinned to the
    /// instant the pass began, so a clock that moves between stages cannot change what was read.
    /// </summary>
    [Fact]
    public async Task Two_passes_over_the_same_evidence_produce_the_same_candidate()
    {
        Seed(FallsAndRecovers);

        var firstPlan = Plan();
        var secondPlan = Plan();

        var first = await DriveAsync(firstPlan, StartCycle("trigger-one"));

        _clock.Advance(TimeSpan.FromHours(6));

        var second = await DriveAsync(secondPlan, StartCycle("trigger-two"));

        await firstPlan.ExecuteAsync(first!, Resolution());
        await secondPlan.ExecuteAsync(second!, Resolution());

        Assert.Equal(2, _repository.All.Count);
        Assert.Equal(_repository.All[0].Detail.Json, _repository.All[1].Detail.Json);
        Assert.Equal(_repository.All[0].Evidence, _repository.All[1].Evidence);
        Assert.Equal(_repository.All[0].Score!.Value, _repository.All[1].Score!.Value);
    }

    /// <summary>The plan answers for one template, and the runner matches on the name.</summary>
    [Fact]
    public void The_plan_names_the_template_it_answers_for()
    {
        Assert.Equal("equity-price-review", Plan().TemplateName);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private void Seed(IReadOnlyList<decimal> closes) =>
        _observations.Seed(MarketObservations.Series(
            MarketObservations.Security("AAPL"),
            FirstSession,
            closes));

    // ---- acquisition: the cycle fetches before it screens --------------------------------------

    /// <summary>
    /// The gap this closes. Before this, Collect read storage that nothing ever filled.
    /// </summary>
    [Fact]
    public async Task A_pass_acquires_from_the_configured_source_before_it_screens()
    {
        Seed(FallsAndRecovers);

        var (_, result) = await RunCollectAsync(StartCycle(), Acquiring());

        var request = Assert.Single(_acquisition.Requests);

        Assert.Equal("eodhd-eod", request.SourceId.Value);
        Assert.Equal(DataCategory.MarketPrices, request.Category);
        Assert.Equal("Security", request.Subject.Kind);
        Assert.Equal("AAPL", request.Subject.Identifier);

        // No window: the screen counts a base rate over months, and a narrowed request would
        // produce a series too short to evidence anything.
        Assert.Null(request.Window);

        // Counted against the cycle's provider-call budget, and no model spend.
        Assert.Equal(1, result.ProviderCalls);
        Assert.Equal(0m, result.ModelSpend.Amount);
        Assert.False(result.ProviderFailed);
    }

    [Fact]
    public async Task A_successful_acquisition_leaves_the_pass_able_to_propose_a_candidate()
    {
        Seed(FallsAndRecovers);

        var proposal = await DriveAsync(Plan(Acquiring()), StartCycle());

        Assert.NotNull(proposal);
        Assert.Single(_acquisition.Requests);
    }

    /// <summary>
    /// A refusal is the gateway's answer, not an exception. The stage fails, nothing is screened,
    /// and the obstacle names the source - because "the licence does not permit this" and "the
    /// series simply has not fallen" are different facts.
    /// </summary>
    [Fact]
    public async Task A_refused_acquisition_fails_the_stage_and_screens_nothing()
    {
        Seed(FallsAndRecovers);

        _acquisition.Refuse("data-plane.source-active@1", "Source 'eodhd-eod' is not active.");

        var (plan, result) = await RunCollectAsync(StartCycle(), Acquiring());

        Assert.True(result.ProviderFailed);
        Assert.Equal(1, result.ProviderCalls);

        // The obstacle names the source and relays the run's own reason, so a licensing refusal is
        // not reported as "the series has not fallen".
        Assert.Contains("eodhd-eod", plan.Obstacle, StringComparison.Ordinal);
        Assert.Contains("not active", plan.Obstacle, StringComparison.Ordinal);
        Assert.Empty(_repository.All);
    }

    [Fact]
    public async Task A_failed_acquisition_fails_the_stage_and_proposes_nothing()
    {
        Seed(FallsAndRecovers);

        _acquisition.Fail("the provider returned 503 three times.");

        var proposal = await DriveAsync(Plan(Acquiring()), StartCycle());

        Assert.Null(proposal);
        Assert.Empty(_repository.All);
    }

    /// <summary>
    /// A fetch that succeeded but delivered nothing usable is not a failure - it is a pass with no
    /// evidence, which is the existing empty-observation behaviour and must stay distinguishable
    /// from a refusal.
    /// </summary>
    [Fact]
    public async Task A_successful_acquisition_that_stored_nothing_proposes_nothing_without_failing()
    {
        // Nothing seeded: the fetch succeeded and the store is still empty.
        var (_, result) = await RunCollectAsync(StartCycle(), Acquiring());

        Assert.False(result.ProviderFailed);
        Assert.Equal(1, result.ProviderCalls);
    }

    [Fact]
    public async Task A_pass_with_no_stored_observations_proposes_nothing()
    {
        var proposal = await DriveAsync(Plan(Acquiring()), StartCycle());

        Assert.Null(proposal);
        Assert.Single(_acquisition.Requests);
        Assert.Empty(_repository.All);
    }

    /// <summary>Blank source: screen what is stored, call nobody. The pre-existing arrangement.</summary>
    [Fact]
    public async Task No_configured_price_source_screens_what_is_stored_and_calls_no_provider()
    {
        Seed(FallsAndRecovers);

        var (_, result) = await RunCollectAsync(StartCycle(), Settings);

        Assert.Empty(_acquisition.Requests);
        Assert.Equal(0, result.ProviderCalls);
        Assert.False(result.ProviderFailed);

        var proposal = await DriveAsync(Plan(), StartCycle("trigger-second"));

        Assert.NotNull(proposal);
        Assert.Empty(_acquisition.Requests);
    }

    /// <summary>
    /// The plan reaches a provider only through IDataAcquisition, which is the gated path. No HTTP
    /// client, no connector, no vendor type reaches this class.
    /// </summary>
    [Fact]
    public void The_plan_reaches_a_provider_only_through_the_gated_acquisition_path()
    {
        var dependencies = typeof(EquityReviewWorkPlan)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.Contains("IDataAcquisition", dependencies, StringComparer.Ordinal);

        Assert.DoesNotContain(dependencies, name =>
            name.Contains("Http", StringComparison.Ordinal) ||
            name.Contains("Eodhd", StringComparison.Ordinal) ||
            name.Contains("IDataProvider", StringComparison.Ordinal));
    }

    private EquityReviewWorkPlan Plan(DiscoverySettings? settings = null)
    {
        var effective = settings ?? Settings;

        return new EquityReviewWorkPlan(
            _cycles,
            _watches,
            [new PriceRecoveryDiscoverer(new PriceSeriesReader(_observations), effective)],
            [new EquityEvidenceRequirement()],
            [new EquityEconomicsCalculator()],
            new PriceSeriesReader(_observations),
            _acquisition,
            effective,
            _repository,
            _unitOfWork,
            _clock);
    }

    private static DiscoverySettings Acquiring() => Settings with { PriceSourceId = "eodhd-eod" };

    private OperatingCycle StartCycle(string triggerKey = "trigger-aapl")
    {
        var watch = Watch.Create(
            "AAPL price review",
            WatchTarget.Create("Security", "AAPL"),
            TriggerType.Schedule,
            TriggerCondition.Every(TimeSpan.FromHours(6)),
            TimeSpan.FromHours(1),
            Capability.OpportunityManagement,
            EquityReviewWorkPlan.Template,
            Now);

        _watches.Seed(watch);

        var cycle = OperatingCycle.Start(
            CorrelationId.Create(triggerKey),
            Capability.OpportunityManagement,
            EquityReviewWorkPlan.Template,
            triggerKey,
            Budget(),
            Currency.Usd,
            Now,
            watch.WatchId);

        _cycles.TryAddAsync(cycle).GetAwaiter().GetResult();

        return cycle;
    }

    /// <summary>Runs Discover then Collect, returning the plan and what Collect reported.</summary>
    private async Task<(EquityReviewWorkPlan Plan, CycleStageResult Result)> RunCollectAsync(
        OperatingCycle cycle,
        DiscoverySettings settings)
    {
        var plan = Plan(settings);

        await plan.RunStageAsync(Context(cycle, CycleStage.Discover));

        var result = await plan.RunStageAsync(Context(cycle, CycleStage.Collect));

        return (plan, result);
    }

    /// <summary>
    /// An acquisition that records what it was asked for and answers however the test needs.
    /// </summary>
    /// <remarks>
    /// It never reaches a network, which is the point: this suite proves the plan asks correctly
    /// and reacts correctly, and the gateway's own refusals are established by the ingestion tests.
    /// </remarks>
    internal sealed class RecordingAcquisition : IDataAcquisition
    {
        private readonly List<IngestionRequest> _requests = [];

        private string? _refusalRule;
        private string? _reason;
        private bool _failed;

        public IReadOnlyList<IngestionRequest> Requests => _requests;

        public void Refuse(string ruleId, string reason)
        {
            _refusalRule = ruleId;
            _reason = reason;
        }

        public void Fail(string reason)
        {
            _failed = true;
            _reason = reason;
        }

        public Task<AcquisitionResult> AcquireAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            if (_refusalRule is not null)
            {
                return Task.FromResult(new AcquisitionResult(
                    IngestionRun.Refuse(request, _refusalRule, _reason!, request.RequestedAtUtc),
                    Normalization: null));
            }

            var run = IngestionRun.Start(request, request.RequestedAtUtc);

            if (_failed)
            {
                run.MarkFailed(_reason!, request.RequestedAtUtc);
            }
            else
            {
                run.MarkSucceeded(request.RequestedAtUtc);
            }

            return Task.FromResult(new AcquisitionResult(run, Normalization: null));
        }
    }

    private static CycleBudget Budget() =>
        CycleBudget.Create(TimeSpan.FromMinutes(15), Money.Create(1m, Currency.Usd), 50, 1);

    private static CycleStageContext Context(OperatingCycle cycle, CycleStage stage) =>
        new(cycle.CycleId, cycle.Capability, cycle.TemplateName, stage, Now);

    /// <summary>Runs the stages the runner would run, in order, up to the gate.</summary>
    private static async Task<ActionProposal?> DriveAsync(
        EquityReviewWorkPlan plan,
        OperatingCycle cycle,
        bool fromDiscover = true)
    {
        ActionProposal? proposal = null;

        foreach (var stage in CycleStages.Ordered)
        {
            if (stage == CycleStage.PolicyGate)
            {
                // The runner takes it from here. Stages after the gate do no work in this plan.
                break;
            }

            if (!fromDiscover && stage == CycleStage.Discover)
            {
                continue;
            }

            var result = await plan.RunStageAsync(Context(cycle, stage));

            if (stage == CycleStage.ProposeAction)
            {
                proposal = result.Proposal;
            }
        }

        return proposal;
    }

    /// <summary>
    /// What the gateway hands the effect. Resolved against no grants at all, which is the honest
    /// state of this platform: nothing is granted, and the plan's effect does not read it anyway.
    /// </summary>
    private static AutonomyResolution Resolution() =>
        AutonomyResolver.Resolve(
            AutonomyRequest.Create(
                Capability.OpportunityManagement,
                EquityReviewWorkPlan.RecordCandidate.Value,
                RiskTier.Low,
                Money.Create(0m, Currency.Usd),
                "Test"),
            [],
            Now);

    private static ActionProposal SomeProposal() =>
        ActionProposal.Create(
            CorrelationId.Create("cycle-none"),
            Capability.OpportunityManagement,
            EquityReviewWorkPlan.RecordCandidate,
            ActionTarget.Create("Security", "AAPL"),
            new OpportunityCandidateParameters(
                EquityOpportunity.Type,
                PriceRecoveryRule.DiscovererId,
                "AAPL",
                0),
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service("test", "1.0"),
            "opportunity.record-candidate:none",
            Now);
}
