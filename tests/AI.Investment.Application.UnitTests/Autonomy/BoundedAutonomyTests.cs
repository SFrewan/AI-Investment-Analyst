using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Autonomy;
using AI.Investment.Application.Operations;
using AI.Investment.Application.UnitTests.Operations;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using Xunit;

namespace AI.Investment.Application.UnitTests.Autonomy;

/// <summary>
/// The production path to a grant, and the breaker that takes one away again.
/// </summary>
/// <remarks>
/// The domain tests establish that a warrant cannot be built without evidence. These establish the
/// half that matters operationally: that the one service which writes grants refuses to write an
/// unattended one without a warrant, and that the breaker lowers a grant on its own when the
/// conditions it was written under stop holding.
/// </remarks>
public sealed class BoundedAutonomyTests
{
    private static readonly DateTime Now = JustifiedEvidence.Now;

    private readonly InMemoryAutonomyGrantStore _grants = new();
    private readonly InMemoryPromotionWarrantStore _warrants = new();
    private readonly InMemoryEscalationStore _escalations = new();
    private readonly RecordingAuditSink _audit = new();
    private readonly FakeClock _clock = new(Now);
    private readonly TestWriteAuthorization _writes = new();
    private readonly AutonomyContext _autonomy = new();

    private AutonomyAdministration Administration()
    {
        var contextProvider = new ContextProvider(_autonomy);

        var gateway = new ActionGateway(
            new PolicyEngine(),
            contextProvider,
            _audit,
            new FakeIdempotencyStore(),
            new FakeExecutionStore(),
            _writes,
            _clock);

        return new AutonomyAdministration(
            gateway,
            _grants,
            _warrants,
            new NoOpUnitOfWork(),
            _audit,
            new FakeCorrelationContext(),
            _clock);
    }

    // ---- the production gate -----------------------------------------------------------------------

