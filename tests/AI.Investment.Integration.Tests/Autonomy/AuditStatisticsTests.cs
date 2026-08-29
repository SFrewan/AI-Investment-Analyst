using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using Xunit;

namespace AI.Investment.Integration.Tests.Autonomy;

/// <summary>
/// The circuit breaker's signals, counted from the audit trail against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The breaker's demotion policy has always been fail-closed, which made it correct and useless:
/// policy breaches and execution failures were counted nowhere, so every unattended grant demoted on
/// the first sweep for want of a number rather than because of one. These establish that the number
/// exists and means what the breaker takes it to mean.
/// </para>
/// <para>
/// Against the database rather than a double, because the claims are about a query. Whether a count
/// narrows on the right capability, whether it excludes what happened outside the window, and
/// whether an approval requirement is kept apart from a denial are all properties of SQL - and a
/// double would agree with whatever the query was written to do.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class AuditStatisticsTests : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public AuditStatisticsTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Denials and failures are counted, and nothing else is.</summary>
    [SkippableFact]
    public async Task Denials_and_failures_are_counted_and_successes_are_not()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await WriteAsync(
            Denied(Capability.SimulatedExecution, Start.AddDays(1)),
            Denied(Capability.SimulatedExecution, Start.AddDays(2)),
            Failed(Capability.SimulatedExecution, Start.AddDays(3)),
            Executed(Capability.SimulatedExecution, Start.AddDays(4)),
            ApprovalRequired(Capability.SimulatedExecution, Start.AddDays(5)));

        var incidents = await CountAsync(Capability.SimulatedExecution, Start, Now);

        Assert.Equal(2, incidents.PolicyBreaches);
        Assert.Equal(1, incidents.ExecutionFailures);
    }

    /// <summary>
    /// An action that needs a person is not a breach. Counting it as one would demote every grant on
    /// a platform behaving exactly as designed.
    /// </summary>
    [SkippableFact]
    public async Task An_approval_requirement_is_not_a_breach()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await WriteAsync(
            ApprovalRequired(Capability.SimulatedExecution, Start.AddDays(1)),
            ApprovalRequired(Capability.SimulatedExecution, Start.AddDays(2)));

        var incidents = await CountAsync(Capability.SimulatedExecution, Start, Now);

        Assert.Equal(0, incidents.PolicyBreaches);
        Assert.Equal(0, incidents.ExecutionFailures);
    }

    /// <summary>
    /// Autonomy is per capability, so a denial under one says nothing about a grant under another.
    /// </summary>
    [SkippableFact]
    public async Task A_denial_under_another_capability_is_not_counted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await WriteAsync(
            Denied(Capability.DataIngestion, Start.AddDays(1)),
            Denied(Capability.DataIngestion, Start.AddDays(2)),
            Denied(Capability.SimulatedExecution, Start.AddDays(3)));

        Assert.Equal(1, (await CountAsync(Capability.SimulatedExecution, Start, Now)).PolicyBreaches);
        Assert.Equal(2, (await CountAsync(Capability.DataIngestion, Start, Now)).PolicyBreaches);
        Assert.Equal(0, (await CountAsync(Capability.Analysis, Start, Now)).PolicyBreaches);
    }

    /// <summary>
    /// The window is closed at both ends: a breach before a grant was issued was something the
    /// person issuing it could see, and one after the instant being reasoned about had not happened.
    /// </summary>
    [SkippableFact]
    public async Task Only_what_happened_inside_the_window_is_counted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await WriteAsync(
            Denied(Capability.SimulatedExecution, Start.AddDays(-1)),
            Denied(Capability.SimulatedExecution, Start.AddDays(5)),
            Denied(Capability.SimulatedExecution, Now.AddDays(1)));

        Assert.Equal(1, (await CountAsync(Capability.SimulatedExecution, Start, Now)).PolicyBreaches);
    }

    /// <summary>An empty trail counts zero, which is a measurement rather than an absence.</summary>
    [SkippableFact]
    public async Task An_empty_trail_counts_zero()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var incidents = await CountAsync(Capability.SimulatedExecution, Start, Now);

        Assert.Equal(0, incidents.PolicyBreaches);
        Assert.Equal(0, incidents.ExecutionFailures);
    }

    /// <summary>
    /// An inverted window is a caller defect, and answering it with zeros would report "all clear"
    /// for a question nobody could have asked on purpose.
    /// </summary>
    [SkippableFact]
    public async Task An_inverted_window_is_refused_rather_than_answered()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new EfAuditStatistics(context).CountIncidentsAsync(
                Capability.SimulatedExecution, Now, Start));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<CapabilityIncidents> CountAsync(
        Capability capability,
        DateTime sinceUtc,
        DateTime nowUtc)
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await new EfAuditStatistics(context).CountIncidentsAsync(capability, sinceUtc, nowUtc);
    }

    private static AuditRecord Denied(Capability capability, DateTime atUtc)
    {
        var proposal = Proposal(capability, atUtc);

        return AuditRecord.ForPolicyDecision(
            proposal,
            PolicyDecision.Deny(proposal, "refused for the test", ["test@1"], atUtc),
            atUtc);
    }

    private static AuditRecord ApprovalRequired(Capability capability, DateTime atUtc)
    {
        var proposal = Proposal(capability, atUtc);

        return AuditRecord.ForPolicyDecision(
            proposal,
            PolicyDecision.RequireApproval(proposal, "a person must decide", ["test@1"], atUtc),
            atUtc);
    }

    private static AuditRecord Failed(Capability capability, DateTime atUtc) =>
        Execution(capability, atUtc, succeeded: false);

    private static AuditRecord Executed(Capability capability, DateTime atUtc) =>
        Execution(capability, atUtc, succeeded: true);

    private static AuditRecord Execution(Capability capability, DateTime atUtc, bool succeeded)
    {
        var proposal = Proposal(capability, atUtc);
        var decision = PolicyDecision.Execute(proposal, "permitted for the test", ["test@1"], atUtc);
        var execution = ActionExecution.Start(proposal, decision, atUtc);

        if (succeeded)
        {
            execution.MarkSucceeded(atUtc);
        }
        else
        {
            execution.MarkFailed("the effect threw for the test", atUtc);
        }

        return AuditRecord.ForExecution(proposal, decision, execution, atUtc);
    }

    private static ActionProposal Proposal(Capability capability, DateTime atUtc) =>
        ActionProposal.Create(
            CorrelationId.New(),
            capability,
            ActionType.Create("test.action"),
            ActionTarget.Create("Test"),
            new AuditTestParameters(),
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service("integration-test", "1.0"),
            Guid.NewGuid().ToString("n"),
            atUtc);

    /// <summary>
    /// Written through the sink, which is the only door audit records go through in production.
    /// </summary>
    private async Task WriteAsync(params AuditRecord[] records)
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var sink = new EfAuditSink(context);

        foreach (var record in records)
        {
            await sink.RecordAsync(record);
        }
    }

    private sealed record AuditTestParameters : IActionParameters
    {
        public string Describe() => "audit statistics test parameters";
    }
}
