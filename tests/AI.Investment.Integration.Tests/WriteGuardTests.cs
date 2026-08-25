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
public sealed class WriteGuardTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public WriteGuardTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The headline invariant: a repository write plus SaveChanges, outside the seam, is refused.
    /// </summary>
    [Fact]
    public async Task A_domain_write_without_an_authorisation_window_is_refused()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        context.Companies.Add(NewCompany(Ticker.Create("BYPASS1")));

        var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
            () => context.SaveChangesAsync());

        Assert.Contains("IActionGateway", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_domain_write_inside_an_authorisation_window_succeeds()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var ticker = Ticker.Create("ALLOW1");
        var company = NewCompany(ticker);

        using (authorization.Authorize(ExecuteDecision()))
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
    [Fact]
    public async Task An_audit_record_can_be_written_when_nothing_is_authorised()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var sink = new Infrastructure.Auditing.EfAuditSink(context);
        var proposal = NewProposal();
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
    [Fact]
    public async Task An_audit_record_cannot_be_modified()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = NewProposal();
        var decision = PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], Now);
        var sink = new Infrastructure.Auditing.EfAuditSink(context);

        await sink.RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, Now));

        var tracked = await context.AuditRecords.FirstAsync(a => a.ProposalId == proposal.ProposalId);
        context.Entry(tracked).State = EntityState.Modified;

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task An_audit_record_cannot_be_deleted()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = NewProposal();
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
    [Fact]
    public async Task An_audit_record_cannot_be_modified_even_inside_an_authorisation_window()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = NewProposal();
        var decision = PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], Now);
        await new Infrastructure.Auditing.EfAuditSink(context)
            .RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, Now));

        var tracked = await context.AuditRecords.FirstAsync(a => a.ProposalId == proposal.ProposalId);

        using (authorization.Authorize(ExecuteDecision()))
        {
            context.Entry(tracked).State = EntityState.Modified;

            var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => context.SaveChangesAsync());

            Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_execution_record_cannot_be_deleted_even_inside_an_authorisation_window()
    {
        if (!Skip()) { return; }

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        var proposal = NewProposal();
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
    /// The database, not the application, is what makes deduplication correct under the
    /// concurrency that retries create.
    /// </summary>
    [Fact]
    public async Task An_idempotency_key_can_be_claimed_only_once()
    {
        if (!Skip()) { return; }

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());
        var store = new EfIdempotencyStore(context);
        var key = $"test.claim:{Guid.NewGuid():n}";

        Assert.True(await store.TryClaimAsync(key, Guid.NewGuid(), Now));
        Assert.False(await store.TryClaimAsync(key, Guid.NewGuid(), Now));
    }

    private bool Skip()
    {
        if (_fixture.Available)
        {
            return true;
        }

        // Reported rather than failed: no database is an environment gap, not a code defect.
        // CI must supply one, or these tests prove nothing.
        Console.WriteLine($"SKIPPED: {_fixture.UnavailableReason}");
        return false;
    }

    private static Company NewCompany(Ticker ticker) =>
        Company.Create(CompanyId.New(), $"Test {ticker.Value}", ticker, Now);

    private static ActionProposal NewProposal() =>
        ActionProposal.Create(
            CorrelationId.New(),
            Capability.ReferenceDataManagement,
            ActionType.Create("test.action"),
            ActionTarget.Create("Test"),
            new TestParameters(),
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service("integration-test", "1.0"),
            Guid.NewGuid().ToString("n"),
            Now);

    private static PolicyDecision ExecuteDecision() =>
        PolicyDecision.Execute(NewProposal(), "permitted for the test", ["test@1"], Now);

    private sealed record TestParameters : IActionParameters
    {
        public string Describe() => "integration test parameters";
    }
}
