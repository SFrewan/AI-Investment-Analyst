using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Retention;
using AI.Investment.Application.UnitTests.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Retention;

/// <summary>
/// Walking the archive: what it counts, where it stops, and what it refuses to claim.
/// </summary>
/// <remarks>
/// The sweep makes no retention judgements at all, so none is asserted here - those belong to
/// <see cref="RetentionEnforcerTests"/>. What is asserted is bookkeeping, and the reason it
/// deserves tests of its own is that every one of these counts is a compliance statement. A sweep
/// that reported completion when a limit stopped it, or counted a database outage as fifty policy
/// refusals, would be describing a state of affairs nobody observed.
/// </remarks>
public sealed class RetentionSweepTests
{
    private static readonly DateTime Retrieved = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId Vendor = SourceId.Create("test-vendor");

    /// <summary>An enforcer whose answer is scripted per payload.</summary>
    private sealed class ScriptedEnforcer : IRetentionEnforcer
    {
        private readonly Func<ContentHash, int, RetentionAction> _answer;

        public ScriptedEnforcer(Func<ContentHash, int, RetentionAction> answer) => _answer = answer;

        public int Calls { get; private set; }

        public Task<RetentionEnforcementResult> EnforceAsync(
            ContentHash hash,
            CancellationToken cancellationToken = default)
        {
            // Counted before the answer is produced, so a scripted throw still advances the index.
            // Otherwise the payload scripted to fail would be re-scripted for every payload after
            // it, and "one poisoned entry" would silently become "all of them".
            var index = Calls;
            Calls++;

            var action = _answer(hash, index);

            var outcome = action == RetentionAction.NothingRequired
                ? RetentionOutcome.Retain
                : RetentionOutcome.DeleteRequired;

            return Task.FromResult(new RetentionEnforcementResult(
                new RetentionDecision(outcome, "test.rule@1", "scripted"),
                action));
        }
    }

    private sealed class ThrowingEnforcer : IRetentionEnforcer
    {
        private readonly Exception _exception;

        public ThrowingEnforcer(Exception exception) => _exception = exception;

        public int Calls { get; private set; }

        public Task<RetentionEnforcementResult> EnforceAsync(
            ContentHash hash,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            throw _exception;
        }
    }

    private static RecordingArchive ArchiveWith(int payloads)
    {
        var archive = new RecordingArchive();

        for (var i = 0; i < payloads; i++)
        {
            archive.Seed(Encoding.UTF8.GetBytes($"payload {i}"), Vendor, Retrieved);
        }

        return archive;
    }

    private static RetentionSweep Sweep(IRawResponseArchive archive, IRetentionEnforcer enforcer) =>
        new(archive, enforcer);

    // ---------- counting ----------

    [Fact]
    public async Task An_empty_archive_sweeps_clean()
    {
        var summary = await Sweep(
                new RecordingArchive(),
                new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired))
            .SweepAsync(100);

