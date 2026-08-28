using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Operations;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using AI.Investment.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Operations;

/// <summary>
/// The continuous-operation tables against a real PostgreSQL, through the real migrations.
/// </summary>
/// <remarks>
/// <para>
/// Three of these tests are the ones worth having, because their subjects cannot be established
/// anywhere else. The unique trigger key is what turns a trigger storm into one cycle, and only the
/// database enforces it. The write guard's narrow permission - a cycle may record its progress and
/// nothing else - is a rule about change-tracker entries, and only a real context has those. And two
/// workers racing for the same cycle is a race between processes, which an in-memory store cannot
/// have.
/// </para>
/// <para>
/// The converted columns matter too: a budget that serialises and never materialises looks correct
/// in every unit test written against the object graph in memory.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class Phase6PersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public Phase6PersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    private static CycleBudget Budget() =>
        CycleBudget.Create(TimeSpan.FromMinutes(15), Usd(1.25m), 40, 2);

    private static OperatingCycle Cycle(string? triggerKey = null) =>
        OperatingCycle.Start(
            CorrelationId.New(),
            Capability.SimulatedExecution,
            "monitor-watchlist",
            triggerKey ?? "watch:" + Guid.NewGuid().ToString("n"),
            Budget(),
            Currency.Usd,
            Now,
            Guid.NewGuid());

    // ---- Round trips --------------------------------------------------------------------------

    [SkippableFact]
    public async Task A_cycle_round_trips_with_its_budget_and_its_consumption()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var cycle = Cycle();

        cycle.Consume(Usd(0.30m), providerCalls: 4, actions: 1, Now);

        await using (var context = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfCycleStore(context).TryAddAsync(cycle);
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await verification.OperatingCycles
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CycleId == cycle.CycleId);

        Assert.NotNull(stored);
        Assert.Equal("monitor-watchlist", stored!.TemplateName);
        Assert.Equal(CycleStatus.Running, stored.Status);
        Assert.Equal(CycleStage.Discover, stored.Stage);
        Assert.Equal(TimeSpan.FromMinutes(15), stored.Budget.MaxWallClock);
        Assert.Equal(1.25m, stored.Budget.MaxModelSpend.Amount);
        Assert.Equal("USD", stored.Budget.MaxModelSpend.Currency.Code);
        Assert.Equal(0.30m, stored.Consumption.ModelSpend.Amount);
        Assert.Equal(4, stored.Consumption.ProviderCalls);
        Assert.Equal(1, stored.Consumption.Actions);
    }

    [SkippableFact]
    public async Task A_grant_a_watch_an_escalation_and_a_shadow_decision_all_round_trip()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var grant = AutonomyGrant.Issue(
            Capability.SimulatedExecution,
            "execution.simulated-order",
            "Test",
            AutonomyMode.AutoExecuteBounded,
            RiskTier.Medium,
            Usd(10_000m),
            "limits.default",
            "operator@example.test",
            Now,
            TimeSpan.FromDays(7));

        var watch = Watch.Create(
            "watchlist price move",
            WatchTarget.Create("Security", "AAPL"),
            TriggerType.PriceMove,
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 0.05m),
            TimeSpan.FromMinutes(30),
            Capability.Analysis,
            "monitor-watchlist",
            Now);

        var escalation = Escalation.Raise(
            Capability.SimulatedExecution,
            EscalationReason.LimitBreach,
            "the position-size ceiling would be exceeded",
            Now,
            TimeSpan.FromHours(24),
            Guid.NewGuid(),
            Guid.NewGuid());

        var shadow = ShadowDecision.Record(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Capability.SimulatedExecution,
            "execution.simulated-order",
            RiskTier.Medium,
            Usd(1_000m),
            AutonomyMode.PrepareForApproval,
            PolicyOutcome.RequireApproval,
            AutonomyMode.AutoExecuteBounded,
            PolicyOutcome.Execute,
            "the shadow gate permitted unattended execution",
            Now);

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            // The grant and the watch are ordinary domain state, so they need the seam; the
            // escalation and the measurement are the platform's account of itself and do not.
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await new EfAutonomyGrantStore(context).AddAsync(grant);
                await new EfWatchStore(context).AddAsync(watch);
                await context.SaveChangesAsync();
            }

            await new EfEscalationStore(context).AddAsync(escalation);
            await new EfShadowDecisionStore(context).AddAsync(shadow);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var storedGrant = await verification.AutonomyGrants.AsNoTracking()
            .FirstAsync(g => g.AutonomyGrantId == grant.AutonomyGrantId);

        Assert.Equal(AutonomyMode.AutoExecuteBounded, storedGrant.EffectiveMode);
        Assert.Equal("execution.simulated-order", storedGrant.ActionType);
        Assert.Equal(10_000m, storedGrant.MaxExposure.Amount);
        Assert.True(storedGrant.IsActive(Now));

        var storedWatch = await verification.Watches.AsNoTracking()
            .FirstAsync(w => w.WatchId == watch.WatchId);

        Assert.Equal("Security", storedWatch.Target.Kind);
        Assert.Equal("AAPL", storedWatch.Target.Identifier);
        Assert.Equal(TriggerComparison.MovedAtLeast, storedWatch.Condition.Comparison);
        Assert.Equal(0.05m, storedWatch.Condition.Threshold);
        Assert.True(storedWatch.Enabled);

        var storedEscalation = await verification.Escalations.AsNoTracking()
            .FirstAsync(e => e.EscalationId == escalation.EscalationId);

        Assert.Equal(EscalationReason.LimitBreach, storedEscalation.Reason);
        Assert.True(storedEscalation.IsUnhandled(Now.AddHours(25)));

        var storedShadow = await verification.ShadowDecisions.AsNoTracking()
            .FirstAsync(s => s.ShadowDecisionId == shadow.ShadowDecisionId);

        Assert.True(storedShadow.WouldHaveExecuted);
        Assert.Equal(1_000m, storedShadow.Exposure.Amount);
    }

    // ---- Deduplication and concurrency ---------------------------------------------------------

    /// <summary>
    /// The single constraint that turns a trigger storm into one cycle. Enforced by the database
    /// rather than by a read-then-write, because that races exactly when it matters.
    /// </summary>
    [SkippableFact]
    public async Task Two_cycles_for_the_same_observation_become_one()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var key = "watch:storm:" + Guid.NewGuid().ToString("n");

        await using var first = _fixture.CreateContext(new ScopedWriteAuthorization());
        await using var second = _fixture.CreateContext(new ScopedWriteAuthorization());

        var added = await new EfCycleStore(first).TryAddAsync(Cycle(key));
        var duplicate = await new EfCycleStore(second).TryAddAsync(Cycle(key));

        Assert.True(added);
        Assert.False(duplicate);

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(1, await verification.OperatingCycles.CountAsync(c => c.TriggerKey == key));
    }

    /// <summary>
    /// Two workers in different processes race for the same cycle. The lease decides in memory; the
    /// row's concurrency token decides between processes, and the loser is told rather than
    /// silently overwriting the winner.
    /// </summary>
    [SkippableFact]
    public async Task Two_workers_cannot_both_advance_the_same_cycle()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var cycle = Cycle();

        await using (var setup = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfCycleStore(setup).TryAddAsync(cycle);
        }

        await using var contextA = _fixture.CreateContext(new ScopedWriteAuthorization());
        await using var contextB = _fixture.CreateContext(new ScopedWriteAuthorization());

        var storeA = new EfCycleStore(contextA);
        var storeB = new EfCycleStore(contextB);

        var forA = await storeA.FindAsync(cycle.CycleId);
        var forB = await storeB.FindAsync(cycle.CycleId);

        Assert.NotNull(forA);
        Assert.NotNull(forB);

        Assert.True(forA!.TryLease("worker-a", Now, TimeSpan.FromMinutes(5)));
        Assert.True(forB!.TryLease("worker-b", Now, TimeSpan.FromMinutes(5)));

        await storeA.SaveAsync();

        // B read the row before A wrote it, so its own write is refused rather than clobbering A's.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => storeB.SaveAsync());

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await verification.OperatingCycles.AsNoTracking()
            .FirstAsync(c => c.CycleId == cycle.CycleId);

        Assert.Equal("worker-a", stored.LeaseOwner);
    }

    // ---- The write guard -----------------------------------------------------------------------

    /// <summary>
    /// A cycle records its own progress with no authorisation window, because the moment it most
    /// needs to is the moment an action was refused.
    /// </summary>
    [SkippableFact]
    public async Task A_cycle_records_its_progress_without_an_authorisation_window()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var cycle = Cycle();

        await using (var setup = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfCycleStore(setup).TryAddAsync(cycle);
        }

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var store = new EfCycleStore(context);
        var stored = await store.FindAsync(cycle.CycleId);

        stored!.Advance(CycleStage.Collect, Now);
        stored.Consume(Usd(0.05m), 1, 0, Now);

        await store.SaveAsync();

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var reloaded = await verification.OperatingCycles.AsNoTracking()
            .FirstAsync(c => c.CycleId == cycle.CycleId);

        Assert.Equal(CycleStage.Collect, reloaded.Stage);
        Assert.Equal(0.05m, reloaded.Consumption.ModelSpend.Amount);
    }

    /// <summary>
    /// And it may not rewrite what it is about. "The platform may record its own progress" never
    /// widens into "the platform may edit what it recorded".
    /// </summary>
    [SkippableFact]
    public async Task A_cycle_cannot_have_its_identity_rewritten_or_be_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var cycle = Cycle();

        await using (var setup = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfCycleStore(setup).TryAddAsync(cycle);
        }

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await context.OperatingCycles.FirstAsync(c => c.CycleId == cycle.CycleId);

        // Reaching past the aggregate, which is the only way this is even expressible.
        context.Entry(stored).Property(nameof(OperatingCycle.TriggerKey)).CurrentValue = "rewritten";

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());

        context.Entry(stored).Property(nameof(OperatingCycle.TriggerKey)).CurrentValue = cycle.TriggerKey;
        context.Entry(stored).State = EntityState.Deleted;

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    /// <summary>A measurement that could be edited afterwards is not a measurement.</summary>
    [SkippableFact]
    public async Task A_shadow_decision_cannot_be_changed_after_it_is_recorded()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var shadow = ShadowDecision.Record(
            null,
            Guid.NewGuid(),
            Capability.Analysis,
            "analysis.run",
            RiskTier.Low,
            Usd(0m),
            AutonomyMode.Advise,
            PolicyOutcome.RequireApproval,
            AutonomyMode.PrepareForApproval,
            PolicyOutcome.Execute,
            "measured",
            Now);

        await using (var setup = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfShadowDecisionStore(setup).AddAsync(shadow);
            await setup.SaveChangesAsync();
        }

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await context.ShadowDecisions.FirstAsync(s => s.ShadowDecisionId == shadow.ShadowDecisionId);

        context.Entry(stored).Property(nameof(ShadowDecision.ShadowOutcome)).CurrentValue = PolicyOutcome.Deny;

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    // ---- The outbox -----------------------------------------------------------------------------

    /// <summary>
    /// Queuing the same fact twice queues it once, which is what makes the step that produced it
    /// safe to retry.
    /// </summary>
    [SkippableFact]
    public async Task Queuing_the_same_fact_twice_queues_it_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var envelope = new OutboxEnvelope(
            "operations.escalation-raised@1",
            "{\"a\":\"b\"}",
            "escalation:" + Guid.NewGuid().ToString("n"),
            CorrelationId.New().Value);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var outbox = new EfOutbox(context, new SystemClock());

        Assert.True(await outbox.EnqueueAsync(envelope));
        Assert.False(await outbox.EnqueueAsync(envelope));

        await context.SaveChangesAsync();

        await using var second = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.False(await new EfOutbox(second, new SystemClock()).EnqueueAsync(envelope));

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(1, await verification.OutboxMessages.CountAsync(m => m.DedupKey == envelope.DedupKey));
    }

    /// <summary>
    /// A queued message may change its delivery state and nothing else. A payload that could be
    /// edited after it was queued would make the atomicity the outbox exists for decorative.
    /// </summary>
    [SkippableFact]
    public async Task A_queued_message_may_change_its_delivery_state_and_not_its_payload()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var envelope = new OutboxEnvelope(
            "operations.cycle-finished@1",
            "{\"cycle\":\"x\"}",
            "cycle:" + Guid.NewGuid().ToString("n"),
            CorrelationId.New().Value);

        await using (var setup = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfOutbox(setup, new SystemClock()).EnqueueAsync(envelope);
            await setup.SaveChangesAsync();
        }

        await using (var deliver = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            var message = await deliver.OutboxMessages.FirstAsync(m => m.DedupKey == envelope.DedupKey);

            message.MarkDispatched(DateTime.UtcNow);

            await deliver.SaveChangesAsync();
        }

        await using var tamper = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await tamper.OutboxMessages.FirstAsync(m => m.DedupKey == envelope.DedupKey);

        Assert.Equal(OutboxStatus.Dispatched, stored.Status);

        tamper.Entry(stored).Property(nameof(OutboxMessage.Payload)).CurrentValue = "{\"cycle\":\"rewritten\"}";

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => tamper.SaveChangesAsync());
    }

    /// <summary>
    /// A watch records that it fired without a window - that is its own bookkeeping - but everything
    /// else about it is ordinary domain state.
    /// </summary>
    [SkippableFact]
    public async Task A_watch_records_a_firing_without_a_window_but_cannot_be_reconfigured_without_one()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var watch = Watch.Create(
            "guard test",
            WatchTarget.Create("Security", "AAPL"),
            TriggerType.PriceMove,
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 0.05m),
            TimeSpan.FromMinutes(30),
            Capability.Analysis,
            "monitor-watchlist",
            Now);

        var authorization = new ScopedWriteAuthorization();

        await using (var setup = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await new EfWatchStore(setup).AddAsync(watch);
                await setup.SaveChangesAsync();
            }
        }

        await using (var firing = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            var store = new EfWatchStore(firing);
            var stored = await store.FindAsync(watch.WatchId);

            stored!.RecordFiring(Now);

            await store.SaveAsync();
        }

        await using var reconfigure = _fixture.CreateContext(new ScopedWriteAuthorization());

        var toDisable = await new EfWatchStore(reconfigure).FindAsync(watch.WatchId);

        Assert.Equal(1, toDisable!.FireCount);

        toDisable.Disable("too noisy", Now);

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => reconfigure.SaveChangesAsync());
    }
}
