using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Approvals;
using AI.Investment.Application.Execution;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// Hand-written doubles for the execution path.
/// </summary>
/// <remarks>
/// <para>
/// The executor is wired to the <em>real</em> <c>ActionGateway</c> and the <em>real</em>
/// <c>PolicyEngine</c> in these tests. Only the things that would reach a database, a clock or a
/// venue are replaced. A safety test that stubs the gate it is meant to be testing proves that the
/// stub works.
/// </para>
/// <para>
/// Every double records what it was asked to do, so a test can assert not only the outcome but that
/// the steps after a refusal never ran - which is the property that matters when the question is
/// whether a refused action can still have an effect.
/// </para>
/// </remarks>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; set; }
}

internal sealed class FakePolicyContextProvider : IPolicyContextProvider
{
    private readonly PolicyContext _context;

    public FakePolicyContextProvider(PolicyContext context) => _context = context;

    public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_context);
}

internal sealed class RecordingAuditSink : IAuditSink
{
    public List<AuditRecord> Records { get; } = [];

    public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        Records.Add(record);

        return Task.CompletedTask;
    }
}

internal sealed class RecordingIdempotencyStore : IIdempotencyStore
{
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(
        string idempotencyKey,
        Guid proposalId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_claimed.Add(idempotencyKey));
}

internal sealed class RecordingExecutionStore : IActionExecutionStore
{
    public List<ActionExecution> Recorded { get; } = [];

    public Task RecordAsync(ActionExecution execution, CancellationToken cancellationToken = default)
    {
        Recorded.Add(execution);

        return Task.CompletedTask;
    }
}

internal sealed class TrackingWriteAuthorization : IWriteAuthorization
{
    private PolicyDecision? _current;

    public bool IsAuthorized => _current is not null;

    public Guid? AuthorizingDecisionId => _current?.DecisionId;

    public int WindowsOpened { get; private set; }

    public IDisposable Authorize(PolicyDecision decision)
    {
        _current = decision;
        WindowsOpened++;

        return new Window(this);
    }

    private sealed class Window : IDisposable
    {
        private readonly TrackingWriteAuthorization _owner;

        public Window(TrackingWriteAuthorization owner) => _owner = owner;

        public void Dispose() => _owner._current = null;
    }
}

internal sealed class InMemoryApprovalTokenStore : IApprovalTokenStore
{
    private readonly Dictionary<Guid, ApprovalToken> _tokens = new();

    public int ConsumeAttempts { get; private set; }

    public IReadOnlyCollection<ApprovalToken> Stored => _tokens.Values;

    public Task AddAsync(ApprovalToken token, CancellationToken cancellationToken = default)
    {
        _tokens[token.ApprovalTokenId] = token;

        return Task.CompletedTask;
    }

    public Task<ApprovalToken?> GetAsync(Guid approvalTokenId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ApprovalToken?>(_tokens.GetValueOrDefault(approvalTokenId));

    public Task<ApprovalRefusal> ConsumeAsync(
        Guid approvalTokenId,
        OpportunityId opportunityId,
        ActionProposal proposal,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ConsumeAttempts++;

        if (!_tokens.TryGetValue(approvalTokenId, out var token))
        {
            return Task.FromResult(ApprovalRefusal.Revoked);
        }

        var refusal = token.Check(opportunityId, proposal, nowUtc);

        if (refusal == ApprovalRefusal.None)
        {
            token.Consume(opportunityId, proposal, nowUtc);
        }

        return Task.FromResult(refusal);
    }
}

internal sealed class CountingUnitOfWork : IUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;

        return Task.FromResult(1);
    }
}

internal sealed class InMemoryLedgerStore : ILedgerStore
{
    public List<LedgerEntry> Entries { get; } = [];

