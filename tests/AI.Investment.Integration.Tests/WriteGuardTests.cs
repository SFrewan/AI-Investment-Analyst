using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests;

/// <summary>
/// The persistence half of the safety guarantee, against a real database.
/// </summary>
/// <remarks>
/// The domain refuses to start an execution without an authorising decision; this proves the
/// other, independent half - that the database refuses to accept a domain write when no
/// authorisation window is open. Two mechanisms, because one can be forgotten at a call site.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class WriteGuardTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public WriteGuardTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Empties the shared test database before each test in this class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identifiers below - <c>ALLOW1</c>, <c>BYPASS1</c> - are fixed on purpose: they name the
    /// case under test and read in a failure message. Fixed identifiers plus a database that
    /// outlives the run is what produced <c>23505 duplicate key value violates unique constraint
    /// "ix_companies_ticker"</c> on every run after the first, so the database is emptied here
    /// rather than the identifiers being made unique. Uniqueness in the test data would have
    /// hidden the leak; it would not have removed it, and the next test to reuse a value would
    /// have found it again.
    /// </para>
    /// <para>
    /// Before each test, not after: a failed test then leaves its rows behind to be looked at, and
    /// the next test is still clean.
    /// </para>
    /// </remarks>
    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The headline invariant: a repository write plus SaveChanges, outside the seam, is refused.
    /// </summary>
    [SkippableFact]
    public async Task A_domain_write_without_an_authorisation_window_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        context.Companies.Add(NewCompany(Ticker.Create("BYPASS1")));

        var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
            () => context.SaveChangesAsync());

        Assert.Contains("IActionGateway", exception.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_domain_write_inside_an_authorisation_window_succeeds()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var ticker = Ticker.Create("ALLOW1");
        var company = NewCompany(ticker);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Companies.Add(company);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());
        Assert.NotNull(await verification.Companies.FirstOrDefaultAsync(c => c.Ticker == ticker));
    }

    /// <summary>
    /// Audit rows must be writable precisely when nothing is authorised - that is the situation a
    /// denial creates, and a denial is one of the most important things to record.
    /// </summary>
    [SkippableFact]
    public async Task An_audit_record_can_be_written_when_nothing_is_authorised()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var sink = new Infrastructure.Auditing.EfAuditSink(context);
        var proposal = SeamTestDecisions.NewProposal(Now);
        var decision = PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], Now);

        await sink.RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, Now));

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());
        var stored = await verification.AuditRecords
            .FirstOrDefaultAsync(a => a.ProposalId == proposal.ProposalId);

        Assert.NotNull(stored);
        Assert.Equal(AuditEventType.ActionDenied, stored!.EventType);
        Assert.Equal(PolicyOutcome.Deny, stored.Outcome);

        // The jsonb details column round-trips.
        Assert.True(stored.Details.ContainsKey("decision.reason"));
    }

    /// <summary>
    /// An audit trail the application can rewrite is not an audit trail.
    /// </summary>
    [SkippableFact]
    public async Task An_audit_record_cannot_be_modified()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = SeamTestDecisions.NewProposal(Now);
        var decision = PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], Now);
        var sink = new Infrastructure.Auditing.EfAuditSink(context);

        await sink.RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, Now));

        var tracked = await context.AuditRecords.FirstAsync(a => a.ProposalId == proposal.ProposalId);
        context.Entry(tracked).State = EntityState.Modified;

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task An_audit_record_cannot_be_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = SeamTestDecisions.NewProposal(Now);
        var decision = PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], Now);
        await new Infrastructure.Auditing.EfAuditSink(context)
            .RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, Now));

        var tracked = await context.AuditRecords.FirstAsync(a => a.ProposalId == proposal.ProposalId);
        context.AuditRecords.Remove(tracked);

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// The same rule, on the path that actually matters: inside an open authorisation window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two tests above open no window, so they exercise the guard's unauthorised branch. That
    /// left the important case untested. An authorisation window is open for the whole duration of
    /// an action's effect, which means the code best placed to rewrite the record of what it just
    /// did is the code running inside the window - and the guard used to return early for exactly
    /// that code.
    /// </para>
    /// <para>
    /// Authorisation permits an effect. It does not permit editing the history of that effect.
    /// These two tests are what hold that distinction in place.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_audit_record_cannot_be_modified_even_inside_an_authorisation_window()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = SeamTestDecisions.NewProposal(Now);
        var decision = PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], Now);
        await new Infrastructure.Auditing.EfAuditSink(context)
            .RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, Now));

        var tracked = await context.AuditRecords.FirstAsync(a => a.ProposalId == proposal.ProposalId);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Entry(tracked).State = EntityState.Modified;

            var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => context.SaveChangesAsync());

            Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task An_execution_record_cannot_be_deleted_even_inside_an_authorisation_window()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = SeamTestDecisions.NewProposal(Now);
        var decision = PolicyDecision.Execute(proposal, "permitted for the test", ["test@1"], Now);
        var execution = ActionExecution.Start(proposal, decision, Now);
        execution.MarkSucceeded(Now);

        await new EfActionExecutionStore(context).RecordAsync(execution);

        var tracked = await context.ActionExecutions
            .FirstAsync(e => e.ExecutionId == execution.ExecutionId);

        using (authorization.Authorize(decision))
        {
            context.ActionExecutions.Remove(tracked);

            var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => context.SaveChangesAsync());

            Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A key claimed twice through the same context is refused the second time - and refused, not
    /// thrown at.
    /// </summary>
    /// <remarks>
    /// This is the path where the store answers from its own identity map. It used to fail with
    /// <c>InvalidOperationException: another instance with the same key value for
    /// {'IdempotencyKey'} is already being tracked</c>, thrown by <c>Add</c> before any SQL was
    /// sent - so the store's unique-violation handler never saw it and a caller asking a
    /// reasonable question got an exception instead of an answer. The test below covers the other
    /// path, where the database is the one that decides.
    /// </remarks>
    [SkippableFact]
    public async Task An_idempotency_key_can_be_claimed_only_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());
        var store = new EfIdempotencyStore(context);
        var key = $"test.claim:{Guid.NewGuid():n}";

        Assert.True(await store.TryClaimAsync(key, Guid.NewGuid(), Now));
        Assert.False(await store.TryClaimAsync(key, Guid.NewGuid(), Now));
    }

    /// <summary>
    /// The database, not the application, is what makes deduplication correct under the
    /// concurrency that retries create.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two contexts, because that is the shape a retry actually has: the second attempt arrives in
    /// a different scope, with a different context, whose identity map knows nothing about the
    /// first. Nothing in the application can answer here - the refusal can only come from the
    /// primary key on <c>processed_actions</c>, which is the whole reason the claim is an INSERT.
    /// </para>
    /// <para>
    /// Written alongside the identity-map fix above so that the short-circuit added there cannot
    /// quietly become the only thing being tested. A store that stopped inserting altogether would
    /// still pass the test above; it fails this one.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_idempotency_key_claimed_by_one_context_is_refused_to_another()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var key = $"test.claim:{Guid.NewGuid():n}";

        await using var claiming = _fixture.CreateContext(new ScopedWriteAuthorization());
        Assert.True(
            await new EfIdempotencyStore(claiming).TryClaimAsync(key, Guid.NewGuid(), Now));

        await using var retrying = _fixture.CreateContext(new ScopedWriteAuthorization());
        Assert.False(
            await new EfIdempotencyStore(retrying).TryClaimAsync(key, Guid.NewGuid(), Now));
    }


    private static Company NewCompany(Ticker ticker) =>
        Company.Create(CompanyId.New(), $"Test {ticker.Value}", ticker, Now);

}
