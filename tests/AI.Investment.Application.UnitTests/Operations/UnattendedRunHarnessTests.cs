using System.Globalization;
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
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>
/// Drives the whole loop over a simulated fortnight and measures the invariants unattended
/// operation is judged on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is, and what it is not.</strong> It is a deterministic, accelerated-time
/// exercise of the controls: a virtual clock advances through fourteen days in half-hour ticks, two
/// watches fire, cycles run through the real policy engine and the real action gateway, and the
/// counts are checked against <see cref="UnattendedInvariants"/>. It demonstrates that the controls
/// hold across the sequences it exercises.
/// </para>
/// <para>
/// The fortnight deliberately contains the events that make the controls worth having, each
/// introduced at a fixed point so the run is reproducible: a feed that redelivers every observation
/// twice, a market-wide burst on day five that pushes past the per-watch firing allowance, cycles
/// whose provider usage exhausts their budget, two independent watches that reach the same action
/// and must produce one effect between them, a worker that dies mid-cycle and a second that picks
/// the cycle up after the lease expires, and an autonomy grant that expires at the end of week one
/// and is not renewed.
/// </para>
/// <para>
/// It is <strong>not</strong> two weeks of real unattended operation, and nothing here should be
/// read as though it were. A simulation cannot produce the failures that make the criterion worth
/// having: a provider that degrades at four in the morning, a clock that steps, a disk that fills, a
/// deployment mid-cycle, an operator who stops reading escalations in week two. The phase
/// documentation says so plainly rather than letting a green test imply otherwise.
/// </para>
/// <para>
/// The negative twin is what gives the rest their meaning. A harness that could only pass would
/// measure nothing, so the same fortnight with nobody answering escalations is run as well, and the
/// report fails.
/// </para>
/// </remarks>
public sealed class UnattendedRunHarnessTests
{
    private const string Template = "monitor-watchlist";

    private const string Instrument = "AAPL";

    /// <summary>The marker a killed worker carries, so a real defect is not mistaken for the injection.</summary>
    private const string WorkerDied = "simulated worker death";

    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Fortnight = TimeSpan.FromDays(14);

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(30);

    /// <summary>The grant runs out halfway, and nobody renews it. Week two is unattended and ungranted.</summary>
    private static readonly TimeSpan GrantValidFor = TimeSpan.FromDays(7);

    private static readonly DateTime GrantExpiresAtUtc = Start.Add(GrantValidFor);

    /// <summary>Per-cycle ceilings. Small enough that a heavy cycle actually reaches them.</summary>
    private static readonly CycleBudget Budget =
        CycleBudget.Create(TimeSpan.FromMinutes(15), Money.Create(1m, Currency.Usd), 40, 2);

    /// <summary>Six starts per watch per hour is what the day-five burst runs into.</summary>
    private static readonly AdmissionLimits Limits =
        AdmissionLimits.Create(4, 2, 100, 6, TimeSpan.FromHours(1));

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    [Fact]
    public async Task A_simulated_fortnight_holds_every_invariant_when_escalations_are_answered()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.True(run.Report.Holds, string.Join(" | ", run.Report.Failures));

