using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Operations;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>
/// The loop, driven end to end against the real policy engine and the real action gateway.
/// </summary>
/// <remarks>
/// The gateway and the engine are real on purpose. A test of the operating loop that stubbed the
/// gate would prove the stub works, and the whole question this file asks is whether the loop can
/// reach an effect without going through it.
/// </remarks>
public sealed class OperatingCycleRunnerTests
{
    private const string Template = "monitor-watchlist";

    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeClock _clock = new(Now);
    private readonly InMemoryCycleStore _cycles = new();
    private readonly InMemoryAutonomyGrantStore _grants = new();
    private readonly InMemoryEscalationStore _escalations = new();
    private readonly InMemoryShadowStore _shadow = new();
    private readonly FakeOutbox _outbox = new();
    private readonly RecordingAuditSink _audit = new();
    private readonly FakeIdempotencyStore _idempotency = new();
    private readonly FakeExecutionStore _executions = new();
    private readonly TestWriteAuthorization _writes = new();
    private readonly AutonomyContext _autonomy = new();
    private readonly FakeCorrelationContext _correlation = new();

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    private OperatingCycle Cycle(CycleBudget? budget = null, string template = Template)
    {
        var cycle = OperatingCycle.Start(
            CorrelationId.New(),
            Capability.SimulatedExecution,
            template,
            "trigger:" + Guid.NewGuid().ToString("n"),
            budget ?? CycleBudget.Create(TimeSpan.FromMinutes(10), Usd(1m), 50, 1),
            Currency.Usd,
            _clock.UtcNow);

        _cycles.TryAddAsync(cycle).GetAwaiter().GetResult();

        return cycle;
    }

    private ActionProposal Proposal(Guid cycleId, decimal exposure = 1_000m) =>
        ActionProposal.Create(
            _correlation.Current,
            Capability.SimulatedExecution,
            ActionType.Create("execution.simulated-order"),
            ActionTarget.Create("Instrument", "AAPL"),
            new PlanParameters(),
            ActionEconomics.Create(Usd(0m), Usd(exposure), ReversibilityClass.ReversibleWithCost),
            ProposedBy.Service("cycle-runner-tests", "1.0"),
            idempotencyKey: Guid.NewGuid().ToString("n"),
            _clock.UtcNow,
            cycleId);

    private OperatingCycleRunner Runner(ScriptedWorkPlan? plan, LimitSet? limits = null)
    {
        var contextProvider = new ContextProvider(_autonomy);
        var engine = new PolicyEngine();

        var gateway = new ActionGateway(
            engine,
            contextProvider,
            _audit,
            _idempotency,
            _executions,
            _writes,
            _clock);

        var escalations = new EscalationService(_escalations, _outbox, _audit, _correlation, _clock);
        var shadow = new ShadowRecorder(engine, _shadow, _outbox, _audit, _correlation, _clock);

        return new OperatingCycleRunner(
            _cycles,
            plan is null ? [] : [plan],
            _grants,
            _autonomy,
            contextProvider,
            gateway,
            new FixedLimits(limits ?? LimitSet.Empty),
            new FlatExposure(),
            escalations,
            shadow,
            _audit,
            _clock);
    }

    private ScriptedWorkPlan PlanProposing(decimal exposure = 1_000m) =>
        new(Template, context => context.Stage == CycleStage.ProposeAction
            ? new CycleStageResult
            {
                ModelSpend = Usd(0.01m),
                ProviderCalls = 1,
                Proposal = Proposal(context.CycleId, exposure),
            }
            : CycleStageResult.Nothing(Currency.Usd));

    private void GrantAutonomy(AutonomyMode mode = AutonomyMode.AutoExecuteBounded) =>
        _grants.Seed(AutonomyGrant.Issue(
            Capability.SimulatedExecution,
            null,
            ContextProvider.EnvironmentName,
            mode,
            RiskTier.Critical,
            Usd(100_000m),
            "limits.default",
            "operator@example.test",
            Now,
            TimeSpan.FromDays(7)));

    /// <summary>A plan whose Collect reports a failed fetch and which proposes nothing.</summary>
    private static ScriptedWorkPlan PlanFailingToCollect(string obstacle = "market data for Security:AAPL.US could not be acquired from 'eodhd-eod': refused.") =>
        new(Template, context => context.Stage == CycleStage.Collect
            ? new CycleStageResult
            {
                ModelSpend = Usd(0m),
                ProviderCalls = 1,
                ProviderFailed = true,
            }
            : CycleStageResult.Nothing(Currency.Usd))
        { Obstacle = obstacle };

