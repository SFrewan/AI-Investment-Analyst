using System.Text;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Retention;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Application.UnitTests.Ingestion;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Retention;

/// <summary>
/// The one operation in this platform that destroys evidence.
/// </summary>
/// <remarks>
/// Two assertions carry most of the weight: that nothing was deleted, and that when something was,
/// the marker was written first. The ordering is not cosmetic - a crash between the two steps must
/// leave a marker for a payload that still exists, never a deleted payload with nothing recording
/// why.
/// </remarks>
public sealed class RetentionEnforcerTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId VendorId = SourceId.Create("some-vendor");

    private static DataSource Source(RetentionLimit retention)
    {
        var source = DataSource.Register(
            VendorId,
            "Some Vendor",
            SourceType.DataVendor,
            SourceAuthority.Secondary,
            Region.UnitedStates,
            [DataCategory.MarketPrices],
            UpdateCadence.Daily(),
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: false,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                retention: retention),
            VerificationPolicy.RequiresCorroboration,
            Now.AddYears(-5));

        source.Activate(Now.AddYears(-5));

        return source;
    }

    private sealed record Harness(
        RetentionEnforcer Enforcer,
        RecordingArchive Archive,
        RecordingUnreplayableEvidenceStore Markers,
        StubActionGateway Actions);

    private static Harness Build(
        DataSource? source,
        bool isReferenced,
        ActionOutcomeStatus policy = ActionOutcomeStatus.Executed)
    {
        var registry = new InMemorySourceRegistry();

        if (source is not null)
        {
            registry.Add(source);
        }

        var archive = new RecordingArchive();
        var markers = new RecordingUnreplayableEvidenceStore();
        var actions = new StubActionGateway(policy);

        var enforcer = new RetentionEnforcer(
            registry,
            archive,
            new StubPayloadReferenceIndex(isReferenced),
            markers,
            actions,
            new FixedClock(Now));

        return new Harness(enforcer, archive, markers, actions);
    }

    private static Domain.Ingestion.ContentHash Seed(Harness harness, DateTime retrievedAtUtc) =>
        harness.Archive.Seed(Encoding.UTF8.GetBytes("{\"quote\":1}"), VendorId, retrievedAtUtc);

    [Fact]
    public async Task A_source_with_no_licensed_limit_keeps_its_data()
    {
        var harness = Build(Source(RetentionLimit.Unlimited), isReferenced: false);
        var hash = Seed(harness, Now.AddYears(-10));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.Equal(RetentionOutcome.Retain, result.Decision.Outcome);
        Assert.Equal(RetentionAction.NothingRequired, result.Action);
        Assert.Empty(harness.Archive.Deleted);
    }

    [Fact]
    public async Task A_payload_inside_its_licensed_limit_is_kept()
    {
        var harness = Build(Source(RetentionLimit.OfDays(365)), isReferenced: false);
        var hash = Seed(harness, Now.AddDays(-100));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.Equal(RetentionOutcome.Retain, result.Decision.Outcome);
        Assert.Equal(RetentionAction.NothingRequired, result.Action);
        Assert.Empty(harness.Archive.Deleted);
    }

    [Fact]
    public async Task A_payload_past_its_licensed_limit_is_deleted()
    {
        var harness = Build(Source(RetentionLimit.OfDays(365)), isReferenced: false);
        var hash = Seed(harness, Now.AddDays(-400));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.True(result.Decision.RequiresDeletion);
        Assert.Equal(RetentionAction.Deleted, result.Action);
        Assert.True(result.WasDeleted);
        Assert.Single(harness.Archive.Deleted);
        Assert.Empty(harness.Markers.Recorded);
    }

    /// <summary>
    /// The floor: the claim survives, the payload does not, and the gap is recorded rather than
    /// discovered later by a replay that quietly returns nothing.
    /// </summary>
    [Fact]
    public async Task Referenced_evidence_is_marked_unreplayable_before_it_is_deleted()
    {
        var harness = Build(Source(RetentionLimit.OfDays(365)), isReferenced: true);
        var hash = Seed(harness, Now.AddDays(-400));

        await harness.Enforcer.EnforceAsync(hash);

        Assert.Single(harness.Markers.Recorded);
        Assert.Single(harness.Archive.Deleted);

        var marker = harness.Markers.Recorded[0];
        Assert.Equal(hash, marker.Id);
        Assert.Equal(VendorId, marker.SourceId);
        Assert.Equal(RetentionPolicy.LicensedLimitExceededRule, marker.RuleId);
    }

    /// <summary>
    /// Deleting evidence is irreversible and is declared as such, so the policy engine's
    /// irreversibility rule applies and a denial stops it.
    /// </summary>
    [Fact]
    public async Task A_policy_denial_stops_the_deletion()
    {
        var harness = Build(
            Source(RetentionLimit.OfDays(365)),
            isReferenced: true,
            policy: ActionOutcomeStatus.Denied);

        var hash = Seed(harness, Now.AddDays(-400));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.Empty(harness.Archive.Deleted);
        Assert.Empty(harness.Markers.Recorded);

        // The obligation stands and the payload is still on disk. Reporting only the decision
        // would let a caller record this as discharged.
        Assert.True(result.Decision.RequiresDeletion);
        Assert.Equal(RetentionAction.DeletionRefused, result.Action);
        Assert.True(result.IsOutstanding);
        Assert.False(result.WasDeleted);
    }

    /// <summary>
    /// Approval and duplicate suppression are not deletions either. Both leave the payload in
    /// place, and both must be visible as outstanding rather than as work completed.
    /// </summary>
    [Theory]
    [InlineData(ActionOutcomeStatus.ApprovalRequired)]
    [InlineData(ActionOutcomeStatus.DuplicateSuppressed)]
    public async Task A_deletion_that_does_not_execute_is_reported_as_outstanding(
        ActionOutcomeStatus status)
    {
        var harness = Build(
            Source(RetentionLimit.OfDays(365)),
            isReferenced: false,
            policy: status);

        var hash = Seed(harness, Now.AddDays(-400));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.Equal(RetentionAction.DeletionRefused, result.Action);
        Assert.Empty(harness.Archive.Deleted);
    }

    [Fact]
    public async Task The_deletion_is_proposed_under_its_own_capability_and_declared_irreversible()
    {
        var harness = Build(Source(RetentionLimit.OfDays(365)), isReferenced: false);
        var hash = Seed(harness, Now.AddDays(-400));

        await harness.Enforcer.EnforceAsync(hash);

        var proposal = harness.Actions.LastProposal;

        Assert.NotNull(proposal);
        Assert.Equal(Capability.DataRetention, proposal!.Capability);
        Assert.Equal(ReversibilityClass.Irreversible, proposal.Economics.Reversibility);
        Assert.Equal($"retention.delete:{hash.Value}", proposal.IdempotencyKey);
    }

    /// <summary>
    /// The obligation lives in the source's terms. With no source there are no terms, and an
    /// obligation that cannot be established never compels deletion.
    /// </summary>
    [Fact]
    public async Task An_unregistered_source_never_causes_deletion()
    {
        var harness = Build(source: null, isReferenced: false);
        var hash = Seed(harness, Now.AddYears(-10));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.Equal(RetentionOutcome.Retain, result.Decision.Outcome);
        Assert.Equal(RetentionEnforcer.UnknownSourceRule, result.Decision.RuleId);
        Assert.Empty(harness.Archive.Deleted);
    }

    /// <summary>
    /// A sweep racing a previous pass should converge, not throw.
    /// </summary>
    [Fact]
    public async Task A_payload_that_is_not_archived_is_not_an_error()
    {
        var harness = Build(Source(RetentionLimit.OfDays(1)), isReferenced: false);

        var result = await harness.Enforcer.EnforceAsync(
            Domain.Ingestion.ContentHash.Compute(Encoding.UTF8.GetBytes("never stored")));

        Assert.Equal(RetentionOutcome.Retain, result.Decision.Outcome);
        Assert.Equal(RetentionEnforcer.NothingArchivedRule, result.Decision.RuleId);
    }

    /// <summary>
    /// Nothing in the enforcer names a provider. The behaviour follows from the licensing metadata,
    /// so a future source with a different obligation needs no change here.
    /// </summary>
    [Theory]
    [InlineData(30, 45, true)]
    [InlineData(30, 15, false)]
    [InlineData(2555, 3000, true)]
    [InlineData(2555, 100, false)]
    public async Task Behaviour_follows_the_licence_rather_than_the_provider(
        int limitDays,
        int ageDays,
        bool expectDeletion)
    {
        var harness = Build(Source(RetentionLimit.OfDays(limitDays)), isReferenced: false);
        var hash = Seed(harness, Now.AddDays(-ageDays));

        var result = await harness.Enforcer.EnforceAsync(hash);

        Assert.Equal(expectDeletion, result.Decision.RequiresDeletion);
        Assert.Equal(expectDeletion ? 1 : 0, harness.Archive.Deleted.Count);
    }
}
