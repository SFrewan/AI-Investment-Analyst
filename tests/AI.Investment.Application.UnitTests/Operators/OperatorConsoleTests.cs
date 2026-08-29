using System.Reflection;

using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Operations;
using AI.Investment.Application.Operators;
using AI.Investment.Application.UnitTests.Autonomy;
using AI.Investment.Application.UnitTests.Operations;
using AI.Investment.Application.UnitTests.Opportunities;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Domain.Watching;
using Xunit;

namespace AI.Investment.Application.UnitTests.Operators;

/// <summary>
/// The operator surface: who may do what, and what happens to the record when they do.
/// </summary>
/// <remarks>
/// <para>
/// The assertions that matter are the identity ones. Phase 6 refused to expose these operations
/// because "an endpoint that resolved an escalation without knowing who was calling would make the
/// record of who decided a fiction" - so the tests that earn this surface are the ones proving the
/// caller's own identifier reaches the audit trail and the escalation, and that an anonymous caller
/// gets nothing at all.
/// </para>
/// <para>
/// The gateway here is the real one, wired to the real <c>PolicyEngine</c>. A double would agree
/// with whatever the console was written to do; the point is that these actions go through the same
/// gate as everything else.
/// </para>
/// </remarks>
public sealed class OperatorConsoleTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private const string OperatorId = "alex@example.test";

    private readonly RecordingAuditSink _audit = new();
    private readonly FakeClock _clock = new(Now);
    private readonly TestWriteAuthorization _writes = new();
    private readonly AutonomyContext _autonomy = new();
    private readonly RecordingOpportunityRepository _opportunities = new();
    private readonly InMemoryEscalationStore _escalations = new();
    private readonly InMemoryWatchStore _watches = new();
    private readonly RecordingKillSwitchAdministration _killSwitch = new();
    private readonly NoOpUnitOfWork _unitOfWork = new();
    private readonly StubOperatorContext _operators = new();

    // ---- authentication and authorization -------------------------------------------------

    [Fact]
    public async Task An_anonymous_caller_is_refused_and_nothing_is_proposed()
    {
        _operators.Current = null;

        var opportunity = await StoredOpportunityAsync();

        var outcome = await Console().RejectOpportunityAsync(opportunity, "no thanks");

        Assert.Equal(OperatorOutcomeStatus.NotAuthenticated, outcome.Status);
        Assert.Empty(_audit.Records);
        Assert.Equal(OpportunityStatus.Ranked, _opportunities.All[0].Status);
    }

    [Fact]
    public async Task An_operator_without_the_privilege_is_refused_and_nothing_is_proposed()
    {
        SignIn(OperatorPrivilege.AnswerEscalations);

        var opportunity = await StoredOpportunityAsync();

        var outcome = await Console().RejectOpportunityAsync(opportunity, "no thanks");

        Assert.Equal(OperatorOutcomeStatus.NotPermitted, outcome.Status);
        Assert.Contains("DecideOpportunities", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(_audit.Records);
    }

    /// <summary>
    /// The two refusals stay distinct. A privilege problem that looked like a login problem sends
    /// somebody to re-enter a key that was fine.
    /// </summary>
    [Fact]
    public async Task Not_authenticated_and_not_permitted_are_different_answers()
    {
        _operators.Current = null;
        var anonymous = await Console().EngageKillSwitchAsync("stop");

        SignIn(OperatorPrivilege.DecideOpportunities);
        var unprivileged = await Console().EngageKillSwitchAsync("stop");

        Assert.Equal(OperatorOutcomeStatus.NotAuthenticated, anonymous.Status);
        Assert.Equal(OperatorOutcomeStatus.NotPermitted, unprivileged.Status);
        Assert.False(_killSwitch.Engaged);
    }

    // ---- the identity in the record --------------------------------------------------------

    /// <summary>The whole reason this surface exists: the record says who decided.</summary>
    [Fact]
    public async Task Every_action_is_proposed_by_the_operator_who_asked()
    {
        SignIn(OperatorPrivilege.DecideOpportunities);

        var opportunity = await StoredOpportunityAsync();

        var outcome = await Console().RejectOpportunityAsync(opportunity, "the thesis does not hold");

        Assert.True(outcome.Succeeded);

        var record = Assert.Single(
            _audit.Records,
            r => r.EventType == AuditEventType.ActionExecuted);

        Assert.Equal(OperatorId, record.Actor);
        Assert.Equal(ProposerKind.Human, record.ActorKind);
        Assert.Equal(Capability.OpportunityManagement, record.Capability);
        Assert.Equal(OperatorActionTypes.RejectOpportunity.Value, record.ActionType);
    }

    [Fact]
    public async Task Rejecting_records_the_reason_and_moves_the_opportunity()
    {
        SignIn(OperatorPrivilege.DecideOpportunities);

        var opportunity = await StoredOpportunityAsync();

        var outcome = await Console().RejectOpportunityAsync(opportunity, "the thesis does not hold");

        Assert.True(outcome.Succeeded);
        Assert.Equal(OpportunityStatus.Rejected, _opportunities.All[0].Status);
        Assert.Contains("thesis", _opportunities.All[0].Resolution!, StringComparison.Ordinal);
        Assert.Equal(1, _unitOfWork.SaveCount);
    }

    [Fact]
    public async Task An_escalation_is_answered_under_the_operators_own_name()
    {
        SignIn(OperatorPrivilege.AnswerEscalations);

        var escalation = Escalation.Raise(
            Capability.OpportunityManagement,
            EscalationReason.NoAutonomyGrant,
            "A proposal needs a person.",
            Now,
            TimeSpan.FromHours(24));

        await _escalations.AddAsync(escalation);

        var acknowledged = await Console().AcknowledgeEscalationAsync(escalation.EscalationId);
        var resolved = await Console().ResolveEscalationAsync(
            escalation.EscalationId,
            "Reviewed and declined.");

        Assert.True(acknowledged.Succeeded);
        Assert.True(resolved.Succeeded);
        Assert.Equal(OperatorId, escalation.AcknowledgedBy);
        // The domain stamps the resolver's name onto the text itself, which is the record this
        // surface exists to make true.
        Assert.Equal($"{OperatorId}: Reviewed and declined.", escalation.Resolution);
        Assert.True(escalation.IsResolved);
    }

    // ---- what it refuses --------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_rejection_without_a_reason_is_refused(string reason)
    {
        SignIn(OperatorPrivilege.DecideOpportunities);

        var opportunity = await StoredOpportunityAsync();

        var outcome = await Console().RejectOpportunityAsync(opportunity, reason);

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.Equal(OpportunityStatus.Ranked, _opportunities.All[0].Status);
        Assert.Empty(_audit.Records);
    }

    [Fact]
    public async Task An_opportunity_that_no_longer_exists_is_not_found()
    {
        SignIn(OperatorPrivilege.DecideOpportunities);

        var outcome = await Console().RejectOpportunityAsync(Guid.NewGuid(), "no thanks");

        Assert.Equal(OperatorOutcomeStatus.NotFound, outcome.Status);
        Assert.Empty(_audit.Records);
    }

    [Fact]
    public async Task An_opportunity_already_in_a_terminal_state_is_refused()
    {
        SignIn(OperatorPrivilege.DecideOpportunities);

        var opportunity = await StoredOpportunityAsync();

        await Console().RejectOpportunityAsync(opportunity, "the thesis does not hold");

        var second = await Console().RejectOpportunityAsync(opportunity, "again");

        Assert.Equal(OperatorOutcomeStatus.Refused, second.Status);
        Assert.Contains("Rejected", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_an_escalation_without_saying_what_was_done_is_refused()
    {
        SignIn(OperatorPrivilege.AnswerEscalations);

        var escalation = Escalation.Raise(
            Capability.Analysis, EscalationReason.ProviderFailure, "A provider failed.",
            Now, TimeSpan.FromHours(24));

        await _escalations.AddAsync(escalation);

        var outcome = await Console().ResolveEscalationAsync(escalation.EscalationId, "  ");

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.False(escalation.IsResolved);
    }

    [Fact]
    public async Task Engaging_the_kill_switch_without_a_reason_is_refused()
    {
        SignIn(OperatorPrivilege.AdministerKillSwitch);

        var outcome = await Console().EngageKillSwitchAsync("   ");

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.False(_killSwitch.Engaged);
    }

    [Fact]
    public async Task Engaging_the_kill_switch_records_the_reason()
    {
        SignIn(OperatorPrivilege.AdministerKillSwitch);

        var outcome = await Console().EngageKillSwitchAsync("the provider is returning nonsense");

        Assert.True(outcome.Succeeded);
        Assert.True(_killSwitch.Engaged);
        Assert.Contains("nonsense", _killSwitch.Reason, StringComparison.Ordinal);
        Assert.Null(_killSwitch.Capability);
    }

    // ---- watches -----------------------------------------------------------------------------

    [Fact]
    public async Task A_scheduled_watch_is_created_and_points_at_the_instrument()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        var outcome = await Console().CreateScheduledWatchAsync(Definition());

        Assert.True(outcome.Succeeded);

        var watch = Assert.Single(await _watches.GetAllAsync());

        Assert.Equal("Security", watch.Target.Kind);
        Assert.Equal("AAPL", watch.Target.Identifier);
        Assert.Equal(TriggerType.Schedule, watch.TriggerType);
        Assert.Equal("equity-price-review", watch.CycleTemplate);
    }

    /// <summary>
    /// The domain's own refusals are reported rather than proposed. Proposing an action that cannot
    /// be built would audit an intention that never existed.
    /// </summary>
    [Fact]
    public async Task A_watch_the_domain_refuses_is_reported_rather_than_proposed()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        var outcome = await Console().CreateScheduledWatchAsync(
            Definition() with { Cooldown = TimeSpan.Zero });

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.Empty(await _watches.GetAllAsync());
        Assert.Empty(_audit.Records);
    }

    // ---- watch disablement: the reversal that makes creation safe ---------------------------

    /// <summary>
    /// The test this whole block exists for. Creating a watch was a one-way door until a person
    /// could take it back, and taking it back has to reach the path that actually evaluates it.
    /// </summary>
    [Fact]
    public async Task A_disabled_watch_is_no_longer_offered_to_the_trigger_evaluator()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());

        var watch = Assert.Single(await _watches.GetAllAsync());

        Assert.Single(await _watches.GetEnabledAsync(TriggerType.Schedule));

        var outcome = await Console().DisableScheduledWatchAsync(
            watch.WatchId,
            "the vendor is restating prices");

        Assert.True(outcome.Succeeded);
        Assert.False(watch.Enabled);
        Assert.Contains("restating", watch.DisabledReason!, StringComparison.Ordinal);
        Assert.Empty(await _watches.GetEnabledAsync(TriggerType.Schedule));

        // Disabled, not deleted: the row and its history survive so the record of what ran stays
        // answerable.
        Assert.Single(await _watches.GetAllAsync());
    }

    [Fact]
    public async Task Disabling_a_watch_is_audited_under_the_operators_own_name()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var outcome = await Console().DisableScheduledWatchAsync(watch.WatchId, "stepping back");

        Assert.True(outcome.Succeeded);

        // Filtered by action type rather than cleared: the create above audits too, and a double
        // that could be emptied mid-test would let an assertion pass for the wrong reason.
        var record = Assert.Single(
            _audit.Records,
            r => r.EventType == AuditEventType.ActionExecuted &&
                 r.ActionType == OperatorActionTypes.DisableWatch.Value);

        Assert.Equal(OperatorId, record.Actor);
        Assert.Equal(ProposerKind.Human, record.ActorKind);
        Assert.Equal(Capability.ReferenceDataManagement, record.Capability);
        Assert.Equal(OperatorActionTypes.DisableWatch.Value, record.ActionType);
    }

    /// <summary>
    /// The write goes through the seam, not around it: one authorisation window is opened for it,
    /// and the store is saved exactly once inside that window.
    /// </summary>
    [Fact]
    public async Task Disabling_a_watch_persists_inside_one_authorisation_window()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var savesBefore = _watches.SaveCount;
        var windowsBefore = _writes.WindowsOpened;

        var outcome = await Console().DisableScheduledWatchAsync(watch.WatchId, "stepping back");

        Assert.True(outcome.Succeeded);
        Assert.Equal(savesBefore + 1, _watches.SaveCount);
        Assert.Equal(windowsBefore + 1, _writes.WindowsOpened);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_disable_a_watch()
    {
        SignIn(OperatorPrivilege.AdministerWatches);
        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        _operators.Current = null;

        var outcome = await Console().DisableScheduledWatchAsync(watch.WatchId, "no");

        Assert.Equal(OperatorOutcomeStatus.NotAuthenticated, outcome.Status);
        Assert.True(watch.Enabled);
        Assert.DoesNotContain(
            _audit.Records,
            r => r.ActionType == OperatorActionTypes.DisableWatch.Value);
    }

    [Fact]
    public async Task An_operator_without_AdministerWatches_cannot_disable_a_watch()
    {
        SignIn(OperatorPrivilege.AdministerWatches);
        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        SignIn(OperatorPrivilege.AnswerEscalations);

        var outcome = await Console().DisableScheduledWatchAsync(watch.WatchId, "no");

        Assert.Equal(OperatorOutcomeStatus.NotPermitted, outcome.Status);
        Assert.Contains("AdministerWatches", outcome.Reason, StringComparison.Ordinal);
        Assert.True(watch.Enabled);
        Assert.DoesNotContain(
            _audit.Records,
            r => r.ActionType == OperatorActionTypes.DisableWatch.Value);
    }

    [Fact]
    public async Task A_watch_that_does_not_exist_is_not_found_and_nothing_is_proposed()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        var outcome = await Console().DisableScheduledWatchAsync(Guid.NewGuid(), "stepping back");

        Assert.Equal(OperatorOutcomeStatus.NotFound, outcome.Status);
        Assert.Empty(_audit.Records);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Disabling_a_watch_without_a_reason_is_refused(string reason)
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var outcome = await Console().DisableScheduledWatchAsync(watch.WatchId, reason);

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.True(watch.Enabled);
        Assert.DoesNotContain(
            _audit.Records,
            r => r.ActionType == OperatorActionTypes.DisableWatch.Value);
    }

    /// <summary>
    /// Idempotent, and specifically not re-enabling. The second caller must not overwrite the
    /// reason the first one recorded, and must not switch anything back on.
    /// </summary>
    [Fact]
    public async Task Disabling_an_already_disabled_watch_changes_nothing_and_cannot_re_enable_it()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var first = await Console().DisableScheduledWatchAsync(watch.WatchId, "the original reason");

        Assert.True(first.Succeeded);

        var savesAfterFirst = _watches.SaveCount;

        var second = await Console().DisableScheduledWatchAsync(watch.WatchId, "a different reason");

        Assert.Equal(OperatorOutcomeStatus.DuplicateSuppressed, second.Status);
        Assert.False(watch.Enabled);
        Assert.Contains("original", watch.DisabledReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("different", watch.DisabledReason!, StringComparison.Ordinal);
        Assert.Equal(savesAfterFirst, _watches.SaveCount);
    }

    /// <summary>
    /// There is no delete, and this asserts the absence rather than trusting it. A delete would
    /// take the record of what ran with it.
    /// </summary>
    [Fact]
    public void The_operator_console_offers_no_way_to_delete_a_watch()
    {
        var methods = typeof(OperatorConsole)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(methods, name =>
            name.Contains("Delete", StringComparison.Ordinal) ||
            name.Contains("Remove", StringComparison.Ordinal) ||
            name.Contains("Enable", StringComparison.Ordinal));

        Assert.Contains("DisableScheduledWatchAsync", methods, StringComparer.Ordinal);
    }

    /// <summary>
    /// No provider, no HTTP client, no host configuration, no cycle store. Disabling a watch cannot
    /// call EODHD, cannot start a cycle and cannot reach OperationsHost:RunCycles, because the
    /// console is not given anything that could.
    /// </summary>
    [Fact]
    public void The_operator_console_cannot_reach_a_provider_the_network_or_host_configuration()
    {
        var dependencies = typeof(OperatorConsole)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.DoesNotContain(dependencies, name =>
            name.Contains("Provider", StringComparison.Ordinal) ||
            name.Contains("Http", StringComparison.Ordinal) ||
            name.Contains("Options", StringComparison.Ordinal) ||
            name.Contains("Configuration", StringComparison.Ordinal) ||
            name.Contains("Cycle", StringComparison.Ordinal));
    }

    // ---- rescheduling: how often, revised deliberately ----------------------------------------

    [Fact]
    public async Task Rescheduling_changes_only_the_interval()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var createdAt = watch.CreatedAtUtc;
        var cooldown = watch.Cooldown;
        var savesBefore = _watches.SaveCount;

        var outcome = await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.FromMinutes(5), "proving the pipeline without waiting a day");

        Assert.True(outcome.Succeeded);
        Assert.Equal(TimeSpan.FromMinutes(5), watch.Condition.Interval);

        // Everything else is exactly as it was. Rewriting the history would forge the record of
        // what ran; rewriting the cooldown would remove the storm bound by accident.
        Assert.Equal(createdAt, watch.CreatedAtUtc);
        Assert.Null(watch.LastFiredAtUtc);
        Assert.Equal(0, watch.FireCount);
        Assert.Equal(cooldown, watch.Cooldown);
        Assert.True(watch.Enabled);
        Assert.Equal("Security", watch.Target.Kind);
        Assert.Equal(TriggerType.Schedule, watch.TriggerType);
        Assert.Equal("equity-price-review", watch.CycleTemplate);

        Assert.Equal(savesBefore + 1, _watches.SaveCount);
    }

    /// <summary>
    /// The property that makes a temporary schedule safe: putting it back restores it completely,
    /// and both directions are audited.
    /// </summary>
    [Fact]
    public async Task Rescheduling_is_reversible_and_both_changes_are_audited()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var original = watch.Condition.Interval;

        Assert.True((await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.FromMinutes(5), "temporary, for the observation")).Succeeded);

        Assert.True((await Console().RescheduleWatchAsync(
            watch.WatchId, original!.Value, "restoring the daily schedule")).Succeeded);

        Assert.Equal(original, watch.Condition.Interval);
        Assert.Null(watch.LastFiredAtUtc);

        // Two acts, two records. The idempotency key carries the interval, so putting it back is
        // not mistaken for a repeat of taking it away.
        var records = _audit.Records
            .Where(r => r.EventType == AuditEventType.ActionExecuted &&
                        r.ActionType == OperatorActionTypes.RescheduleWatch.Value)
            .ToList();

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(OperatorId, r.Actor));
        Assert.All(records, r => Assert.Equal(ProposerKind.Human, r.ActorKind));
        Assert.All(records, r => Assert.Equal(Capability.ReferenceDataManagement, r.Capability));
    }

    [Fact]
    public async Task Rescheduling_to_the_interval_it_already_has_changes_nothing()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var savesBefore = _watches.SaveCount;

        var outcome = await Console().RescheduleWatchAsync(
            watch.WatchId, watch.Condition.Interval!.Value, "no change");

        Assert.Equal(OperatorOutcomeStatus.DuplicateSuppressed, outcome.Status);
        Assert.Equal(savesBefore, _watches.SaveCount);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_reschedule_a_watch()
    {
        SignIn(OperatorPrivilege.AdministerWatches);
        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());
        var original = watch.Condition.Interval;

        _operators.Current = null;

        var outcome = await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.FromMinutes(5), "no");

        Assert.Equal(OperatorOutcomeStatus.NotAuthenticated, outcome.Status);
        Assert.Equal(original, watch.Condition.Interval);
        Assert.DoesNotContain(
            _audit.Records, r => r.ActionType == OperatorActionTypes.RescheduleWatch.Value);
    }

    [Fact]
    public async Task An_operator_without_AdministerWatches_cannot_reschedule_a_watch()
    {
        SignIn(OperatorPrivilege.AdministerWatches);
        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());
        var original = watch.Condition.Interval;

        SignIn(OperatorPrivilege.AnswerEscalations);

        var outcome = await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.FromMinutes(5), "no");

        Assert.Equal(OperatorOutcomeStatus.NotPermitted, outcome.Status);
        Assert.Contains("AdministerWatches", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(original, watch.Condition.Interval);
        Assert.DoesNotContain(
            _audit.Records, r => r.ActionType == OperatorActionTypes.RescheduleWatch.Value);
    }

    [Fact]
    public async Task Rescheduling_a_watch_that_does_not_exist_is_not_found()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        var outcome = await Console().RescheduleWatchAsync(
            Guid.NewGuid(), TimeSpan.FromMinutes(5), "stepping back");

        Assert.Equal(OperatorOutcomeStatus.NotFound, outcome.Status);
        Assert.Empty(_audit.Records);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rescheduling_without_a_reason_is_refused(string reason)
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());
        var original = watch.Condition.Interval;

        var outcome = await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.FromMinutes(5), reason);

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.Equal(original, watch.Condition.Interval);
        Assert.DoesNotContain(
            _audit.Records, r => r.ActionType == OperatorActionTypes.RescheduleWatch.Value);
    }

    /// <summary>
    /// The domain's own refusal, reported rather than proposed - and checked before the effect, so
    /// nothing can throw inside an authorisation window.
    /// </summary>
    [Fact]
    public async Task An_interval_the_domain_refuses_is_reported_rather_than_proposed()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());
        var original = watch.Condition.Interval;

        var outcome = await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.Zero, "zero would mean firing continuously");

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.Equal(original, watch.Condition.Interval);
        Assert.DoesNotContain(
            _audit.Records, r => r.ActionType == OperatorActionTypes.RescheduleWatch.Value);
    }

    /// <summary>A watch that waits for a comparison cannot be given an interval.</summary>
    [Fact]
    public async Task A_watch_that_is_not_a_schedule_cannot_be_rescheduled()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        var priceMove = Watch.Create(
            "price move",
            WatchTarget.Create("Security", "AAPL"),
            TriggerType.PriceMove,
            TriggerCondition.Compare(TriggerComparison.MovedAtLeast, 0.02m),
            TimeSpan.FromHours(1),
            Capability.OpportunityManagement,
            "equity-price-review",
            Now);

        _watches.Seed(priceMove);

        var outcome = await Console().RescheduleWatchAsync(
            priceMove.WatchId, TimeSpan.FromMinutes(5), "not a schedule");

        Assert.Equal(OperatorOutcomeStatus.Refused, outcome.Status);
        Assert.Contains("schedule", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            _audit.Records, r => r.ActionType == OperatorActionTypes.RescheduleWatch.Value);
    }

    /// <summary>The write goes through the seam: one authorisation window, one save.</summary>
    [Fact]
    public async Task Rescheduling_persists_inside_one_authorisation_window()
    {
        SignIn(OperatorPrivilege.AdministerWatches);

        await Console().CreateScheduledWatchAsync(Definition());
        var watch = Assert.Single(await _watches.GetAllAsync());

        var windowsBefore = _writes.WindowsOpened;

        Assert.True((await Console().RescheduleWatchAsync(
            watch.WatchId, TimeSpan.FromMinutes(5), "temporary")).Succeeded);

        Assert.Equal(windowsBefore + 1, _writes.WindowsOpened);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static ScheduledWatchDefinition Definition() =>
        new(
            "AAPL price review",
            "Security",
            "AAPL",
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(1),
            Capability.OpportunityManagement,
            "equity-price-review");

    /// <summary>A versioned score, because an opportunity cannot be ranked by a loose number.</summary>
    private static MetricResult Score(Opportunity opportunity) =>
        MetricResult.Create(
            CalculationContext.Create(opportunity.Subject, KnowledgeCutoff.At(Now), Now),
            MetricId.Create("score.test"),
            MetricValue.Ratio(0.6m),
            "a fixture",
            opportunity.Source.DiscovererId,
            CalculationVersion.Create(1, 0),
            Now,
            [CalculationInput.Create(
                "close",
                Claims.Fact(100m, Provenance.Create("operator-price-history", Now, Now, Now)),
                UnitOfMeasure.Money)]);

    private void SignIn(params OperatorPrivilege[] privileges) =>
        _operators.Current = OperatorIdentity.Create(OperatorId, "Alex", privileges);

    private OperatorConsole Console()
    {
        var gateway = new ActionGateway(
            new PolicyEngine(),
            new ContextProvider(_autonomy),
            _audit,
            new FakeIdempotencyStore(),
            new FakeExecutionStore(),
            _writes,
            _clock);

        return new OperatorConsole(
            gateway,
            _operators,
            new FakeCorrelationContext(),
            _opportunities,
            _escalations,
            _watches,
            _killSwitch,
            _unitOfWork,
            _clock);
    }

    /// <summary>A ranked opportunity in the repository, which is what an operator would see.</summary>
    private async Task<Guid> StoredOpportunityAsync()
    {
        var opportunity = Opportunity.Draft(
            EquityOpportunity.Type,
            IngestionSubject.Create("Security", "AAPL"),
            OpportunitySource.Create("discovery.price-recovery", Now),
            "AAPL fell below its recent high",
            "A candidate the screen produced.",
            OpportunityDetail.Create(
                EquityOpportunity.Type,
                EquityDetail.ToJson("AAPL", 1m, 100m, 130m, "USD", 0.6m, 30)),
            Now,
            [ClaimId.New()]);

        opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(opportunity, Now),
            OpportunityRisk.Create(
                "A reversible position in a listed equity.",
                ReversibilityClass.ReversibleWithCost,
                opportunity.Evidence),
            Confidence.Create(0.5m),
            Now);

        opportunity.Rank(OpportunityScore.From(Score(opportunity)), Now);

        await _opportunities.AddAsync(opportunity);

        return opportunity.OpportunityId.Value;
    }
}

/// <summary>An operator context a test can sign in and out of.</summary>
internal sealed class StubOperatorContext : IOperatorContext
{
    public OperatorIdentity? Current { get; set; }
}

/// <summary>A kill-switch administration that records what it was asked to engage.</summary>
/// <remarks>
/// It has no disengage, which is the point: the interface it implements has none either, and a
/// double that invented one would be testing a system that does not exist.
/// </remarks>
internal sealed class RecordingKillSwitchAdministration : IKillSwitchAdministration
{
    public bool Engaged { get; private set; }

    public Capability? Capability { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Task EngageAsync(
        Capability? capability,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        Engaged = true;
        Capability = capability;
        Reason = reason;

        return Task.CompletedTask;
    }
}