    public Task AppendAsync(IEnumerable<LedgerEntry> entries, CancellationToken cancellationToken = default)
    {
        Entries.AddRange(entries);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LedgerEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LedgerEntry>>(Entries.ToList());

    public Task<IReadOnlyList<LedgerEntry>> ListForAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LedgerEntry>>(
            Entries.Where(entry => entry.OpportunityId == opportunityId).ToList());
}

internal sealed class FixedLimitProvider : ILimitProvider
{
    private readonly LimitSet _limits;

    public FixedLimitProvider(LimitSet limits) => _limits = limits;

    public Task<LimitSet> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_limits);
}

internal sealed class FixedExposureProvider : IExposureProvider
{
    private readonly ExposureSnapshot _snapshot;

    public FixedExposureProvider(ExposureSnapshot snapshot) => _snapshot = snapshot;

    public Task<ExposureSnapshot> GetAsync(Currency currency, CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshot);
}

internal sealed class FixedKillSwitch : IKillSwitch
{
    private readonly KillSwitchState _state;

    public FixedKillSwitch(KillSwitchState state) => _state = state;

    public Task<KillSwitchState> ReadAsync(
        Capability? capability = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_state);
}

internal sealed class InMemoryOpportunityRepository : IOpportunityRepository
{
    private readonly Dictionary<OpportunityId, Opportunity> _opportunities = new();

    public Task AddAsync(Opportunity opportunity, CancellationToken cancellationToken = default)
    {
        _opportunities[opportunity.OpportunityId] = opportunity;

        return Task.CompletedTask;
    }

    public Task<Opportunity?> GetAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Opportunity?>(_opportunities.GetValueOrDefault(opportunityId));

    public Task<IReadOnlyList<Opportunity>> ListAsync(
        OpportunityStatus status,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Opportunity>>(
            _opportunities.Values.Where(o => o.Status == status).Take(limit).ToList());
}

/// <summary>A venue that records what it was asked to do and answers however the test wants.</summary>
internal sealed class RecordingVenue : IExecutionVenue
{
    private readonly VenueResult _result;

    public RecordingVenue(VenueResult? result = null) =>
        _result = result ?? VenueResult.Ok(VenueFill.Create(
            "recording-venue-1",
            10m,
            Money.Create(100m, Currency.Usd),
            Money.Create(1m, Currency.Usd),
            new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)));

    public string VenueId => "recording";

    public bool IsSimulated => true;

    public List<VenueOrder> Orders { get; } = [];

    public Task<VenueResult> PlaceAsync(VenueOrder order, CancellationToken cancellationToken = default)
    {
        Orders.Add(order);

        return Task.FromResult(_result);
    }
}

/// <summary>Wires an executor to the real gateway and the real policy engine.</summary>
internal sealed class ExecutorHarness
{
    internal ExecutorHarness(
        KillSwitchState killSwitch = KillSwitchState.Disengaged,
        LimitSet? limits = null,
        ExposureSnapshot? exposure = null,
        PolicyContext? policy = null,
        VenueResult? venueResult = null,
        DateTime? nowUtc = null)
    {
        Clock = new FakeClock(nowUtc ?? Phase5Fixtures.Now);
        Audit = new RecordingAuditSink();
        Executions = new RecordingExecutionStore();
        WriteAuthorization = new TrackingWriteAuthorization();
        Tokens = new InMemoryApprovalTokenStore();
        Ledger = new InMemoryLedgerStore();
        Venue = new RecordingVenue(venueResult);
        Opportunities = new InMemoryOpportunityRepository();
        UnitOfWork = new CountingUnitOfWork();
        Positions = new InMemoryPositionEventStore();

        Gateway = new ActionGateway(
            new PolicyEngine(),
            new FakePolicyContextProvider(policy ?? Phase5Fixtures.PermissiveContext()),
            Audit,
            new RecordingIdempotencyStore(),
            Executions,
            WriteAuthorization,
            Clock);

        Executor = new OpportunityExecutor(
            Gateway,
            Tokens,
            Venue,
            Ledger,
            Positions,
            new FixedLimitProvider(limits ?? LimitSet.Empty),
            new FixedExposureProvider(
                exposure ?? ExposureSnapshot.Flat(Currency.Usd, Money.Create(100_000m, Currency.Usd))),
            new FixedKillSwitch(killSwitch),
            Opportunities,
            UnitOfWork,
            WriteAuthorization,
            Clock);
    }

