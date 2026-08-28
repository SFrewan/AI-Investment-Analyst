using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Operations;

/// <summary>
/// The loop as a persisted state machine: resumable, deterministic, and safe under failure.
/// </summary>
/// <remarks>
/// Every test here corresponds to a way an unattended loop goes wrong: it skips a step, it repeats
/// one, two workers take the same work, it runs past its budget, or it carries on after being told
/// to stop.
/// </remarks>
public sealed class OperatingCycleTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    private static CycleBudget Budget(
        int minutes = 15,
        decimal spend = 1m,
        int calls = 50,
        int actions = 1) =>
        CycleBudget.Create(TimeSpan.FromMinutes(minutes), Usd(spend), calls, actions);

    private static OperatingCycle Cycle(CycleBudget? budget = null, DateTime? nowUtc = null) =>
        OperatingCycle.Start(
            CorrelationId.New(),
            Capability.OpportunityManagement,
            "monitor-watchlist",
            "watch:test:" + Guid.NewGuid().ToString("n"),
            budget ?? Budget(),
            Currency.Usd,
            nowUtc ?? Now);

    [Fact]
    public void A_cycle_starts_running_at_the_first_stage()
    {
        var cycle = Cycle();

        Assert.Equal(CycleStatus.Running, cycle.Status);
        Assert.Equal(CycleStages.First, cycle.Stage);
        Assert.Equal(0, cycle.EscalationCount);
        Assert.True(cycle.Consumption.ModelSpend.IsZero);
    }

    [Fact]
    public void A_cycle_must_name_its_template_and_its_trigger()
    {
        Assert.Throws<DomainValidationException>(() => OperatingCycle.Start(
            CorrelationId.New(), Capability.Analysis, "  ", "key", Budget(), Currency.Usd, Now));

        var error = Assert.Throws<DomainValidationException>(() => OperatingCycle.Start(
            CorrelationId.New(), Capability.Analysis, "template", "  ", Budget(), Currency.Usd, Now));

        Assert.Contains("starts two cycles", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The resumption contract: a retried worker sees that the stage it meant to reach is already
    /// reached and skips the work rather than doing it twice.
    /// </summary>
    [Fact]
    public void Advancing_to_the_stage_it_is_already_in_is_a_no_op_that_says_so()
    {
        var cycle = Cycle();

        Assert.False(cycle.Advance(CycleStage.Discover, Now));
        Assert.True(cycle.Advance(CycleStage.Collect, Now));
        Assert.False(cycle.Advance(CycleStage.Collect, Now));
    }

    /// <summary>Skipping a stage would mean deciding on evidence that was never collected.</summary>
    [Fact]
    public void A_cycle_cannot_skip_a_stage()
    {
        var cycle = Cycle();

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            cycle.Advance(CycleStage.ExecuteOrEscalate, Now));

        Assert.Equal("OperatingCycle.NoSkip", error.Rule);
    }

    /// <summary>Replaying a stage would repeat effects the seam has already recorded as performed.</summary>
    [Fact]
    public void A_cycle_cannot_go_back()
    {
        var cycle = Cycle();

        cycle.Advance(CycleStage.Collect, Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            cycle.Advance(CycleStage.Discover, Now));

        Assert.Equal("OperatingCycle.NoRewind", error.Rule);
    }

    [Fact]
    public void A_stopped_cycle_cannot_advance()
    {
        var cycle = Cycle();

        cycle.Suspend("waiting for a human", Now);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            cycle.Advance(CycleStage.Collect, Now));

        Assert.Equal("OperatingCycle.NotRunning", error.Rule);
    }

    [Fact]
    public void A_cycle_completes_only_at_the_last_stage()
    {
        var cycle = Cycle();

        var early = Assert.Throws<DomainRuleViolationException>(() => cycle.Complete(Now));

        Assert.Equal("OperatingCycle.IncompleteStages", early.Rule);

        foreach (var stage in CycleStages.Ordered.Skip(1))
        {
            cycle.Advance(stage, Now);
        }

        cycle.Complete(Now);

        Assert.Equal(CycleStatus.Completed, cycle.Status);
        Assert.True(cycle.IsFinished);
        Assert.Null(cycle.LeaseOwner);
    }

    // ---- Concurrent workers ---------------------------------------------------------------

    [Fact]
    public void A_lease_keeps_a_second_worker_out()
    {
        var cycle = Cycle();

        Assert.True(cycle.TryLease("worker-a", Now, TimeSpan.FromMinutes(5)));
        Assert.False(cycle.TryLease("worker-b", Now.AddMinutes(1), TimeSpan.FromMinutes(5)));
        Assert.True(cycle.HoldsLease("worker-a", Now.AddMinutes(1)));
    }

    /// <summary>
    /// A worker that dies holding a lease releases it by not renewing it, and nothing has to notice
    /// the process is gone.
    /// </summary>
    [Fact]
    public void An_expired_lease_lets_another_worker_recover_the_cycle()
    {
        var cycle = Cycle();

        cycle.TryLease("worker-a", Now, TimeSpan.FromMinutes(5));

        Assert.True(cycle.TryLease("worker-b", Now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(cycle.HoldsLease("worker-a", Now.AddMinutes(6)));
    }

    [Fact]
    public void Releasing_a_lease_somebody_else_now_holds_is_not_an_error()
    {
        var cycle = Cycle();

        cycle.TryLease("worker-a", Now, TimeSpan.FromMinutes(5));
        cycle.TryLease("worker-b", Now.AddMinutes(6), TimeSpan.FromMinutes(5));

        cycle.ReleaseLease("worker-a", Now.AddMinutes(7));

        Assert.True(cycle.HoldsLease("worker-b", Now.AddMinutes(7)));
    }

    [Fact]
    public void A_stopped_cycle_cannot_be_leased()
    {
        var cycle = Cycle();

        cycle.Suspend("stopped", Now);

        Assert.False(cycle.TryLease("worker-a", Now, TimeSpan.FromMinutes(5)));
    }

    // ---- Budgets ---------------------------------------------------------------------------

    /// <summary>
    /// A cycle that quietly analysed half its evidence and then decided would produce output
    /// indistinguishable from a complete one.
    /// </summary>
    [Fact]
    public void Exhausting_a_budget_suspends_the_cycle_rather_than_truncating_it()
    {
        var cycle = Cycle(Budget(spend: 0.10m));

        var verdict = cycle.Consume(Usd(0.50m), providerCalls: 1, actions: 0, Now);

        Assert.True(verdict.IsExhausted);
        Assert.Equal(BudgetKind.ModelSpend, verdict.Kind);
        Assert.Equal(CycleStatus.Suspended, cycle.Status);
        Assert.Contains("budget exhausted", cycle.StoppedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wall_clock_ceiling_stops_a_cycle_that_spent_nothing()
    {
        var cycle = Cycle(Budget(minutes: 5));

        var verdict = cycle.CheckBudget(Now.AddMinutes(6));

        Assert.True(verdict.IsExhausted);
        Assert.Equal(BudgetKind.WallClock, verdict.Kind);
        Assert.Equal(CycleStatus.Suspended, cycle.Status);
    }

    [Fact]
    public void Consumption_accumulates_and_a_cycle_inside_its_budget_keeps_running()
    {
        var cycle = Cycle(Budget(spend: 1m, calls: 10));

        cycle.Consume(Usd(0.20m), providerCalls: 3, actions: 0, Now);
        var verdict = cycle.Consume(Usd(0.20m), providerCalls: 3, actions: 0, Now);

        Assert.False(verdict.IsExhausted);
        Assert.Equal(0.40m, cycle.Consumption.ModelSpend.Amount);
        Assert.Equal(6, cycle.Consumption.ProviderCalls);
        Assert.True(cycle.IsRunning);
    }

    [Fact]
    public void A_stopped_cycle_cannot_consume_budget()
    {
        var cycle = Cycle();

        cycle.Suspend("stopped", Now);

        Assert.Throws<DomainRuleViolationException>(() =>
            cycle.Consume(Usd(0.01m), 1, 0, Now));
    }

    // ---- Stopping and resuming ---------------------------------------------------------------

    [Fact]
    public void Escalating_suspends_the_cycle_and_counts_the_question()
    {
        var cycle = Cycle();

        cycle.Escalate("the limit engine refused the action", Now);

        Assert.Equal(CycleStatus.Suspended, cycle.Status);
        Assert.Equal(1, cycle.EscalationCount);
    }

    /// <summary>
    /// A cycle that suspended itself on a budget and resumed itself has no budget, so resuming names
    /// who decided to.
    /// </summary>
    [Fact]
    public void Resuming_names_who_authorised_it()
    {
        var cycle = Cycle();

        cycle.Suspend("budget exhausted", Now);

        Assert.Throws<DomainValidationException>(() => cycle.Resume("  ", Now));

        cycle.Resume("operator@example.test", Now);

        Assert.Equal(CycleStatus.Running, cycle.Status);
        Assert.Contains("operator@example.test", cycle.StoppedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_suspended_cycle_can_be_resumed()
    {
        var cycle = Cycle();

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            cycle.Resume("operator", Now));

        Assert.Equal("OperatingCycle.NotSuspended", error.Rule);
    }

    /// <summary>Rewriting the outcome of finished work would make the record of it worthless.</summary>
    [Fact]
    public void A_completed_cycle_cannot_then_fail()
    {
        var cycle = Cycle();

        foreach (var stage in CycleStages.Ordered.Skip(1))
        {
            cycle.Advance(stage, Now);
        }

        cycle.Complete(Now);

        var error = Assert.Throws<DomainRuleViolationException>(() => cycle.Fail("too late", Now));

        Assert.Equal("OperatingCycle.AlreadyCompleted", error.Rule);
    }

    [Fact]
    public void Every_stage_transition_and_stop_requires_a_utc_instant()
    {
        var cycle = Cycle();
        var local = DateTime.SpecifyKind(Now, DateTimeKind.Local);

        Assert.Throws<DomainValidationException>(() => cycle.Advance(CycleStage.Collect, local));
        Assert.Throws<DomainValidationException>(() => cycle.Suspend("x", local));
        Assert.Throws<DomainValidationException>(() => cycle.TryLease("w", local, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void The_stage_order_is_declared_once_and_has_no_gaps()
    {
        Assert.Equal(CycleStage.Discover, CycleStages.First);
        Assert.Equal(CycleStage.Record, CycleStages.Last);
        Assert.Null(CycleStages.Next(CycleStages.Last));
        Assert.Null(CycleStages.Next(CycleStage.Unknown));

        for (var i = 0; i < CycleStages.Ordered.Count - 1; i++)
        {
            Assert.Equal(CycleStages.Ordered[i + 1], CycleStages.Next(CycleStages.Ordered[i]));
        }

        // Every declared stage except Unknown appears exactly once in the order.
        var declared = Enum.GetValues<CycleStage>().Where(s => s != CycleStage.Unknown).ToList();

        Assert.Equal(declared.Count, CycleStages.Ordered.Count);
        Assert.Equal(declared.Count, CycleStages.Ordered.Distinct().Count());
    }
}
