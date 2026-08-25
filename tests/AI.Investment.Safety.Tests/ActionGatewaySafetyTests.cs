using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The central safety claim of the platform, stated as tests: <strong>no side effect runs
/// without a policy decision permitting it.</strong>
/// </summary>
/// <remarks>
/// Each test asserts on whether the effect delegate was invoked, because that - not a returned
/// status - is what actually distinguishes "refused" from "done".
/// </remarks>
public sealed class ActionGatewaySafetyTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_denied_action_never_invokes_the_effect()
    {
        var harness = new Harness(PolicyOutcome.Deny);

        var outcome = await harness.DispatchAsync();

        Assert.Equal(ActionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal(0, harness.EffectInvocations);
        Assert.False(outcome.WasExecuted);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task An_approval_required_action_never_invokes_the_effect()
    {
        var harness = new Harness(PolicyOutcome.RequireApproval);

        var outcome = await harness.DispatchAsync();

        Assert.Equal(ActionOutcomeStatus.ApprovalRequired, outcome.Status);
        Assert.Equal(0, harness.EffectInvocations);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task A_permitted_action_invokes_the_effect_exactly_once()
    {
        var harness = new Harness(PolicyOutcome.Execute);

        var outcome = await harness.DispatchAsync();

        Assert.Equal(ActionOutcomeStatus.Executed, outcome.Status);
        Assert.Equal(1, harness.EffectInvocations);
        Assert.Equal("done", outcome.Result);
    }

    [Fact]
    public async Task A_duplicate_idempotency_key_suppresses_the_effect()
    {
        var harness = new Harness(PolicyOutcome.Execute);
        harness.Idempotency.AlwaysRefuse = true;

        var outcome = await harness.DispatchAsync();

        Assert.Equal(ActionOutcomeStatus.DuplicateSuppressed, outcome.Status);
        Assert.Equal(0, harness.EffectInvocations);
    }

    [Fact]
    public async Task Writes_are_authorised_only_while_the_effect_is_running()
    {
        var harness = new Harness(PolicyOutcome.Execute);

        Assert.False(harness.WriteAuthorization.IsAuthorized);

        await harness.DispatchAsync(_ =>
        {
            Assert.True(harness.WriteAuthorization.IsAuthorized);
            return Task.FromResult("done");
        });

        Assert.False(harness.WriteAuthorization.IsAuthorized);
    }

    [Fact]
    public async Task Writes_are_never_authorised_for_a_denied_action()
    {
        var harness = new Harness(PolicyOutcome.Deny);

        await harness.DispatchAsync();

        Assert.False(harness.WriteAuthorization.IsAuthorized);
        Assert.Equal(0, harness.WriteAuthorization.WindowsOpened);
    }

    [Fact]
    public async Task Every_outcome_is_recorded_in_the_audit_trail()
    {
        foreach (var outcome in new[] { PolicyOutcome.Deny, PolicyOutcome.RequireApproval, PolicyOutcome.Execute })
        {
            var harness = new Harness(outcome);

            await harness.DispatchAsync();

            Assert.NotEmpty(harness.Audit.Records);
            Assert.Contains(harness.Audit.Records, r => r.DecisionId is not null);
        }
    }

    [Fact]
    public async Task A_failing_effect_is_recorded_and_then_rethrown()
    {
        var harness = new Harness(PolicyOutcome.Execute);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.DispatchAsync(_ => throw new InvalidOperationException("boom")));

        Assert.Contains(harness.Audit.Records, r => r.EventType == AuditEventType.ActionFailed);

        // The recorded reason names the exception type but never its message: audit rows are
        // permanent and an exception message is exactly where a connection string ends up.
        var failure = harness.Executions.Recorded.Single();
        Assert.Equal(ActionExecutionStatus.Failed, failure.Status);
        Assert.DoesNotContain("boom", failure.FailureReason, StringComparison.Ordinal);
    }

    // ---- Domain-level guarantee ------------------------------------------------------------

    [Theory]
    [InlineData(PolicyOutcome.Deny)]
    [InlineData(PolicyOutcome.RequireApproval)]
    public void An_execution_cannot_start_from_a_decision_that_does_not_permit_execution(PolicyOutcome outcome)
    {
        var proposal = Harness.NewProposal();
        var decision = Harness.DecisionFor(proposal, outcome);

        Assert.Throws<DomainRuleViolationException>(() => ActionExecution.Start(proposal, decision, Now));
    }

    [Fact]
    public void An_execution_cannot_reuse_a_decision_made_for_a_different_proposal()
    {
        var authorised = Harness.NewProposal();
        var other = Harness.NewProposal();
        var decision = Harness.DecisionFor(authorised, PolicyOutcome.Execute);

        Assert.Throws<DomainRuleViolationException>(() => ActionExecution.Start(other, decision, Now));
    }

    [Fact]
    public void An_execution_completes_exactly_once()
    {
        var proposal = Harness.NewProposal();
        var execution = ActionExecution.Start(proposal, Harness.DecisionFor(proposal, PolicyOutcome.Execute), Now);

        execution.MarkSucceeded(Now);

        Assert.Throws<DomainRuleViolationException>(() => execution.MarkSucceeded(Now));
        Assert.Throws<DomainRuleViolationException>(() => execution.MarkFailed("late", Now));
    }

    // ---- Harness -----------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness(PolicyOutcome outcome)
        {
            Policy = new StubPolicyEngine(outcome);
            Audit = new RecordingAuditSink();
            Idempotency = new FakeIdempotencyStore();
            Executions = new RecordingExecutionStore();
            WriteAuthorization = new CountingWriteAuthorization();

            Gateway = new ActionGateway(
                Policy,
                new FixedPolicyContextProvider(),
                Audit,
                Idempotency,
                Executions,
                WriteAuthorization,
                new FixedClock(Now));
        }

        public ActionGateway Gateway { get; }

        public StubPolicyEngine Policy { get; }

        public RecordingAuditSink Audit { get; }

        public FakeIdempotencyStore Idempotency { get; }

        public RecordingExecutionStore Executions { get; }

        public CountingWriteAuthorization WriteAuthorization { get; }

        public int EffectInvocations { get; private set; }

        public Task<ActionOutcome<string>> DispatchAsync(Func<CancellationToken, Task<string>>? effect = null)
        {
            effect ??= _ => Task.FromResult("done");

            return Gateway.DispatchAsync(
                NewProposal(),
                token =>
                {
                    EffectInvocations++;
                    return effect(token);
                });
        }

        public static ActionProposal NewProposal() =>
            ActionProposal.Create(
                CorrelationId.New(),
                Capability.ReferenceDataManagement,
                ActionType.Create("test.action"),
                ActionTarget.Create("Test"),
                new TestParameters(),
                ActionEconomics.NoFinancialEffect(),
                ProposedBy.Service("test", "1.0"),
                Guid.NewGuid().ToString("n"),
                Now);

        public static PolicyDecision DecisionFor(ActionProposal proposal, PolicyOutcome outcome) => outcome switch
        {
            PolicyOutcome.Execute => PolicyDecision.Execute(proposal, "permitted", ["test@1"], Now),
            PolicyOutcome.RequireApproval => PolicyDecision.RequireApproval(proposal, "needs a human", ["test@1"], Now),
            _ => PolicyDecision.Deny(proposal, "refused", ["test@1"], Now),
        };

        private sealed record TestParameters : IActionParameters
        {
            public string Describe() => "test parameters";
        }
    }

    private sealed class StubPolicyEngine : IPolicyEngine
    {
        private readonly PolicyOutcome _outcome;

        public StubPolicyEngine(PolicyOutcome outcome) => _outcome = outcome;

        public PolicyDecision Evaluate(ActionProposal proposal, PolicyContext context, DateTime nowUtc) =>
            Harness.DecisionFor(proposal, _outcome);
    }

    private sealed class FixedPolicyContextProvider : IPolicyContextProvider
    {
        public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyContext.FailClosed("Test"));
    }

    private sealed class RecordingAuditSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionStore : IActionExecutionStore
    {
        public List<ActionExecution> Recorded { get; } = [];

        public Task RecordAsync(ActionExecution execution, CancellationToken cancellationToken = default)
        {
            Recorded.Add(execution);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        public bool AlwaysRefuse { get; set; }

        public Task<bool> TryClaimAsync(
            string idempotencyKey,
            Guid proposalId,
            DateTime nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(!AlwaysRefuse);
    }

    private sealed class CountingWriteAuthorization : IWriteAuthorization
    {
        private PolicyDecision? _current;

        public int WindowsOpened { get; private set; }

        public bool IsAuthorized => _current is not null;

        public Guid? AuthorizingDecisionId => _current?.DecisionId;

        public IDisposable Authorize(PolicyDecision decision)
        {
            if (decision.Outcome != PolicyOutcome.Execute)
            {
                throw new InvalidOperationException("Only an Execute decision opens a window.");
            }

            _current = decision;
            WindowsOpened++;
            return new Window(this);
        }

        private sealed class Window : IDisposable
        {
            private readonly CountingWriteAuthorization _owner;

            public Window(CountingWriteAuthorization owner) => _owner = owner;

            public void Dispose() => _owner._current = null;
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; }
    }
}
