using AI.Investment.Application.Execution;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The five gates between an approved opportunity and a fill, and the order they run in.
/// </summary>
/// <remarks>
/// <para>
/// Each test breaks one gate and asserts two things: that the outcome names that gate, and that
/// <strong>nothing after it ran</strong>. The second half is the one that matters. A refusal that
/// still consumed an approval, still reached the venue or still posted to the ledger is not a
/// refusal, and an outcome object saying "refused" would hide it.
/// </para>
/// <para>
/// The executor is wired to the real action gateway and the real policy engine, so a change that
/// weakened either would fail here rather than passing against a stub.
/// </para>
/// </remarks>
public sealed class OpportunityExecutorSafetyTests
{
    [Fact]
    public async Task An_engaged_kill_switch_stops_everything_before_anything_happens()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness(killSwitch: KillSwitchState.Engaged);

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByKillSwitch, outcome.Status);
        Assert.Equal(0, harness.Tokens.ConsumeAttempts);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
        Assert.False(scenario.Token.IsConsumed);
    }

    [Fact]
    public async Task A_kill_switch_of_unknown_state_stops_everything_exactly_as_an_engaged_one_does()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness(killSwitch: KillSwitchState.Unknown);

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByKillSwitch, outcome.Status);
        Assert.Contains("could not be determined", outcome.Explanation, StringComparison.Ordinal);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
    }

    [Fact]
    public async Task A_breached_limit_stops_the_action_before_the_approval_is_consumed()
    {
        var scenario = Phase5Fixtures.Build(quantity: 10m, price: 100m);

        var harness = new ExecutorHarness(
            limits: LimitSet.Create([Limit.OfMoney(LimitKind.MaxPositionSize, Phase5Fixtures.Usd(10m))]));

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByLimits, outcome.Status);
        Assert.Contains(outcome.Breaches, breach => breach.Kind == LimitKind.MaxPositionSize);
        Assert.Equal(0, harness.Tokens.ConsumeAttempts);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
        Assert.False(scenario.Token.IsConsumed);
    }

    [Fact]
    public async Task Limits_that_could_not_be_read_refuse_the_action()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness(limits: LimitSet.FailClosed);

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByLimits, outcome.Status);
        Assert.Empty(harness.Venue.Orders);
    }

    [Fact]
    public async Task A_policy_that_denies_the_capability_stops_the_action_and_leaves_the_token_unspent()
    {
        var scenario = Phase5Fixtures.Build();

        var harness = new ExecutorHarness(policy: PolicyContext.FailClosed("Test"));

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.DeniedByPolicy, outcome.Status);
        Assert.Equal(0, harness.Tokens.ConsumeAttempts);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
        Assert.False(scenario.Token.IsConsumed);
    }

    [Fact]
    public async Task A_capability_whose_ceiling_is_below_the_computed_tier_escalates_rather_than_executing()
    {
        var scenario = Phase5Fixtures.Build();

        var policy = PolicyContext.Create(
            "Test",
            KillSwitchState.Disengaged,
            [CapabilityPolicy.Create(Capability.SimulatedExecution, enabled: true, RiskTier.Low)]);

        var harness = new ExecutorHarness(policy: policy);

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.ApprovalRequired, outcome.Status);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
    }

    [Fact]
    public async Task An_approval_that_does_not_authorise_the_action_stops_it_before_the_venue()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness();

        scenario.Token.Revoke("The market moved.", Phase5Fixtures.Now);

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByApproval, outcome.Status);
        Assert.Equal(ApprovalRefusal.Revoked, outcome.ApprovalRefusal);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
    }

    [Fact]
    public async Task An_approval_that_is_not_in_the_store_at_all_refuses_rather_than_proceeding()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness();

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByApproval, outcome.Status);
        Assert.Empty(harness.Venue.Orders);
        Assert.Empty(harness.Ledger.Entries);
    }

    [Fact]
    public async Task An_expired_approval_refuses_even_though_everything_else_permits_it()
    {
        var scenario = Phase5Fixtures.Build(validFor: TimeSpan.FromMinutes(30));

        var harness = new ExecutorHarness(nowUtc: Phase5Fixtures.Now);

        await harness.Tokens.AddAsync(scenario.Token);

        harness.Clock.UtcNow = Phase5Fixtures.Now.AddHours(1);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.RefusedByApproval, outcome.Status);
        Assert.Equal(ApprovalRefusal.Expired, outcome.ApprovalRefusal);
        Assert.Empty(harness.Venue.Orders);
    }

    [Fact]
    public async Task A_venue_that_refuses_leaves_no_ledger_entry_and_still_spends_the_approval()
    {
        var scenario = Phase5Fixtures.Build();

        var harness = new ExecutorHarness(
            venueResult: VenueResult.Rejected("The venue is closed."));

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.Equal(ExecutionStatus.VenueRejected, outcome.Status);
        Assert.Empty(harness.Ledger.Entries);
        Assert.Single(harness.Venue.Orders);
        Assert.True(scenario.Token.IsConsumed);
    }

    [Fact]
    public async Task A_permitted_action_fills_posts_balanced_books_and_activates_the_opportunity()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness();

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.True(outcome.Executed);
        Assert.NotNull(outcome.Fill);
        Assert.Single(harness.Venue.Orders);
        Assert.True(scenario.Token.IsConsumed);

        Assert.True(CapitalLedger.IsBalanced(harness.Ledger.Entries, Currency.Usd));
        Assert.Equal(
            1_000m,
            CapitalLedger.Balance(LedgerAccount.Positions, harness.Ledger.Entries, Currency.Usd).Amount);
        Assert.Equal(
            -1_001m,
            CapitalLedger.Balance(LedgerAccount.Cash, harness.Ledger.Entries, Currency.Usd).Amount);

        Assert.Equal(OpportunityStatus.Active, scenario.Opportunity.Status);
        Assert.NotNull(scenario.Opportunity.ExecutionId);
    }

    [Fact]
    public async Task An_execution_is_audited_whether_it_filled_or_not()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness();

        await harness.Tokens.AddAsync(scenario.Token);
        await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.NotEmpty(harness.Audit.Records);
        Assert.Single(harness.Executions.Recorded);

        // Two windows: the gateway's, around the effect, and the executor's, around the
        // opportunity's own transition - opened with the same decision, and both closed.
        Assert.Equal(2, harness.WriteAuthorization.WindowsOpened);
        Assert.Equal(1, harness.UnitOfWork.Saves);
        Assert.False(harness.WriteAuthorization.IsAuthorized);
    }

    [Fact]
    public async Task A_denied_action_is_audited_and_opens_no_write_window()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness(policy: PolicyContext.FailClosed("Test"));

        await harness.Tokens.AddAsync(scenario.Token);
        await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.NotEmpty(harness.Audit.Records);
        Assert.Empty(harness.Executions.Recorded);
        Assert.Equal(0, harness.WriteAuthorization.WindowsOpened);
    }

    [Fact]
    public async Task A_replayed_order_is_suppressed_rather_than_filled_twice()
    {
        var scenario = Phase5Fixtures.Build();
        var harness = new ExecutorHarness();

        await harness.Tokens.AddAsync(scenario.Token);

        var first = await harness.Executor.ExecuteAsync(scenario.Request);
        var second = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.True(first.Executed);
        Assert.Equal(ExecutionStatus.DuplicateSuppressed, second.Status);
        Assert.Single(harness.Venue.Orders);
        Assert.Equal(2, harness.Ledger.Entries.Count);
    }

    [Fact]
    public async Task A_sale_with_a_known_cost_basis_records_the_realised_result_and_still_balances()
    {
        var scenario = Phase5Fixtures.Build(
            side: OrderSide.Sell,
            quantity: 10m,
            price: 100m,
            costBasis: Phase5Fixtures.Usd(900m));

        var harness = new ExecutorHarness();

        await harness.Tokens.AddAsync(scenario.Token);

        var outcome = await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.True(outcome.Executed);
        Assert.True(CapitalLedger.IsBalanced(harness.Ledger.Entries, Currency.Usd));
        Assert.Equal(
            100m,
            CapitalLedger.Balance(LedgerAccount.RealisedGains, harness.Ledger.Entries, Currency.Usd).Amount);
    }

    [Fact]
    public async Task A_sale_with_no_stated_cost_basis_invents_no_profit()
    {
        var scenario = Phase5Fixtures.Build(side: OrderSide.Sell, quantity: 10m, price: 100m);
        var harness = new ExecutorHarness();

        await harness.Tokens.AddAsync(scenario.Token);

        await harness.Executor.ExecuteAsync(scenario.Request);

        Assert.True(
            CapitalLedger.Balance(LedgerAccount.RealisedGains, harness.Ledger.Entries, Currency.Usd).IsZero);
        Assert.True(
            CapitalLedger.Balance(LedgerAccount.RealisedLosses, harness.Ledger.Entries, Currency.Usd).IsZero);
    }
}