        Assert.Equal(new RetentionSweepSummary(0, 0, 0, 0, 0, Reached: true), summary);
        Assert.False(summary.HasMore);
    }

    [Fact]
    public async Task Every_payload_is_examined()
    {
        var enforcer = new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired);

        var summary = await Sweep(ArchiveWith(5), enforcer).SweepAsync(100);

        Assert.Equal(5, summary.Examined);
        Assert.Equal(5, summary.Retained);
        Assert.Equal(5, enforcer.Calls);
        Assert.True(summary.Reached);
    }

    [Fact]
    public async Task Deletions_and_retentions_are_counted_separately()
    {
        // Alternates, so neither count can be right by accident.
        var enforcer = new ScriptedEnforcer((_, call) =>
            call % 2 == 0 ? RetentionAction.Deleted : RetentionAction.NothingRequired);

        var summary = await Sweep(ArchiveWith(6), enforcer).SweepAsync(100);

        Assert.Equal(6, summary.Examined);
        Assert.Equal(3, summary.Deleted);
        Assert.Equal(3, summary.Retained);
        Assert.Equal(0, summary.Outstanding);
    }

    [Fact]
    public async Task A_refused_deletion_is_outstanding_not_retained()
    {
        var summary = await Sweep(
                ArchiveWith(4),
                new ScriptedEnforcer((_, _) => RetentionAction.DeletionRefused))
            .SweepAsync(100);

        // The distinction that matters: four payloads a licence says must go are still on disk.
        // Counting them as retained would report a compliance exposure as normal operation.
        Assert.Equal(0, summary.Retained);
        Assert.Equal(4, summary.DeletionsRefused);
        Assert.Equal(4, summary.Outstanding);
        Assert.Equal(0, summary.Deleted);
    }

    // ---------- bounds ----------

    [Fact]
    public async Task The_limit_stops_the_sweep_and_says_so()
    {
        var enforcer = new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired);

        var summary = await Sweep(ArchiveWith(10), enforcer).SweepAsync(3);

        Assert.Equal(3, summary.Examined);
        Assert.Equal(3, enforcer.Calls);

        // "We stopped looking" must not read as "there is nothing left".
        Assert.False(summary.Reached);
        Assert.True(summary.HasMore);
    }

    [Fact]
    public async Task Exhausting_the_archive_reports_completion()
    {
        var summary = await Sweep(
                ArchiveWith(3),
                new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired))
            .SweepAsync(3);

        // Exactly the limit, and exactly the archive. Completion here is a real statement.
        Assert.Equal(3, summary.Examined);
        Assert.True(summary.Reached);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_limit_below_one_examines_nothing_and_claims_nothing(int limit)
    {
        var enforcer = new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired);

        var summary = await Sweep(ArchiveWith(5), enforcer).SweepAsync(limit);

        Assert.Equal(0, summary.Examined);
        Assert.Equal(0, enforcer.Calls);

        // A misconfigured limit must not read as "the archive is clean".
        Assert.False(summary.Reached);
    }

    [Fact]
    public async Task A_limit_above_the_ceiling_is_capped()
    {
        var enforcer = new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired);

        var summary = await Sweep(ArchiveWith(4), enforcer).SweepAsync(int.MaxValue);

        // The ceiling bounds work, it does not bound honesty: the archive really was exhausted.
        Assert.Equal(4, summary.Examined);
        Assert.True(summary.Reached);
    }

    // ---------- failure ----------

    [Fact]
    public async Task A_payload_that_throws_does_not_end_the_sweep()
    {
        var enforcer = new ScriptedEnforcer((_, call) =>
            call == 0
                ? throw new InvalidOperationException("this one is poisoned")
                : RetentionAction.NothingRequired);

        var summary = await Sweep(ArchiveWith(4), enforcer).SweepAsync(100);

        // A single poisoned entry that killed every sweep would block the obligation permanently.
        Assert.Equal(4, summary.Examined);
        Assert.Equal(3, summary.Retained);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public async Task A_failure_is_not_reported_as_a_policy_refusal()
    {
        var summary = await Sweep(
                ArchiveWith(3),
                new ThrowingEnforcer(new InvalidOperationException("the database is down")))
            .SweepAsync(100);

        // An outage reported as "policy declined to delete these" would be a sentence about
        // compliance that nothing observed.
        Assert.Equal(3, summary.Failed);
        Assert.Equal(0, summary.DeletionsRefused);
        Assert.Equal(0, summary.Retained);
        Assert.Equal(3, summary.Outstanding);
    }

    [Fact]
    public async Task Cancellation_ends_the_sweep_rather_than_being_counted()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var enforcer = new ThrowingEnforcer(new OperationCanceledException());

        // Swallowing this would turn a shutdown into a report claiming every remaining payload
        // had failed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sweep(ArchiveWith(3), enforcer).SweepAsync(100, cancellation.Token));
    }

    // ---------- refusals ----------

    [Fact]
    public void Null_collaborators_are_refused()
    {
        var enforcer = new ScriptedEnforcer((_, _) => RetentionAction.NothingRequired);

        Assert.Throws<ArgumentNullException>(() => new RetentionSweep(null!, enforcer));
        Assert.Throws<ArgumentNullException>(() => new RetentionSweep(new RecordingArchive(), null!));
    }
}