    /// <summary>A plan whose fetch failed but which still proposed something.</summary>
    private ScriptedWorkPlan PlanFailingButProposing() =>
        new(Template, context => context.Stage switch
        {
            CycleStage.Collect => new CycleStageResult
            {
                ModelSpend = Usd(0m),
                ProviderCalls = 1,
                ProviderFailed = true,
            },
            CycleStage.ProposeAction => new CycleStageResult
            {
                ModelSpend = Usd(0.01m),
                ProviderCalls = 0,
                Proposal = Proposal(context.CycleId),
            },
            _ => CycleStageResult.Nothing(Currency.Usd),
        })
        { Obstacle = "the fetch failed but the pass proposed anyway." };

    private List<Escalation> ProviderFailures() =>
        _escalations.Escalations
            .Where(e => e.Reason == EscalationReason.ProviderFailure)
            .ToList();

    // ---- A failed fetch must not look like a quiet pass ---------------------------------------

    /// <summary>
    /// The defect this closes: the cycle completed, and nothing anywhere said the market data
    /// never arrived.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_with_no_proposal_raises_exactly_one_escalation()
    {
        var cycle = Cycle();
        var plan = PlanFailingToCollect();

        var result = await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        var escalation = Assert.Single(ProviderFailures());

        Assert.Equal(cycle.CycleId, escalation.CycleId!.Value);
        Assert.Equal(cycle.Capability, escalation.Capability);
        Assert.Null(escalation.ProposalId);

        // Completed, deliberately: the cycle ran to the end, it just has nothing to show.
        Assert.Equal(CycleStatus.Completed, result.Status);
        Assert.True(result.Escalated);
        Assert.Equal(CycleStages.Last, cycle.Stage);
    }

    [Fact]
    public async Task The_escalation_carries_the_work_plans_own_obstacle()
    {
        var cycle = Cycle();
        var plan = PlanFailingToCollect("the licence does not permit automated processing.");

        await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        var escalation = Assert.Single(ProviderFailures());

        // Named, so an operator is not left to guess between "the vendor refused us" and "the
        // series has not fallen".
        Assert.Contains("licence", escalation.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The distinction that makes the change worth anything. A pass that fetched cleanly and found
    /// nothing is the normal case and must stay silent.
    /// </summary>
    [Fact]
    public async Task A_successful_pass_that_proposes_nothing_raises_no_escalation()
    {
        var cycle = Cycle();
        var plan = new ScriptedWorkPlan(Template, _ => CycleStageResult.Nothing(Currency.Usd));

        var result = await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        Assert.Empty(_escalations.Escalations);
        Assert.Equal(CycleStatus.Completed, result.Status);
        Assert.False(result.Escalated);
    }

    /// <summary>
    /// A failed fetch that still produced a proposal goes through the gate as it always did. The
    /// gate owns that outcome, and a second escalation from the completion branch would double-
    /// report one pass.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_that_still_proposed_is_left_to_the_gate()
    {
        GrantAutonomy();

        var cycle = Cycle();
        var plan = PlanFailingButProposing();

        var result = await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        Assert.Empty(ProviderFailures());
        Assert.Equal(1, plan.Executions);
        Assert.Equal(CycleStatus.Completed, result.Status);
    }

    /// <summary>
    /// The plan need not explain itself, but the operator still gets told something actionable
    /// rather than an empty string.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_with_no_stated_obstacle_still_escalates_with_a_reason()
    {
        var cycle = Cycle();
        var plan = PlanFailingToCollect(obstacle: string.Empty);

        await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        var escalation = Assert.Single(ProviderFailures());

        Assert.False(string.IsNullOrWhiteSpace(escalation.Explanation));
        Assert.Contains("ingestion run ledger", escalation.Explanation, StringComparison.Ordinal);
    }

    // ---- The happy path ---------------------------------------------------------------------

    [Fact]
    public async Task A_granted_cycle_runs_every_stage_executes_once_and_completes()
    {
        GrantAutonomy();

        var cycle = Cycle();
        var plan = PlanProposing();

        var result = await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        Assert.Equal(CycleStatus.Completed, result.Status);
        Assert.False(result.Escalated);
        Assert.Equal(1, plan.Executions);
        Assert.Equal(1, _writes.WindowsOpened);
        Assert.Equal(CycleStages.Last, cycle.Stage);

        // Every stage except the gate is the plan's; the gate is the runner's.
        Assert.DoesNotContain(CycleStage.PolicyGate, plan.StagesRun);
        Assert.Equal(CycleStages.Ordered.Count - 1, plan.StagesRun.Count);
    }

    /// <summary>
    /// The measurement is taken against the resolution actually in force, and taking it changes
    /// nothing about what happened.
    /// </summary>
    [Fact]
    public async Task Running_a_cycle_records_a_shadow_measurement()
    {
        GrantAutonomy(AutonomyMode.PrepareForApproval);

        var cycle = Cycle();

        await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-a");

        var measurement = Assert.Single(_shadow.Decisions);

        Assert.Equal(AutonomyMode.PrepareForApproval, measurement.ActualMode);
        Assert.Equal(AutonomyMode.AutoExecuteBounded, measurement.ShadowMode);
        Assert.True(measurement.WouldHaveExecuted);
        Assert.Equal(1, _audit.CountOf(AuditEventType.ShadowDecisionRecorded));
    }

    // ---- Everything that stops it -------------------------------------------------------------

    /// <summary>
    /// The fail-closed case that matters most: no grant, so the loop asks a human rather than
    /// proceeding.
    /// </summary>
    [Fact]
    public async Task A_cycle_with_no_grant_escalates_and_suspends_without_executing()
    {
        var cycle = Cycle();
        var plan = PlanProposing();

        var result = await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        Assert.True(result.Escalated);
        Assert.Equal(CycleStatus.Suspended, cycle.Status);
        Assert.Equal(0, plan.Executions);
        Assert.Equal(0, _writes.WindowsOpened);

        var escalation = Assert.Single(_escalations.Escalations);

        Assert.Equal(EscalationReason.NoAutonomyGrant, escalation.Reason);
        Assert.Single(_outbox.Messages, m =>
            string.Equals(m.MessageType, OperationsMessages.EscalationRaised, StringComparison.Ordinal));
    }

    /// <summary>A breached ceiling is the decision, and it is taken before the gate is asked.</summary>
    [Fact]
    public async Task A_limit_breach_stops_the_cycle_before_the_gate()
    {
        GrantAutonomy();

        var cycle = Cycle();
        var plan = PlanProposing(exposure: 50_000m);

        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxPositionSize, Usd(100m))]);

