using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;

namespace AI.Investment.Application.UnitTests.Operations;

/// <summary>Hand-written doubles for the continuous-operation application tests.</summary>
/// <remarks>
/// No mocking framework, in keeping with the rest of this repository. A hand-written double states
/// what it does in code somebody can read; a configured mock states it in an expression that reads
/// like a spell, and the difference matters most in the tests that are about safety.
/// </remarks>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTime nowUtc) => UtcNow = nowUtc;

    public DateTime UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    public void SetTo(DateTime nowUtc) => UtcNow = nowUtc;
}

internal sealed class FakeCorrelationContext : ICorrelationContext
{
    public CorrelationId Current { get; } = CorrelationId.New();
}

/// <summary>Records what was written without needing a database.</summary>
internal sealed class RecordingAuditSink : IAuditSink
{
    private readonly List<AuditRecord> _records = [];

    public IReadOnlyList<AuditRecord> Records => _records;

    public int CountOf(AuditEventType eventType) =>
        _records.Count(record => record.EventType == eventType);

    public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        _records.Add(record);

        return Task.CompletedTask;
    }
}

/// <summary>An in-memory queue that behaves like the real one where it matters: it deduplicates.</summary>
internal sealed class FakeOutbox : IOutbox
{
    private readonly List<OutboxEnvelope> _messages = [];

    public IReadOnlyList<OutboxEnvelope> Messages => _messages;

    public Task<bool> EnqueueAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_messages.Any(m => string.Equals(m.DedupKey, envelope.DedupKey, StringComparison.Ordinal)))
        {
            return Task.FromResult(false);
        }

        _messages.Add(envelope);

        return Task.FromResult(true);
    }
}

internal sealed class InMemoryEscalationStore : IEscalationStore
{
    private readonly List<Escalation> _escalations = [];

    public IReadOnlyList<Escalation> Escalations => _escalations;

    public Task AddAsync(Escalation escalation, CancellationToken cancellationToken = default)
    {
        _escalations.Add(escalation);

        return Task.CompletedTask;
    }

    public Task<Escalation?> FindAsync(Guid escalationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_escalations.FirstOrDefault(e => e.EscalationId == escalationId));

    public Task<IReadOnlyList<Escalation>> GetOutstandingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Escalation>>(_escalations.Where(e => !e.IsResolved).ToList());