        // The run has to have done something, or the invariants are vacuous.
        Assert.True(run.Counts.CyclesStarted > 20, $"only {run.Counts.CyclesStarted} cycles started");
        Assert.True(run.Counts.ActionsExecuted > 0);
        Assert.True(run.Counts.ShadowDecisions > 0);
        Assert.True(run.Counts.ModelSpend.IsPositive);
    }

    /// <summary>
    /// No duplicate actions, stated as the thing that actually matters: an effect ran once.
    /// </summary>
    [Fact]
    public async Task No_effect_runs_twice_across_the_fortnight()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.Equal(run.Counts.ActionsExecuted, run.DistinctEffects);

        // And the suppression that produced that equality actually happened. Two independent
        // watches reach the same action in the same window during the burst; the idempotency seam
        // is what stops the second one repeating it, and a run where it never fired would be
        // asserting the equality of two numbers that were never in danger.
        Assert.True(
            run.Counts.DuplicateActionsSuppressed > 0,
            "no duplicate action was ever suppressed, so the seam was not exercised");
    }

    /// <summary>Cooldown, backpressure and trigger-key deduplication, each counted separately.</summary>
    [Fact]
    public async Task Every_storm_control_is_exercised_and_holds()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.True(run.SuppressedByCooldown > 0, "no firing was ever held back by a watch cooldown");
        Assert.True(run.SuppressedByBackpressure > 0, "the firing-rate ceiling was never reached");
        Assert.True(run.SuppressedAsDuplicate > 0, "no redelivered observation was ever deduplicated");

        // Far more was suppressed than started, which is what a storm control looks like when it is
        // working rather than merely present.
        Assert.True(
            run.Counts.DuplicateCyclesSuppressed > run.Counts.CyclesStarted,
            "fewer observations were suppressed than started cycles, so the storm was never applied");
    }

    /// <summary>Budget enforcement: a cycle that overruns stops, and says so.</summary>
    [Fact]
    public async Task Cycles_that_overrun_their_budget_are_suspended_rather_than_allowed_to_continue()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.True(run.CyclesSuspendedByBudget > 0, "no cycle ever reached its budget ceiling");

        // Every one of them raised an escalation rather than stopping quietly.
        Assert.True(run.BudgetEscalations >= run.CyclesSuspendedByBudget);

        // And no cycle got past its ceiling: consumption never exceeds the budget it was started with.
        Assert.All(run.Cycles, cycle =>
            Assert.False(
                cycle.Consumption.ProviderCalls > Budget.MaxProviderCalls &&
                cycle.Status == CycleStatus.Running,
                $"cycle {cycle.CycleId} is still running past its provider-call ceiling"));
    }

    /// <summary>
    /// Crash and restart: a worker dies mid-cycle and another finishes the cycle, once.
    /// </summary>
    [Fact]
    public async Task A_cycle_whose_worker_dies_is_resumed_by_another_and_still_completes_once()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.True(run.CrashesInjected > 0, "no crash was ever injected, so recovery was not tested");
        Assert.Equal(run.CrashesInjected, run.CrashedCyclesFinished);

        // The recovery did not cost a second effect. This is the same equality as the duplicate
        // test, restated over the crashed cycles specifically, because a resumption that re-ran an
        // already-performed effect is exactly how at-most-once is normally lost.
        Assert.Equal(run.Counts.ActionsExecuted, run.DistinctEffects);
    }

    /// <summary>
    /// The grant expires halfway and nobody renews it. Nothing executes afterwards.
    /// </summary>
    [Fact]
    public async Task When_the_autonomy_grant_expires_the_platform_stops_executing_and_keeps_measuring()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.True(run.ExecutionsBeforeGrantExpiry > 0, "nothing executed while the grant was valid");

        // Fail closed. An expired grant is not a weaker grant; it is no grant, and no grant on an
        // execution capability is a denial rather than an approval request.
        Assert.Equal(0, run.ExecutionsAfterGrantExpiry);

        // Cycles kept running after the grant lapsed - the platform did not simply stop - and every
        // one of them reached a human instead of an effect.
        Assert.True(run.CyclesStartedAfterGrantExpiry > 0);
        Assert.True(run.NoGrantEscalations > 0, "the lapsed grant never produced an escalation");

        // And shadow measurement carried on through the second week, which is the whole point of
        // measuring separately from acting: the platform learns what a higher level would have done
        // in exactly the period when it is permitted to do nothing.
        Assert.True(run.ShadowAfterGrantExpiry > 0, "shadow measurement stopped when autonomy lapsed");
    }

    /// <summary>Shadow accumulation, and the fact that it never becomes an action.</summary>
    [Fact]
    public async Task Shadow_measurement_accumulates_and_never_executes_anything()
    {
        var run = await RunAsync(answerEscalations: true);

        Assert.True(run.Counts.ShadowDecisions > run.Counts.ActionsExecuted);

        // There are measurements saying a higher level would have executed. None of them did.
        Assert.True(
            run.ShadowWouldHaveExecuted > 0,
            "no shadow measurement ever differed from what actually happened, so the comparison " +
            "is not telling anybody anything");

        Assert.Equal(run.Counts.ActionsExecuted, run.DistinctEffects);

        // The effects that ran are exactly the ones the real gate authorised: the number of
        // authorisation windows the write seam opened.
        Assert.Equal(run.Counts.ActionsExecuted, run.AuthorisationWindows);
    }

    /// <summary>
    /// An operator who stops answering is how a human-in-the-loop control fails in practice, and the
    /// measurement has to be able to say so.
    /// </summary>
    [Fact]
    public async Task The_same_fortnight_fails_when_nobody_answers_the_escalations()
    {
        var run = await RunAsync(answerEscalations: false);

        Assert.False(run.Report.Holds);
        Assert.False(run.Report.NoUnhandledEscalation);
        Assert.Contains(run.Report.Failures, failure =>
            failure.Contains("reached their expiry unanswered", StringComparison.Ordinal));
    }

    private static async Task<SimulatedRun> RunAsync(bool answerEscalations)
    {
        var clock = new FakeClock(Start);
        var cycles = new InMemoryCycleStore();
        var watches = new InMemoryWatchStore();
        var escalations = new InMemoryEscalationStore();
        var shadow = new InMemoryShadowStore();
        var grants = new InMemoryAutonomyGrantStore();
        var outbox = new FakeOutbox();
        var audit = new RecordingAuditSink();
        var idempotency = new FakeIdempotencyStore();
        var executionStore = new FakeExecutionStore();
        var writes = new TestWriteAuthorization();
        var autonomy = new AutonomyContext();
        var correlation = new FakeCorrelationContext();

        var contextProvider = new ContextProvider(autonomy);
        var engine = new PolicyEngine();

        var gateway = new ActionGateway(
            engine, contextProvider, audit, idempotency, executionStore, writes, clock);

        var escalationService = new EscalationService(escalations, outbox, audit, correlation, clock);
        var shadowRecorder = new ShadowRecorder(engine, shadow, outbox, audit, correlation, clock);

        // Granted one level below unattended execution for half the exposure range, and only for a
        // week, so the fortnight produces executions, escalations, and then a period with neither
        // a grant nor anybody to renew it.
        grants.Seed(AutonomyGrant.Issue(
            Capability.SimulatedExecution,
            null,
            ContextProvider.EnvironmentName,
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Medium,
            Usd(5_000m),
            "limits.default",
            "operator@example.test",
            Start,
            GrantValidFor));

        var order = new CycleOrdering();
        var plan = new FortnightWorkPlan(Template, clock, correlation, order);
        var recordingPlan = new EffectRecordingPlan(plan, clock);

        var runner = new OperatingCycleRunner(
            cycles,
            [recordingPlan],
            grants,
            autonomy,
            contextProvider,
            gateway,
            new FixedLimits(LimitSet.Empty),
            new FlatExposure(),
            escalationService,
            shadowRecorder,
            audit,
            clock);

        var evaluator = new TriggerEvaluator(
            watches,
            cycles,
            new FixedAdmissionLimits(Limits),
            new FixedCycleBudget(Budget),
            audit,
            correlation,
            clock);

        var schedule = Watch.Create(
            "six-hourly review",
            WatchTarget.Create("Security", Instrument),
            TriggerType.Schedule,
            TriggerCondition.Every(TimeSpan.FromHours(6)),
            TimeSpan.FromHours(5),
            Capability.SimulatedExecution,
            Template,
            Start,
            maxSignalAge: TimeSpan.FromHours(1));

        // A second, independent watch on the same instrument. It exists so that two unrelated
        // reasons to act can reach the same action in the same window, which is the situation the
        // idempotency key is for and the one a single-watch harness never produces.
        var priceMove = Watch.Create(
            "price move",
            WatchTarget.Create("Security", Instrument),
            TriggerType.PriceMove,
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 3m),
            Watch.MinimumCooldown,
            Capability.SimulatedExecution,
            Template,
            Start,
            maxSignalAge: TimeSpan.FromHours(1));

        watches.Seed(schedule);
        watches.Seed(priceMove);

        var totals = new SuppressionTotals();
        var crashesInjected = 0;
        var crashedCycles = new HashSet<Guid>();
        var ticks = (int)(Fortnight.Ticks / Tick.Ticks);

        TriggerSignal? replay = null;

        for (var tick = 0; tick < ticks; tick++)
        {
            // A feed catching up: an observation it already sent, sent again half an hour later.
            // By then the watch's cooldown has passed, so the only thing standing between the
            // replay and a second cycle is the trigger key - which is the control this exercises,
            // and the one an immediate redelivery never reaches.
            if (replay is not null)
            {
                totals.Add(await evaluator.OfferAsync(replay));
                replay = null;
            }

            // Offered twice, because a feed that redelivers is the normal case rather than the
            // exception, and the deduplication has to hold across the whole fortnight.
            totals.Add(await evaluator.OfferAsync(Schedule(clock.UtcNow)));
            totals.Add(await evaluator.OfferAsync(Schedule(clock.UtcNow)));

            // A quiet price tape most of the time: below the threshold, so the watch does not fire.
            var move = tick % 8 == 0 ? 4m : 1m;
            var price = Price(clock.UtcNow, move);

            totals.Add(await evaluator.OfferAsync(price));

            if (move >= 3m)
            {
                replay = price;
            }

            crashesInjected += await DrainAsync(cycles, runner, clock, crashedCycles, plan, tick);

            // Day five: a market-wide event. Observations arrive far faster than the watch may
            // start cycles, which is what the firing allowance is for.
            if (tick == BurstTick)
            {
                for (var burst = 0; burst < 20; burst++)
                {
                    totals.Add(await evaluator.OfferAsync(Price(clock.UtcNow, move: 9m)));

                    crashesInjected += await DrainAsync(cycles, runner, clock, crashedCycles, plan, tick);

                    clock.Advance(TimeSpan.FromMinutes(2));
                }
            }

            if (answerEscalations)
            {
                foreach (var escalation in await escalations.GetOutstandingAsync())
                {
                    escalation.Resolve("operator@example.test", "reviewed and closed", clock.UtcNow);
                }
            }

            clock.Advance(Tick);
        }

        var all = cycles.Cycles;

        var counts = new UnattendedRunCounts(
            Start,
            clock.UtcNow,
            CyclesStarted: all.Count,
            CyclesCompleted: all.Count(c => c.Status == CycleStatus.Completed),
            CyclesSuspended: all.Count(c => c.Status == CycleStatus.Suspended),
            DuplicateCyclesSuppressed: totals.Total,
            ActionsExecuted: recordingPlan.Executions,
            DuplicateActionsSuppressed: idempotency.Suppressed,
            ModelSpend: all.Aggregate(Money.Zero(Currency.Usd), (total, c) => total.Add(c.Consumption.ModelSpend)),

            // A ceiling derived from what was configured rather than a round number chosen to pass:
            // every started cycle was allowed at most one budget's worth of model spend.
            SpendCeiling: Budget.MaxModelSpend.MultiplyBy(Math.Max(all.Count, 1)),
            EscalationsRaised: escalations.Escalations.Count,
            EscalationsUnhandled: await escalations.CountUnhandledAsync(clock.UtcNow),
            ShadowDecisions: shadow.Decisions.Count,
            OutboxAbandoned: 0);

        return new SimulatedRun(
            UnattendedInvariants.Evaluate(counts),
            recordingPlan.DistinctEffects,
            recordingPlan.ExecutionsBefore(GrantExpiresAtUtc),
            recordingPlan.ExecutionsFrom(GrantExpiresAtUtc),
            shadow.Decisions.Count(d => d.RecordedAtUtc >= GrantExpiresAtUtc),
            shadow.Decisions.Count(d => d.WouldHaveExecuted),
            totals.Cooldown,
            totals.Backpressure,
            totals.Duplicate,
            all.Count(c => c.StartedAtUtc >= GrantExpiresAtUtc),
            all.Count(c => c.Status == CycleStatus.Suspended &&
                c.Consumption.ProviderCalls >= Budget.MaxProviderCalls),
            escalations.Escalations.Count(e => e.Reason == EscalationReason.BudgetExhausted),
            escalations.Escalations.Count(e => e.Reason == EscalationReason.NoAutonomyGrant),
            crashesInjected,
            crashedCycles.Count(id => all.Any(c => c.CycleId == id && !c.IsRunning)),
            writes.WindowsOpened,
            all);
    }

    /// <summary>Roughly twice a day, the worker handling a runnable cycle dies inside a stage.</summary>
    private const int CrashEveryTicks = 40;

    /// <summary>Day five, in half-hour ticks: the market-wide event.</summary>
    private const int BurstTick = 5 * 48;

    /// <returns>How many workers were killed during this pass.</returns>
    private static async Task<int> DrainAsync(
        InMemoryCycleStore cycles,
        OperatingCycleRunner runner,
        FakeClock clock,
        HashSet<Guid> crashed,
        FortnightWorkPlan plan,
        int tick)
    {
        var runnable = await cycles.GetRunnableAsync(4, clock.UtcNow);
        var crashThisTick = tick > 0 && tick % CrashEveryTicks == 0;
        var crashesInjected = 0;

        foreach (var cycle in runnable)
        {
            if (crashThisTick && crashed.Add(cycle.CycleId))
            {
                // The worker dies part-way through. Nothing is written to say so: a process that
                // crashes does not get to record that it crashed, which is exactly why the lease
                // has to expire on its own.
                plan.CrashOnce(cycle.CycleId);
                crashesInjected++;
                crashThisTick = false;

                var died = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => runner.RunAsync(cycle.CycleId, "worker-a"));

                Assert.Contains(WorkerDied, died.Message, StringComparison.Ordinal);

                continue;
            }

            await runner.RunAsync(cycle.CycleId, "worker-b");
        }

        return crashesInjected;
    }

    private static TriggerSignal Schedule(DateTime nowUtc) =>
        TriggerSignal.Create(TriggerType.Schedule, WatchTarget.Create("Security", Instrument), nowUtc);

    private static TriggerSignal Price(DateTime nowUtc, decimal move) =>
        TriggerSignal.Create(TriggerType.PriceMove, WatchTarget.Create("Security", Instrument), nowUtc, move);

    private sealed record SimulatedRun(
        UnattendedRunReport Report,
        int DistinctEffects,
        int ExecutionsBeforeGrantExpiry,
        int ExecutionsAfterGrantExpiry,
        int ShadowAfterGrantExpiry,
        int ShadowWouldHaveExecuted,
        int SuppressedByCooldown,
        int SuppressedByBackpressure,
        int SuppressedAsDuplicate,
        int CyclesStartedAfterGrantExpiry,
        int CyclesSuspendedByBudget,
        int BudgetEscalations,
        int NoGrantEscalations,
        int CrashesInjected,
        int CrashedCyclesFinished,
        int AuthorisationWindows,
        IReadOnlyList<OperatingCycle> Cycles)
    {
        public UnattendedRunCounts Counts => Report.Counts;
    }

    /// <summary>Adds up what each offer suppressed, keeping the three controls apart.</summary>
    private sealed class SuppressionTotals
    {
        public int Cooldown { get; private set; }

        public int Backpressure { get; private set; }

        public int Duplicate { get; private set; }

        public int Total => Cooldown + Backpressure + Duplicate;

        public void Add(TriggerOutcome outcome)
        {
            Cooldown += outcome.SuppressedByCooldown;
            Backpressure += outcome.SuppressedByBackpressure;
            Duplicate += outcome.SuppressedAsDuplicate;
        }
    }

    /// <summary>Gives every cycle a stable number in the order it was first seen.</summary>
    private sealed class CycleOrdering
    {
        private readonly Dictionary<Guid, int> _index = [];

        public int Of(Guid cycleId)
        {
            if (_index.TryGetValue(cycleId, out var existing))
            {
                return existing;
            }

            var next = _index.Count;
            _index[cycleId] = next;

            return next;
        }
    }

    /// <summary>
    /// The plan the fortnight runs: cheap most of the time, occasionally expensive, and able to die.
    /// </summary>
    private sealed class FortnightWorkPlan : ICycleWorkPlan
    {
        private readonly FakeClock _clock;
        private readonly ICorrelationContext _correlation;
        private readonly CycleOrdering _order;
        private readonly HashSet<Guid> _crashNext = [];

        public FortnightWorkPlan(
            string templateName,
            FakeClock clock,
            ICorrelationContext correlation,
            CycleOrdering order)
        {
            TemplateName = templateName;
            _clock = clock;
            _correlation = correlation;
            _order = order;
        }

        public string TemplateName { get; }

        /// <summary>Arranges for this cycle's next stage to kill its worker, once.</summary>
        public void CrashOnce(Guid cycleId) => _crashNext.Add(cycleId);

        public Task<CycleStageResult> RunStageAsync(
            CycleStageContext context,
            CancellationToken cancellationToken = default)
        {
            if (_crashNext.Remove(context.CycleId))
            {
                throw new InvalidOperationException(
                    $"{WorkerDied}: cycle {context.CycleId} at {context.Stage}.");
            }

            var index = _order.Of(context.CycleId);

            return Task.FromResult(context.Stage switch
            {
                // Every seventh cycle turns out to need far more provider work than it was given.
                CycleStage.Collect when index % 7 == 0 => new CycleStageResult
                {
                    ModelSpend = Usd(0.02m),
                    ProviderCalls = Budget.MaxProviderCalls + 20,
                },

                CycleStage.Analyze => new CycleStageResult
                {
                    ModelSpend = Usd(0.03m),
                    ProviderCalls = 2,
                },

                CycleStage.ProposeAction => new CycleStageResult
                {
                    ModelSpend = Usd(0.01m),
                    ProviderCalls = 1,

                    // Every fourth cycle proposes an order above the grant's ceiling, so the
                    // fortnight contains escalations as well as executions.
                    Proposal = Order(
                        _correlation.Current,
                        context.CycleId,
                        _clock.UtcNow,
                        exposure: index % 4 == 0 ? 50_000m : 1_000m),
                },

                _ => CycleStageResult.Nothing(Currency.Usd),
            });
        }

        public Task<string> ExecuteAsync(
            ActionProposal proposal,
            AutonomyResolution autonomy,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"executed {proposal.ActionType} at {autonomy.Mode}");
    }

    /// <summary>Wraps a plan and records the identity and time of every effect that actually ran.</summary>
    private sealed class EffectRecordingPlan : ICycleWorkPlan
    {
        private readonly ICycleWorkPlan _inner;
        private readonly FakeClock _clock;
        private readonly List<(string Key, DateTime WhenUtc)> _effects = [];

        public EffectRecordingPlan(ICycleWorkPlan inner, FakeClock clock)
        {
            _inner = inner;
            _clock = clock;
        }

        public string TemplateName => _inner.TemplateName;

        public int Executions => _effects.Count;

        public int DistinctEffects => _effects.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count();

        public int ExecutionsBefore(DateTime whenUtc) => _effects.Count(e => e.WhenUtc < whenUtc);

        public int ExecutionsFrom(DateTime whenUtc) => _effects.Count(e => e.WhenUtc >= whenUtc);

        public Task<CycleStageResult> RunStageAsync(
            CycleStageContext context,
            CancellationToken cancellationToken = default) =>
            _inner.RunStageAsync(context, cancellationToken);

        public async Task<string> ExecuteAsync(
            ActionProposal proposal,
            AutonomyResolution autonomy,
            CancellationToken cancellationToken = default)
        {
            _effects.Add((proposal.IdempotencyKey, _clock.UtcNow));

            return await _inner.ExecuteAsync(proposal, autonomy, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ActionProposal Order(
        CorrelationId correlationId,
        Guid cycleId,
        DateTime nowUtc,
        decimal exposure) =>
        ActionProposal.Create(
            correlationId,
            Capability.SimulatedExecution,
            ActionType.Create("execution.simulated-order"),
            ActionTarget.Create("Instrument", Instrument),
            new HarnessParameters(cycleId),
            ActionEconomics.Create(Usd(0m), Usd(exposure), ReversibilityClass.ReversibleWithCost),
            ProposedBy.Service("harness", "1.0"),

            // Keyed on the action rather than on the cycle: what makes two orders the same order is
            // the instrument and the window, not which watch happened to notice. Two independent
            // watches reaching the same conclusion in the same six hours must produce one effect.
            idempotencyKey: string.Create(
                CultureInfo.InvariantCulture,
                $"order:{Instrument}:{(nowUtc - Start).Ticks / TimeSpan.FromHours(6).Ticks}"),
            nowUtc,
            cycleId);

    private sealed record HarnessParameters(Guid CycleId) : IActionParameters
    {
        public string Describe() => "harness order for cycle " + CycleId.ToString("n");
    }
}