    internal FakeClock Clock { get; }

    /// <summary>What the executor recorded against holdings. Block 3.</summary>
    internal InMemoryPositionEventStore Positions { get; }

    internal RecordingAuditSink Audit { get; }

    internal RecordingExecutionStore Executions { get; }

    internal TrackingWriteAuthorization WriteAuthorization { get; }

    internal InMemoryApprovalTokenStore Tokens { get; }

    internal InMemoryLedgerStore Ledger { get; }

    internal RecordingVenue Venue { get; }

    internal InMemoryOpportunityRepository Opportunities { get; }

    internal CountingUnitOfWork UnitOfWork { get; }

    internal ActionGateway Gateway { get; }

    internal OpportunityExecutor Executor { get; }
}

/// <summary>Wires an approval workflow to the real gateway and the real policy engine.</summary>
internal sealed class ApprovalHarness
{
    internal ApprovalHarness(PolicyContext policy, DateTime? nowUtc = null)
    {
        Clock = new FakeClock(nowUtc ?? Phase5Fixtures.Now);
        Audit = new RecordingAuditSink();
        WriteAuthorization = new TrackingWriteAuthorization();
        Tokens = new InMemoryApprovalTokenStore();
        Opportunities = new InMemoryOpportunityRepository();
        UnitOfWork = new CountingUnitOfWork();

        Gateway = new ActionGateway(
            new PolicyEngine(),
            new FakePolicyContextProvider(policy),
            Audit,
            new RecordingIdempotencyStore(),
            new RecordingExecutionStore(),
            WriteAuthorization,
            Clock);

        Workflow = new ApprovalWorkflow(
            Gateway,
            Opportunities,
            Tokens,
            UnitOfWork,
            new FixedCorrelationContext(),
            Clock);
    }

    internal FakeClock Clock { get; }

    internal RecordingAuditSink Audit { get; }

    internal TrackingWriteAuthorization WriteAuthorization { get; }

    internal InMemoryApprovalTokenStore Tokens { get; }

    internal InMemoryOpportunityRepository Opportunities { get; }

    internal CountingUnitOfWork UnitOfWork { get; }

    internal ActionGateway Gateway { get; }

    internal ApprovalWorkflow Workflow { get; }
}

internal sealed class FixedCorrelationContext : ICorrelationContext
{
    public CorrelationId Current { get; } = CorrelationId.Create("safety-tests");
}

/// <summary>
/// An in-memory position event store, idempotent on the venue reference.
/// </summary>
/// <remarks>
/// The uniqueness the real store gets from a database constraint, stated here so the executor's
/// behaviour can be exercised without one. Appending the same venue reference twice writes nothing
/// the second time and reports it.
/// </remarks>
internal sealed class InMemoryPositionEventStore : IPositionEventStore
{
    internal List<PositionEvent> Events { get; } = [];

    public Task<bool> AppendAsync(PositionEvent positionEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(positionEvent);

        if (Events.Exists(e => string.Equals(
                e.VenueReference,
                positionEvent.VenueReference,
                StringComparison.Ordinal)))
        {
            return Task.FromResult(false);
        }

        Events.Add(positionEvent);

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PositionEvent>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PositionEvent>>(Events);

    public Task<IReadOnlyList<PositionEvent>> ListForAsync(
        string instrument,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PositionEvent>>(Events
            .FindAll(e => string.Equals(e.Instrument, instrument, StringComparison.OrdinalIgnoreCase)));
}