    public Task<int> CountUnhandledAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_escalations.Count(e => e.IsUnhandled(nowUtc)));

    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class InMemoryShadowStore : IShadowDecisionStore
{
    private readonly List<ShadowDecision> _decisions = [];

    public IReadOnlyList<ShadowDecision> Decisions => _decisions;

    public Task AddAsync(ShadowDecision decision, CancellationToken cancellationToken = default)
    {
        _decisions.Add(decision);

        return Task.CompletedTask;
    }

    public Task<int> CountAsync(DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_decisions.Count(d => d.RecordedAtUtc >= sinceUtc));

    public Task<int> CountWouldHaveExecutedAsync(DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_decisions.Count(d => d.RecordedAtUtc >= sinceUtc && d.WouldHaveExecuted));

    public Task<IReadOnlyList<ShadowDecision>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ShadowDecision>>(
            _decisions.OrderByDescending(d => d.RecordedAtUtc).Take(Math.Max(limit, 0)).ToList());

    public Task<IReadOnlyList<ShadowDecision>> GetBetweenAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ShadowDecision>>(
            _decisions
                .Where(d => d.RecordedAtUtc >= fromUtc && d.RecordedAtUtc <= toUtc)
                .OrderBy(d => d.RecordedAtUtc)
                .ToList());

    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// An in-memory cycle store whose <see cref="TryAddAsync"/> deduplicates on the trigger key, which
/// is the behaviour the real store gets from a unique index.
/// </summary>
internal sealed class InMemoryCycleStore : ICycleStore
{
    private readonly List<OperatingCycle> _cycles = [];

    public IReadOnlyList<OperatingCycle> Cycles => _cycles;

    public int SaveCount { get; private set; }

    public Task<bool> TryAddAsync(OperatingCycle cycle, CancellationToken cancellationToken = default)
    {
        if (_cycles.Any(c => string.Equals(c.TriggerKey, cycle.TriggerKey, StringComparison.Ordinal)))
        {
            return Task.FromResult(false);
        }

        _cycles.Add(cycle);

        return Task.FromResult(true);
    }

    public Task<OperatingCycle?> FindAsync(Guid cycleId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cycles.FirstOrDefault(c => c.CycleId == cycleId));

    public Task<OperatingCycle?> FindByTriggerKeyAsync(string triggerKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cycles.FirstOrDefault(c =>
            string.Equals(c.TriggerKey, triggerKey, StringComparison.Ordinal)));

    public Task<IReadOnlyList<OperatingCycle>> GetRunnableAsync(
        int limit,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OperatingCycle>>(
            _cycles
                .Where(c => c.Status == CycleStatus.Running)
                .Where(c => c.LeaseExpiresAtUtc is null || c.LeaseExpiresAtUtc <= nowUtc)
                .OrderBy(c => c.UpdatedAtUtc)
                .Take(Math.Max(limit, 0))
                .ToList());

    public Task<int> CountRunningAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_cycles.Count(c => c.Status == CycleStatus.Running));

    public Task<int> CountRunningAsync(Capability capability, CancellationToken cancellationToken = default) =>
        Task.FromResult(_cycles.Count(c => c.Status == CycleStatus.Running && c.Capability == capability));

    public Task<int> CountStartedByWatchAsync(
        Guid watchId,
        DateTime sinceUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_cycles.Count(c => c.WatchId == watchId && c.StartedAtUtc >= sinceUtc));

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryWatchStore : IWatchStore
{
    private readonly List<Watch> _watches = [];

    public int SaveCount { get; private set; }

    public void Seed(Watch watch) => _watches.Add(watch);

    public Task<IReadOnlyList<Watch>> GetEnabledAsync(
        TriggerType triggerType,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Watch>>(
            _watches.Where(w => w.TriggerType == triggerType && w.Enabled)
                .OrderByDescending(w => w.Priority)
                .ToList());

    public Task<IReadOnlyList<Watch>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Watch>>(_watches.ToList());

    public Task<Watch?> FindAsync(Guid watchId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_watches.FirstOrDefault(w => w.WatchId == watchId));

    public Task AddAsync(Watch watch, CancellationToken cancellationToken = default)
    {
        _watches.Add(watch);

        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;

        return Task.CompletedTask;
    }
}

internal sealed class FixedAdmissionLimits : IAdmissionLimitProvider
{
    private readonly AdmissionLimits _limits;

    public FixedAdmissionLimits(AdmissionLimits limits) => _limits = limits;

    public Task<AdmissionLimits> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_limits);
}

internal sealed class FixedCycleBudget : ICycleBudgetProvider
{
    private readonly CycleBudget _budget;

    public FixedCycleBudget(CycleBudget budget) => _budget = budget;

    public Task<CycleBudget> GetAsync(string templateName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_budget);
}

internal sealed class InMemoryAutonomyGrantStore : IAutonomyGrantStore
{
    private readonly List<AutonomyGrant> _grants = [];

    public IReadOnlyList<AutonomyGrant> All => _grants;

    public void Seed(AutonomyGrant grant) => _grants.Add(grant);

    public Task<IReadOnlyList<AutonomyGrant>> GetActiveAsync(
        Capability capability,
        string environmentName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AutonomyGrant>>(
            _grants
                .Where(g => g.Capability == capability)
                .Where(g => string.Equals(g.EnvironmentName, environmentName, StringComparison.OrdinalIgnoreCase))
                .Where(g => g.IsActive(nowUtc))
                .ToList());

    public Task<IReadOnlyList<AutonomyGrant>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AutonomyGrant>>(_grants.ToList());

    public Task<AutonomyGrant?> FindAsync(Guid autonomyGrantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_grants.FirstOrDefault(g => g.AutonomyGrantId == autonomyGrantId));

    public Task AddAsync(AutonomyGrant grant, CancellationToken cancellationToken = default)
    {
        _grants.Add(grant);

        return Task.CompletedTask;
    }
}

// ---- The pieces the cycle runner needs, and the harness that wires them -------------------

internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    public int Suppressed { get; private set; }

    public Task<bool> TryClaimAsync(
        string idempotencyKey,
        Guid proposalId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (_claimed.Add(idempotencyKey))
        {
            return Task.FromResult(true);
        }

        Suppressed++;

        return Task.FromResult(false);
    }
}

internal sealed class FakeExecutionStore : IActionExecutionStore
{
    private readonly List<Domain.Actions.ActionExecution> _executions = [];

    public IReadOnlyList<Domain.Actions.ActionExecution> Executions => _executions;

    public Task RecordAsync(
        Domain.Actions.ActionExecution execution,
        CancellationToken cancellationToken = default)
    {
        _executions.Add(execution);

        return Task.CompletedTask;
    }
}

/// <summary>The same window semantics the real one has, without a database behind it.</summary>
internal sealed class TestWriteAuthorization : IWriteAuthorization
{
    private Domain.Actions.PolicyDecision? _current;

    public bool IsAuthorized => _current is not null;

    public Guid? AuthorizingDecisionId => _current?.DecisionId;

    public int WindowsOpened { get; private set; }

    public IDisposable Authorize(Domain.Actions.PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Outcome != PolicyOutcome.Execute)
        {
            throw new InvalidOperationException("Only a decision permitting execution opens a window.");
        }

        _current = decision;
        WindowsOpened++;

        return new Window(this);
    }

    private sealed class Window : IDisposable
    {
        private TestWriteAuthorization? _owner;

        public Window(TestWriteAuthorization owner) => _owner = owner;

        public void Dispose()
        {
            if (_owner is not null)
            {
                _owner._current = null;
                _owner = null;
            }
        }
    }
}

/// <summary>
/// Assembles the context the way the real provider does, including the autonomy in scope.
/// </summary>
/// <remarks>
/// Reading the autonomy context here rather than taking a fixed resolution is the point: it is what
/// makes the tests exercise the same coupling the production provider has, so a runner that forgot
/// to open the scope fails the test rather than passing with a resolution somebody handed it.
/// </remarks>
internal sealed class ContextProvider : IPolicyContextProvider
{
    private readonly IAutonomyContext _autonomy;
    private readonly KillSwitchState _killSwitch;

    public ContextProvider(IAutonomyContext autonomy, KillSwitchState killSwitch = KillSwitchState.Disengaged)
    {
        _autonomy = autonomy;
        _killSwitch = killSwitch;
    }

    public const string EnvironmentName = "Test";

    public Task<Domain.Actions.PolicyContext> GetAsync(CancellationToken cancellationToken = default)
    {
        var policies = Enum.GetValues<Capability>()
            .Select(capability => Domain.Actions.CapabilityPolicy.Create(
                capability,
                enabled: true,
                RiskTier.Critical,
                allowIrreversibleAutoExecute: false,
                allowAiProposers: true))
            .ToList();

        return Task.FromResult(
            Domain.Actions.PolicyContext.Create(EnvironmentName, _killSwitch, policies, _autonomy.Current));
    }
}

internal sealed class FixedLimits : ILimitProvider
{
    private readonly Domain.Limits.LimitSet _limits;

    public FixedLimits(Domain.Limits.LimitSet limits) => _limits = limits;

    public Task<Domain.Limits.LimitSet> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_limits);
}

