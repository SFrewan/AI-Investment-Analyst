using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Ai;

/// <summary>
/// Agent runs reach the append-only trail, against a real database.
/// </summary>
/// <remarks>
/// Phase 1 designed the audit record to take agent identity, model and prompt version without a
/// schema change, and Phase 4 is the first phase to test that claim rather than assert it. It also
/// checks the property that matters most about these rows: an agent run is recorded, and it is
/// recorded as something that is not an action.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class AgentAuditTrailTests : IAsyncLifetime
{
    private static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Published = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public AgentAuditTrailTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static EvidenceBundle Bundle() =>
        EvidenceBundle.Create(
            IngestionSubject.Create("Company", "AUDIT1"),
            KnowledgeCutoff.At(Now),
            [
                EvidenceItem.Create(
                    "financials.revenue",
                    Claims.Fact(1000m, Provenance.Create("sec-edgar", PeriodEnd, Published, Published))),
            ]);

    private static AgentDiagnostics Diagnostics() =>
        AgentDiagnostics.Create(
            ModelRef.Create("test", "scripted", "2026-01-01"),
            PromptRef.Create("financial-analyst", "statement-interpretation", 1, 0),
            120,
            60,
            0.0004m,
            31,
            2);

    private static AgentResult<FinancialReading> Accepted(EvidenceBundle bundle) =>
        AgentResults.Ok(
            AgentId.Create("financial"),
            "1.0",
            new FinancialReading("Profitability is stated.", [], [], []),
            Confidence.Create(0.7m),
            [bundle.Items[0].Claim.Id],
            Diagnostics(),
            ["no comparative period was supplied"]);

    private static AgentResult<FinancialReading> Rejected() =>
        AgentResults.Failed<FinancialReading>(
            AgentId.Create("financial"),
            "1.0",
            AgentStatus.Ungrounded,
            "quoted a margin that traces to no claim in the bundle",
            Diagnostics());

    [SkippableFact]
    public async Task An_accepted_agent_run_is_recorded_with_its_model_prompt_and_evidence_hash()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bundle = Bundle();

        await using (var context = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfAuditSink(context).RecordAsync(
                AuditRecord.ForAgentRun(
                    CorrelationId.Create("agent-audit-accepted"),
                    bundle.Subject,
                    bundle.Hash,
                    Accepted(bundle),
                    Now));
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var record = await reader.AuditRecords.AsNoTracking().SingleAsync();

        Assert.Equal(AuditEventType.AgentOutputAccepted, record.EventType);
        Assert.Equal(ProposerKind.AiAgent, record.ActorKind);
        Assert.Equal("financial", record.Actor);
        Assert.Equal(bundle.Hash, record.Details["analysis.evidenceHash"]);
        Assert.Equal("test/scripted@2026-01-01", record.Details["model"]);
        Assert.Equal("financial-analyst/statement-interpretation@v1.0", record.Details["prompt"]);
        Assert.Equal("2", record.Details["run.attempts"]);
        Assert.Equal("0.0004", record.Details["run.costUsd"]);
        Assert.Contains("no comparative period", record.Details["agent.limitations"], StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection rate that quietly falls to zero is a defect in the check, not an improvement in
    /// the model - which is only visible if refusals are recorded as their own event.
    /// </summary>
    [SkippableFact]
    public async Task A_rejected_agent_run_is_recorded_as_its_own_event()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bundle = Bundle();

        await using (var context = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfAuditSink(context).RecordAsync(
                AuditRecord.ForAgentRun(
                    CorrelationId.Create("agent-audit-rejected"),
                    bundle.Subject,
                    bundle.Hash,
                    Rejected(),
                    Now));
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var record = await reader.AuditRecords.AsNoTracking().SingleAsync();

        Assert.Equal(AuditEventType.AgentOutputRejected, record.EventType);
        Assert.Equal("Ungrounded", record.Details["agent.status"]);
        Assert.Contains("traces to no claim", record.Details["agent.explanation"], StringComparison.Ordinal);
    }

    /// <summary>
    /// An agent run is not an action. Nothing about it may acquire a proposal, a decision, an
    /// execution, a capability or a policy outcome - the columns that mean something was authorised.
    /// </summary>
    [SkippableFact]
    public async Task An_agent_run_never_acquires_the_identifiers_of_an_action()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bundle = Bundle();

        await using (var context = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfAuditSink(context).RecordAsync(
                AuditRecord.ForAgentRun(
                    CorrelationId.Create("agent-audit-not-an-action"),
                    bundle.Subject,
                    bundle.Hash,
                    Accepted(bundle),
                    Now));
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var record = await reader.AuditRecords.AsNoTracking().SingleAsync();

        Assert.Null(record.ProposalId);
        Assert.Null(record.DecisionId);
        Assert.Null(record.ExecutionId);
        Assert.Null(record.Capability);
        Assert.Null(record.ActionType);
        Assert.Null(record.Outcome);
        Assert.Null(record.RiskTier);
    }

    /// <summary>
    /// The trail is append-only. An agent run must be no more editable after the fact than any
    /// other entry.
    /// </summary>
    [SkippableFact]
    public async Task A_recorded_agent_run_cannot_be_edited_afterwards()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bundle = Bundle();

        await using (var context = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            await new EfAuditSink(context).RecordAsync(
                AuditRecord.ForAgentRun(
                    CorrelationId.Create("agent-audit-immutable"),
                    bundle.Subject,
                    bundle.Hash,
                    Accepted(bundle),
                    Now));
        }

        await using var context2 = _fixture.CreateContext(new ScopedWriteAuthorization());

        var record = await context2.AuditRecords.SingleAsync();

        context2.Entry(record).Property(nameof(AuditRecord.Summary)).CurrentValue = "rewritten";
        context2.Entry(record).Property(nameof(AuditRecord.Summary)).IsModified = true;

        await Assert.ThrowsAnyAsync<Exception>(() => context2.SaveChangesAsync());
    }
}