        var result = await Runner(plan, limits).RunAsync(cycle.CycleId, "worker-a");

        Assert.True(result.Escalated);
        Assert.Equal(0, plan.Executions);
        Assert.Equal(EscalationReason.LimitBreach, Assert.Single(_escalations.Escalations).Reason);

        // Nothing was dispatched, so no measurement was taken either: there was no decision to
        // measure against.
        Assert.Empty(_shadow.Decisions);
    }

    /// <summary>
    /// The per-cycle cost ceiling accumulates across the pass, rather than judging each proposal
    /// on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the test the limit did not have, and could not pass.</strong>
    /// <c>MaxCostPerCycle</c> is read from configuration, documented in appsettings and evaluated
    /// on every gate - and until now the figure it was compared against was a hard zero supplied
    /// by the exposure provider, which is repository-scoped and has never been told which cycle it
    /// is serving. Each proposal was therefore weighed against the ceiling alone and a cycle could
    /// spend without bound, one affordable step at a time.
    /// </para>
    /// <para>
    /// Here the plan spends twelve dollars before it proposes anything, against a ten dollar
    /// ceiling and a budget with ample headroom - so the only thing that can stop it is the
    /// ceiling, and the only way the ceiling can see the spend is if the runner hands the cycle's
    /// own consumption to the limit engine. On the previous code this test executes the action.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_cycle_cost_ceiling_counts_what_the_cycle_has_already_spent()
    {
        GrantAutonomy();

        // Budget headroom well above the ceiling, so a budget exhaustion cannot be mistaken for
        // the limit binding. The two failure modes escalate for different reasons and this test
        // is about exactly one of them.
        var cycle = Cycle(CycleBudget.Create(TimeSpan.FromMinutes(10), Usd(100m), 50, 1));

        var plan = new ScriptedWorkPlan(Template, context => context.Stage switch
        {
            CycleStage.Collect => new CycleStageResult
            {
                ModelSpend = Usd(12m),
                ProviderCalls = 1,
            },

            CycleStage.ProposeAction => new CycleStageResult
            {
                ModelSpend = Usd(0.01m),
                ProviderCalls = 0,
                Proposal = Proposal(context.CycleId),
            },

            _ => CycleStageResult.Nothing(Currency.Usd),
        });

        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxCostPerCycle, Usd(10m))]);

        var result = await Runner(plan, limits).RunAsync(cycle.CycleId, "worker-a");

        Assert.True(result.Escalated);
        Assert.Equal(0, plan.Executions);
        Assert.Equal(EscalationReason.LimitBreach, Assert.Single(_escalations.Escalations).Reason);
    }

    /// <summary>
    /// The same ceiling does not refuse a cycle that has stayed under it.
    /// </summary>
    /// <remarks>
    /// The guard on the test above. A change that made the ceiling bind by making it bind always
    /// would satisfy that one and destroy the limit's usefulness.
    /// </remarks>
    [Fact]
    public async Task A_cycle_that_has_spent_little_still_passes_the_cost_ceiling()
    {
        GrantAutonomy();

        var cycle = Cycle(CycleBudget.Create(TimeSpan.FromMinutes(10), Usd(100m), 50, 1));

        var plan = new ScriptedWorkPlan(Template, context => context.Stage switch
        {
            CycleStage.Collect => new CycleStageResult
            {
                ModelSpend = Usd(1m),
                ProviderCalls = 1,
            },

            CycleStage.ProposeAction => new CycleStageResult
            {
                ModelSpend = Usd(0.01m),
                ProviderCalls = 0,
                Proposal = Proposal(context.CycleId),
            },

            _ => CycleStageResult.Nothing(Currency.Usd),
        });

        var limits = LimitSet.Create([Limit.OfMoney(LimitKind.MaxCostPerCycle, Usd(500m))]);

        var result = await Runner(plan, limits).RunAsync(cycle.CycleId, "worker-a");

        Assert.False(result.Escalated);
        Assert.Equal(1, plan.Executions);
        Assert.Empty(_escalations.Escalations);
    }

    [Fact]
    public async Task A_cycle_that_exhausts_its_budget_suspends_and_escalates()
    {
        GrantAutonomy();

        var cycle = Cycle(CycleBudget.Create(TimeSpan.FromMinutes(10), Usd(0.005m), 50, 1));

        var result = await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-a");

        Assert.True(result.Escalated);
        Assert.Equal(CycleStatus.Suspended, cycle.Status);
        Assert.Equal(EscalationReason.BudgetExhausted, Assert.Single(_escalations.Escalations).Reason);
    }

    [Fact]
    public async Task A_template_with_no_registered_plan_escalates_rather_than_doing_nothing()
    {
        GrantAutonomy();

        var cycle = Cycle(template: "not-registered");

        var result = await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-a");

        Assert.True(result.Escalated);
        Assert.Equal(CycleStatus.Suspended, cycle.Status);
        Assert.Contains("no work plan", result.Summary, StringComparison.Ordinal);
    }

    // ---- Concurrency and resumption -----------------------------------------------------------

    [Fact]
    public async Task A_cycle_another_worker_holds_is_left_alone()
    {
        GrantAutonomy();

        var cycle = Cycle();

        cycle.TryLease("worker-a", _clock.UtcNow, TimeSpan.FromMinutes(5));

        var result = await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-b");

        Assert.False(result.Leased);
        Assert.Contains("another worker", result.Summary, StringComparison.Ordinal);
        Assert.Equal(CycleStages.First, cycle.Stage);
    }

    /// <summary>
    /// The resumption path: a cycle already past a stage does not re-run it, and the effect behind
    /// the gate is suppressed by its idempotency key rather than repeated.
    /// </summary>
    [Fact]
    public async Task A_cycle_that_already_ran_is_not_run_again()
    {
        GrantAutonomy();

        var cycle = Cycle();

        await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-a");

        Assert.Equal(CycleStatus.Completed, cycle.Status);

        var second = await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-b");

        Assert.False(second.Leased);
        Assert.Equal(CycleStatus.Completed, second.Status);
    }

    [Fact]
    public async Task A_cycle_that_finds_nothing_to_propose_completes_without_escalating()
    {
        GrantAutonomy();

        var cycle = Cycle();
        var plan = new ScriptedWorkPlan(Template, _ => CycleStageResult.Nothing(Currency.Usd));

        var result = await Runner(plan).RunAsync(cycle.CycleId, "worker-a");

        Assert.Equal(CycleStatus.Completed, result.Status);
        Assert.False(result.Escalated);
        Assert.Empty(_escalations.Escalations);
        Assert.Equal(0, plan.Executions);
    }

    /// <summary>
    /// The autonomy scope is closed as soon as the dispatch is done, so nothing started afterwards
    /// inherits a resolution nobody made for it.
    /// </summary>
    [Fact]
    public async Task The_autonomy_scope_does_not_outlive_the_dispatch()
    {
        GrantAutonomy();

        var cycle = Cycle();

        await Runner(PlanProposing()).RunAsync(cycle.CycleId, "worker-a");

        Assert.Null(_autonomy.Current);
        Assert.Null(_autonomy.CycleId);
    }

    private sealed record PlanParameters : IActionParameters
    {
        public string Describe() => "cycle runner test order";
    }
}