internal sealed class FlatExposure : IExposureProvider
{
    public Task<Domain.Limits.ExposureSnapshot> GetAsync(
        Currency currency,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Domain.Limits.ExposureSnapshot.Flat(
            currency,
            Money.Create(100_000m, currency)));
}

/// <summary>
/// A plan that does exactly what a test tells it to, and records what the loop asked it for.
/// </summary>
internal sealed class ScriptedWorkPlan : ICycleWorkPlan
{
    private readonly Func<CycleStageContext, CycleStageResult> _stage;
    private readonly List<CycleStage> _stagesRun = [];

    public ScriptedWorkPlan(string templateName, Func<CycleStageContext, CycleStageResult> stage)
    {
        TemplateName = templateName;
        _stage = stage;
    }

    public string TemplateName { get; }

    /// <summary>Settable, so a test can prove the obstacle reaches the escalation.</summary>
    public string Obstacle { get; set; } = string.Empty;

    public IReadOnlyList<CycleStage> StagesRun => _stagesRun;

    public int Executions { get; private set; }

    public Task<CycleStageResult> RunStageAsync(
        CycleStageContext context,
        CancellationToken cancellationToken = default)
    {
        _stagesRun.Add(context.Stage);

        return Task.FromResult(_stage(context));
    }

    public Task<string> ExecuteAsync(
        Domain.Actions.ActionProposal proposal,
        AutonomyResolution autonomy,
        CancellationToken cancellationToken = default)
    {
        Executions++;

        return Task.FromResult($"executed {proposal.ActionType} at {autonomy.Mode}");
    }
}
