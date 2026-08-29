using System.Reflection;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Operators;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The operator surface cannot become a way round the gate it was built beside.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface a person is expected to use, which makes it the one most likely to acquire a
/// shortcut: a convenience method that writes directly, a "force" flag for an incident, an
/// administrative path that skips policy because the caller is trusted. The tests here are the ones
/// that would fail if any of that arrived.
/// </para>
/// <para>
/// Behavioural wherever they can be. The gateway and the policy engine are the real ones; only the
/// things that would reach a database are replaced. A denying engine and a real gateway prove that
/// nothing is written, where a reflection check over a method list would prove only that nobody had
/// renamed anything.
/// </para>
/// </remarks>
public sealed class OperatorSurfaceSafetyTests
{
    private const string OperatorId = "alex@example.test";

    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly RecordingAuditSink _audit = new();
    private readonly InMemoryOpportunityRepository _opportunities = new();
    private readonly OperatorEscalationStore _escalations = new();
    private readonly OperatorWatchStore _watches = new();
    private readonly OperatorKillSwitchAdministration _killSwitch = new();
    private readonly CountingUnitOfWork _unitOfWork = new();

    private Opportunity _opportunity = null!;
    private Escalation _escalation = null!;

    /// <summary>
    /// Every operator action is refused when policy denies, and nothing at all is written.
    /// </summary>
    /// <remarks>
    /// The capability list is empty, which is how this platform denies by default: a capability with
    /// no configured policy is refused. An operator surface that wrote anyway would be the largest
    /// hole the system could acquire, because it is the path a person is meant to use.
    /// </remarks>
    [Fact]
    public async Task Nothing_is_written_when_policy_denies()
    {
        var outcomes = await AllActionsAsync(DenyAll());

        Assert.All(outcomes, outcome =>
            Assert.Equal(OperatorOutcomeStatus.DeniedByPolicy, outcome.Status));

        AssertNothingHappened();
    }

    /// <summary>
    /// The kill switch stops the operator surface too, including the action that engages it.
    /// </summary>
    /// <remarks>
    /// An already-engaged switch denying a second engage is the correct outcome rather than a
    /// defect: the caller wanted the switch on, and it is on. What must not happen is any of the
    /// other operator actions proceeding while everything else in the platform has stopped.
    /// </remarks>
    [Theory]
    [InlineData(KillSwitchState.Engaged)]
    [InlineData(KillSwitchState.Unknown)]
    public async Task Nothing_is_written_when_the_kill_switch_is_engaged_or_unreadable(
        KillSwitchState state)
    {
        var outcomes = await AllActionsAsync(PermitAll(state));

        Assert.All(outcomes, outcome =>
            Assert.Equal(OperatorOutcomeStatus.DeniedByPolicy, outcome.Status));

        AssertNothingHappened();
    }

    /// <summary>
    /// Every denial is recorded, under the operator's own name.
    /// </summary>
    /// <remarks>
    /// A refusal nobody recorded is a refusal nobody can review, and an operator surface whose
    /// denials were anonymous would defeat the reason it exists.
    /// </remarks>
    [Fact]
    public async Task Every_denial_is_recorded_against_the_operator_who_asked()
    {
        var outcomes = await AllActionsAsync(DenyAll());

        var denials = _audit.Records
            .Where(record => record.EventType == AuditEventType.ActionDenied)
            .ToList();

        Assert.Equal(outcomes.Count, denials.Count);
        Assert.DoesNotContain(_audit.Records, r => r.EventType == AuditEventType.ActionExecuted);

        Assert.All(denials, record =>
        {
            Assert.Equal(OperatorId, record.Actor);
            Assert.Equal(ProposerKind.Human, record.ActorKind);
        });
    }