    /// <summary>
    /// The refusal this whole phase exists to produce, on the only path that writes a grant.
    /// </summary>
    [Fact]
    public async Task A_grant_of_unattended_execution_without_a_warrant_is_denied()
    {
        var outcome = await Administration().GrantAsync(
            Parameters(AutonomyMode.AutoExecuteBounded, warrantId: null),
            "operator@example.test");

        Assert.False(outcome.Succeeded);
        Assert.Equal(ActionOutcomeStatus.Denied, outcome.Status);
        Assert.Contains("requires a promotion warrant", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(_grants.All);
    }

    [Fact]
    public async Task A_grant_naming_a_warrant_that_does_not_exist_is_denied()
    {
        var outcome = await Administration().GrantAsync(
            Parameters(AutonomyMode.AutoExecuteBounded, warrantId: Guid.NewGuid()),
            "operator@example.test");

        Assert.False(outcome.Succeeded);
        Assert.Contains("no promotion warrant", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(_grants.All);
    }

    [Fact]
    public async Task A_grant_wider_than_its_warrant_is_denied()
    {
        var warrant = JustifiedEvidence.Warrant(maxExposure: 1_000m);

        _warrants.Seed(warrant);

        var outcome = await Administration().GrantAsync(
            Parameters(AutonomyMode.AutoExecuteBounded, warrant.PromotionWarrantId, maxExposure: 50_000m),
            "operator@example.test");

        Assert.False(outcome.Succeeded);
        Assert.Contains("permits at most", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(_grants.All);
    }

    /// <summary>An attended grant needs no warrant, and is written as before.</summary>
    [Fact]
    public async Task An_attended_grant_is_written_without_a_warrant()
    {
        var outcome = await Administration().GrantAsync(
            Parameters(AutonomyMode.PrepareForApproval, warrantId: null),
            "operator@example.test");

        Assert.True(outcome.Succeeded);

        var grant = Assert.Single(_grants.All);

        Assert.Equal(AutonomyMode.PrepareForApproval, grant.GrantedMode);
        Assert.Null(grant.PromotionWarrantId);
    }

    /// <summary>The permitted path, so the gate is known to be passable.</summary>
    [Fact]
    public async Task A_grant_covered_by_a_warrant_is_written_and_records_it()
    {
        var warrant = JustifiedEvidence.Warrant(maxExposure: 5_000m);

        _warrants.Seed(warrant);

        var outcome = await Administration().GrantAsync(
            Parameters(AutonomyMode.AutoExecuteBounded, warrant.PromotionWarrantId, maxExposure: 1_000m),
            "operator@example.test");

        Assert.True(outcome.Succeeded);

        var grant = Assert.Single(_grants.All);

        Assert.Equal(AutonomyMode.AutoExecuteBounded, grant.GrantedMode);
        Assert.Equal(warrant.PromotionWarrantId, grant.PromotionWarrantId);
        Assert.Equal(1, _audit.CountOf(AuditEventType.AutonomyGranted));
    }

    // ---- the circuit breaker -------------------------------------------------------------------------

    /// <summary>
    /// Fail closed. Signals nobody counts arrive as unknown, and unknown lowers the grant rather than
    /// leaving it - which is why an unattended grant does not survive a sweep on this platform today.
    /// </summary>
    [Fact]
    public async Task An_unattended_grant_is_demoted_when_the_signals_cannot_be_read()
    {
        var warrant = JustifiedEvidence.Warrant();

        _warrants.Seed(warrant);
        _grants.Seed(Bounded(warrant));

        var sweep = await Breaker(KillSwitchState.Disengaged).SweepAsync();

        Assert.Equal(1, sweep.Examined);
        Assert.Equal(1, sweep.Demoted);
        Assert.Equal(DemotionTrigger.StateUnknown, Assert.Single(sweep.Triggers));
        Assert.Equal(AutonomyMode.PrepareForApproval, _grants.All[0].EffectiveMode);

        // And the record says what was permitted as well as what is now in force.
        Assert.Equal(AutonomyMode.AutoExecuteBounded, _grants.All[0].GrantedMode);
        Assert.Equal(1, _audit.CountOf(AuditEventType.AutonomyDemoted));
    }

    /// <summary>
    /// Attended grants are left alone. Demoting them on a transient signal would turn a platform that
    /// asks permission into one that has quietly stopped proposing anything.
    /// </summary>
    [Fact]
    public async Task An_attended_grant_is_not_touched_by_the_breaker()
    {
        _grants.Seed(AutonomyGrant.Issue(
            Capability.SimulatedExecution, null, "Test", AutonomyMode.PrepareForApproval,
            RiskTier.Low, JustifiedEvidence.Usd(1_000m), "limits.default", "operator@example.test",
            Now, TimeSpan.FromDays(7)));

        var sweep = await Breaker(KillSwitchState.Engaged).SweepAsync();

        Assert.Equal(0, sweep.Examined);
        Assert.Equal(0, sweep.Demoted);
        Assert.False(sweep.AnythingChanged);
        Assert.Equal(AutonomyMode.PrepareForApproval, _grants.All[0].EffectiveMode);
    }

    /// <summary>
    /// An unattended grant with no warrant behind it is exactly what the gate exists to prevent, so
    /// meeting one is a reason to lower it rather than to leave it alone.
    /// </summary>
    [Fact]
    public async Task An_unattended_grant_with_no_warrant_is_demoted()
    {
        // Written through the domain factory rather than the service, which is the only way such a
        // grant can exist at all - and the breaker's job is to find one if it ever does.
        _grants.Seed(AutonomyGrant.Issue(
            Capability.SimulatedExecution, null, "Test", AutonomyMode.AutoExecuteBounded,
            RiskTier.Low, JustifiedEvidence.Usd(1_000m), "limits.default", "operator@example.test",
            Now, TimeSpan.FromDays(7)));

        var sweep = await Breaker(KillSwitchState.Disengaged).SweepAsync();

        Assert.Equal(1, sweep.Demoted);
        Assert.Equal(AutonomyMode.PrepareForApproval, _grants.All[0].EffectiveMode);
    }

    /// <summary>The breaker never raises anything, whatever the signals say.</summary>
    [Fact]
    public async Task The_breaker_only_ever_lowers()
    {
        var warrant = JustifiedEvidence.Warrant();

        _warrants.Seed(warrant);

        var grant = Bounded(warrant);

        grant.Demote("earlier breach", Now);
        _grants.Seed(grant);

        var before = grant.EffectiveMode;

        await Breaker(KillSwitchState.Disengaged).SweepAsync();

        Assert.True(_grants.All[0].EffectiveMode <= before);
        Assert.Null(typeof(AutonomyCircuitBreaker).GetMethod("Promote"));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private AutonomyCircuitBreaker Breaker(KillSwitchState state) =>
        new(_grants, _warrants, _escalations, new FixedKillSwitch(state), Administration(), _clock);

    private static AutonomyGrant Bounded(PromotionWarrant warrant) =>
        AutonomyGrant.IssueBounded(
            warrant, null, "Test", AutonomyMode.AutoExecuteBounded, RiskTier.Low,
            JustifiedEvidence.Usd(1_000m), "limits.default", "operator@example.test",
            Now, TimeSpan.FromDays(7));

    private static AutonomyGrantParameters Parameters(
        AutonomyMode mode,
        Guid? warrantId,
        decimal maxExposure = 1_000m) =>
        new(
            Capability.SimulatedExecution,
            null,
            ContextProvider.EnvironmentName,
            mode,
            RiskTier.Low,
            JustifiedEvidence.Usd(maxExposure),
            "limits.default",
            TimeSpan.FromDays(7),
            warrantId);
}

/// <summary>A warrant store a test can put warrants in.</summary>
internal sealed class InMemoryPromotionWarrantStore : IPromotionWarrantStore
{
    private readonly List<PromotionWarrant> _warrants = [];

    public void Seed(PromotionWarrant warrant) => _warrants.Add(warrant);

    public Task AddAsync(PromotionWarrant warrant, CancellationToken cancellationToken = default)
    {
        _warrants.Add(warrant);

        return Task.CompletedTask;
    }

    public Task<PromotionWarrant?> FindAsync(Guid promotionWarrantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_warrants.FirstOrDefault(w => w.PromotionWarrantId == promotionWarrantId));

    public Task<IReadOnlyList<PromotionWarrant>> GetActiveAsync(
        Capability capability,
        string environmentName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PromotionWarrant>>(
            _warrants
                .Where(w => w.Capability == capability)
                .Where(w => string.Equals(w.EnvironmentName, environmentName, StringComparison.OrdinalIgnoreCase))
                .Where(w => w.IsActive(nowUtc))
                .ToList());

    public Task<IReadOnlyList<PromotionWarrant>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PromotionWarrant>>(_warrants.ToList());
}

/// <summary>A kill switch that answers whatever a test set it to.</summary>
internal sealed class FixedKillSwitch : IKillSwitch
{
    private readonly KillSwitchState _state;

    public FixedKillSwitch(KillSwitchState state) => _state = state;

    public Task<KillSwitchState> ReadAsync(
        Capability? capability = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_state);
}

/// <summary>A unit of work with no database behind it.</summary>
internal sealed class NoOpUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;

        return Task.FromResult(0);
    }
}
