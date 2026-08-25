using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.UnitTests.Fakes;

/// <summary>
/// Hand-written test doubles.
/// </summary>
/// <remarks>
/// Preferred over a mocking library at this size: they are readable without knowing a setup DSL,
/// they document the contracts they implement, and a compiler error appears the moment an
/// interface changes - which is exactly when a silently-stale mock would start lying.
/// </remarks>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; }
}

public sealed class FixedCorrelationContext : ICorrelationContext
{
    public CorrelationId Current { get; } = CorrelationId.Create("test-correlation");
}

public sealed class InMemoryCompanyRepository : ICompanyRepository
{
    public List<Company> Companies { get; } = [];

    public List<Company> Staged { get; } = [];

    public Task<Company?> GetByIdAsync(CompanyId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Companies.FirstOrDefault(c => c.Id == id));

    public Task<Company?> GetByTickerAsync(Ticker ticker, CancellationToken cancellationToken = default) =>
        Task.FromResult(Companies.FirstOrDefault(c => c.Ticker == ticker));

    public Task<bool> ExistsWithTickerAsync(Ticker ticker, CancellationToken cancellationToken = default) =>
        Task.FromResult(Companies.Any(c => c.Ticker == ticker));

    public Task<IReadOnlyList<Company>> SearchAsync(
        string? query,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Company>>(
            Companies.OrderBy(c => c.Name, StringComparer.Ordinal).Skip(skip).Take(take).ToList());

    public Task<int> CountAsync(string? query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Companies.Count);

    public void Add(Company company) => Staged.Add(company);
}

public sealed class CountingUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }
}

/// <summary>
/// A gateway stub that records the proposal it was handed and honours a fixed outcome, without
/// evaluating any real policy. Lets a handler be tested for "did it route through the seam and
/// respect the answer" separately from "is the policy correct".
/// </summary>
public sealed class StubActionGateway : IActionGateway
{
    private readonly ActionOutcomeStatus _status;

    public StubActionGateway(ActionOutcomeStatus status = ActionOutcomeStatus.Executed) => _status = status;

    public ActionProposal? LastProposal { get; private set; }

    public int EffectInvocations { get; private set; }

    public async Task<ActionOutcome<TResult>> DispatchAsync<TResult>(
        ActionProposal proposal,
        Func<CancellationToken, Task<TResult>> effect,
        CancellationToken cancellationToken = default)
    {
        LastProposal = proposal;

        var now = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        if (_status != ActionOutcomeStatus.Executed)
        {
            // InternalsVisibleTo lets the stub use the same factories the real gateway uses,
            // so the outcome shape under test is genuinely the production one.
            return _status switch
            {
                ActionOutcomeStatus.Denied => ActionOutcome<TResult>.Denied(
                    PolicyDecision.Deny(proposal, "stub denial", ["stub@1"], now)),

                ActionOutcomeStatus.ApprovalRequired => ActionOutcome<TResult>.ApprovalRequired(
                    PolicyDecision.RequireApproval(proposal, "stub approval requirement", ["stub@1"], now)),

                _ => ActionOutcome<TResult>.DuplicateSuppressed(
                    PolicyDecision.Execute(proposal, "stub duplicate", ["stub@1"], now)),
            };
        }

        var decision = PolicyDecision.Execute(proposal, "stub permission", ["stub@1"], now);

        EffectInvocations++;
        var result = await effect(cancellationToken).ConfigureAwait(false);

        var execution = ActionExecution.Start(proposal, decision, now);
        execution.MarkSucceeded(now);

        return ActionOutcome<TResult>.Executed(decision, result, execution);
    }
}

public sealed class NullAuditSink : IAuditSink
{
    public List<AuditRecord> Records { get; } = [];

    public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }
}

public sealed class AlwaysDenyPolicyContextProvider : IPolicyContextProvider
{
    public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PolicyContext.FailClosed("Test"));
}

public sealed class PermissivePolicyContextProvider : IPolicyContextProvider
{
    public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PolicyContext.Create(
            "Test",
            KillSwitchState.Disengaged,
            [CapabilityPolicy.Create(Capability.ReferenceDataManagement, enabled: true, RiskTier.Low)]));
}

public sealed class AcceptingIdempotencyStore : IIdempotencyStore
{
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(
        string idempotencyKey,
        Guid proposalId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_claimed.Add(idempotencyKey));
}

public sealed class NullExecutionStore : IActionExecutionStore
{
    public List<ActionExecution> Recorded { get; } = [];

    public Task RecordAsync(ActionExecution execution, CancellationToken cancellationToken = default)
    {
        Recorded.Add(execution);
        return Task.CompletedTask;
    }
}

public sealed class SimpleWriteAuthorization : IWriteAuthorization
{
    private PolicyDecision? _current;

    public bool IsAuthorized => _current is not null;

    public Guid? AuthorizingDecisionId => _current?.DecisionId;

    public IDisposable Authorize(PolicyDecision decision)
    {
        _current = decision;
        return new Window(this);
    }

    private sealed class Window : IDisposable
    {
        private readonly SimpleWriteAuthorization _owner;

        public Window(SimpleWriteAuthorization owner) => _owner = owner;

        public void Dispose() => _owner._current = null;
    }
}