    /// <summary>The permitted path, so the refusals above are known to be refusals.</summary>
    [Fact]
    public async Task The_same_actions_succeed_when_policy_permits()
    {
        var outcomes = await AllActionsAsync(PermitAll(KillSwitchState.Disengaged));

        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));

        Assert.Equal(OpportunityStatus.Rejected, _opportunity.Status);
        Assert.True(_escalation.IsResolved);
        Assert.True(_killSwitch.Engaged);
        Assert.Single(_watches.Added);
    }

    // ---- what the surface must never grow ------------------------------------------------------

    /// <summary>
    /// There is no approve. An approval token binds to the identity of the exact proposal a person
    /// was shown, and proposals are not persisted; an approve here would either refuse every token
    /// or would have to loosen the binding that makes a token mean anything.
    /// </summary>
    [Fact]
    public void The_operator_console_cannot_approve_anything()
    {
        Assert.DoesNotContain(
            PublicMethodNames(typeof(OperatorConsole)),
            name => name.Contains("Approve", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// There is no disengage, on the console or on the port beneath it.
    /// </summary>
    /// <remarks>
    /// The policy engine denies every action while the switch is engaged, so a disengage proposal
    /// would be refused by the state it exists to clear - and the only implementation that would
    /// work is one that bypassed the gate. A bypass whose purpose is turning the kill switch off is
    /// the last thing this platform should own.
    /// </remarks>
    [Theory]
    [InlineData("Disengage")]
    [InlineData("Clear")]
    [InlineData("Reset")]
    [InlineData("Force")]
    [InlineData("Override")]
    public void Neither_the_console_nor_the_kill_switch_port_can_undo_a_stop(string forbidden)
    {
        Assert.DoesNotContain(
            PublicMethodNames(typeof(OperatorConsole)),
            name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            PublicMethodNames(typeof(IKillSwitchAdministration)),
            name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every public entry point answers with an outcome rather than with domain state.
    /// </summary>
    /// <remarks>
    /// A method that handed a caller a tracked aggregate back would be a method through which a
    /// controller could mutate one outside the seam. Returning a value object closes that off by
    /// construction rather than by review.
    /// </remarks>
    [Fact]
    public void Every_operator_action_answers_with_an_outcome()
    {
        var methods = typeof(OperatorConsole)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.Equal(typeof(Task<OperatorOutcome>), method.ReturnType));
    }

    /// <summary>
    /// An anonymous caller is refused before anything is proposed, whatever policy would have said.
    /// </summary>
    [Fact]
    public async Task An_anonymous_caller_reaches_neither_the_gate_nor_a_store()
    {
        Seed();

        var console = Console(PermitAll(KillSwitchState.Disengaged), identity: null);

        var outcome = await console.RejectOpportunityAsync(
            _opportunity.OpportunityId.Value,
            "no thanks");

        Assert.Equal(OperatorOutcomeStatus.NotAuthenticated, outcome.Status);
        Assert.Empty(_audit.Records);
        AssertNothingHappened();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static IEnumerable<string> PublicMethodNames(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.Name);

    private static PolicyContext DenyAll() =>
        PolicyContext.Create("Test", KillSwitchState.Disengaged, []);

    private static PolicyContext PermitAll(KillSwitchState killSwitch) =>
        PolicyContext.Create(
            "Test",
            killSwitch,
            Enum.GetValues<Capability>()
                .Select(capability => CapabilityPolicy.Create(
                    capability,
                    enabled: true,
                    RiskTier.Critical,
                    allowIrreversibleAutoExecute: false,
                    allowAiProposers: true))
                .ToList());

    private async Task<List<OperatorOutcome>> AllActionsAsync(PolicyContext context)
    {
        Seed();

        var console = Console(context, SignedIn());

        return
        [
            await console.RejectOpportunityAsync(_opportunity.OpportunityId.Value, "no thanks"),
            await console.ResolveEscalationAsync(_escalation.EscalationId, "dealt with"),
            await console.EngageKillSwitchAsync("everything must stop"),
            await console.CreateScheduledWatchAsync(new ScheduledWatchDefinition(
                "AAPL price review", "Security", "AAPL",
                TimeSpan.FromHours(6), TimeSpan.FromHours(1),
                Capability.OpportunityManagement, "equity-price-review")),
        ];
    }

    private void AssertNothingHappened()
    {
        Assert.NotEqual(OpportunityStatus.Rejected, _opportunity.Status);
        Assert.False(_escalation.IsResolved);
        Assert.False(_escalation.IsAcknowledged);
        Assert.False(_killSwitch.Engaged);
        Assert.Empty(_watches.Added);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    private static OperatorIdentity SignedIn() =>
        OperatorIdentity.Create(
            OperatorId,
            "Alex",
            Enum.GetValues<OperatorPrivilege>()
                .Where(privilege => privilege != OperatorPrivilege.None));

    private OperatorConsole Console(PolicyContext context, OperatorIdentity? identity)
    {
        var gateway = new ActionGateway(
            new PolicyEngine(),
            new FakePolicyContextProvider(context),
            _audit,
            new RecordingIdempotencyStore(),
            new RecordingExecutionStore(),
            new TrackingWriteAuthorization(),
            new FakeClock(Now));

        return new OperatorConsole(
            gateway,
            new StubbedOperatorContext(identity),
            new FixedCorrelationContext(),
            _opportunities,
            _escalations,
            _watches,
            _killSwitch,
            _unitOfWork,
            new FakeClock(Now));
    }

    private void Seed()
    {
        // Ranked rather than approved: an operator refusing a candidate is refusing one the
        // platform put forward, not unwinding one somebody already permitted.
        _opportunity = Phase5Fixtures.Draft(Now);

        _opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(_opportunity, Now),
            OpportunityRisk.Create(
                "A single-name equity position carries issuer and market risk.",
                ReversibilityClass.ReversibleWithCost,
                [ClaimId.New()]),
            Confidence.Create(0.7m),
            Now);

        _opportunity.Rank(Phase5Fixtures.Score(Now), Now);

        _opportunities.AddAsync(_opportunity).GetAwaiter().GetResult();

        _escalation = Escalation.Raise(
            Capability.Analysis, EscalationReason.ProviderFailure, "A provider failed.",
            Now, TimeSpan.FromHours(24));

        _escalations.Seed(_escalation);
    }
}

/// <summary>An operator context that answers with whatever the test signed in as, including nobody.</summary>
internal sealed class StubbedOperatorContext : IOperatorContext
{
    public StubbedOperatorContext(OperatorIdentity? identity) => Current = identity;

    public OperatorIdentity? Current { get; }
}

/// <summary>An escalation store with no database behind it.</summary>
internal sealed class OperatorEscalationStore : IEscalationStore
{
    private readonly List<Escalation> _escalations = [];

    public int Saves { get; private set; }

    public void Seed(Escalation escalation) => _escalations.Add(escalation);

    public Task AddAsync(Escalation escalation, CancellationToken cancellationToken = default)
    {
        _escalations.Add(escalation);

        return Task.CompletedTask;
    }

    public Task<Escalation?> FindAsync(Guid escalationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_escalations.FirstOrDefault(e => e.EscalationId == escalationId));

    public Task<IReadOnlyList<Escalation>> GetOutstandingAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Escalation>>(_escalations.Where(e => !e.IsResolved).ToList());

    public Task<int> CountUnhandledAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_escalations.Count(e => e.IsUnhandled(nowUtc)));

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Saves++;

        return Task.CompletedTask;
    }
}

/// <summary>A watch store that records what it was asked to add.</summary>
internal sealed class OperatorWatchStore : IWatchStore
{
    public List<Watch> Added { get; } = [];

    public Task<IReadOnlyList<Watch>> GetEnabledAsync(
        TriggerType triggerType,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Watch>>(
            Added.Where(w => w.TriggerType == triggerType && w.Enabled).ToList());

    public Task<IReadOnlyList<Watch>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Watch>>(Added.ToList());

    public Task<Watch?> FindAsync(Guid watchId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Added.FirstOrDefault(w => w.WatchId == watchId));

    public Task AddAsync(Watch watch, CancellationToken cancellationToken = default)
    {
        Added.Add(watch);

        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// A kill-switch administration that records what it engaged.
/// </summary>
/// <remarks>
/// It has no disengage, which is the point: the interface it implements has none either, and a
/// double that invented one would be testing a system that does not exist.
/// </remarks>
internal sealed class OperatorKillSwitchAdministration : IKillSwitchAdministration
{
    public bool Engaged { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Task EngageAsync(
        Capability? capability,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        Engaged = true;
        Reason = reason;

        return Task.CompletedTask;
    }
}
