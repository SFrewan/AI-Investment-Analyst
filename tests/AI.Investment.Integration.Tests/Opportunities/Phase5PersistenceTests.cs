using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Opportunities;

/// <summary>
/// The Phase 5 tables against a real PostgreSQL, through the real migrations.
/// </summary>
/// <remarks>
/// <para>
/// Owned types, JSONB converters and shadow-backed collections are the parts most likely to be
/// subtly wrong, and only a real round-trip proves them: a value that serialises and never
/// materialises looks correct in every unit test written against the object graph in memory.
/// </para>
/// <para>
/// The concurrency test is the one worth having. An approval token is single-use, and the in-memory
/// half of that check cannot see a second caller - only the conditional update in the store can.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class Phase5PersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public Phase5PersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task An_opportunity_round_trips_with_its_economics_risk_evidence_and_score()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var evidence = ClaimId.New();
        var proposalId = Guid.NewGuid();
        var opportunity = Proposed(evidence, proposalId);

        await SaveAsync(opportunity);

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await verification.Opportunities
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.OpportunityId == opportunity.OpportunityId);

        Assert.NotNull(stored);
        Assert.Equal(EquityOpportunity.Type, stored!.Type);
        Assert.Equal("Security", stored.Subject.Kind);
        Assert.Equal("AAPL", stored.Subject.Identifier);
        Assert.Equal("equity-screener", stored.Source.DiscovererId.Value);
        Assert.Equal(OpportunityStatus.Proposed, stored.Status);

        Assert.NotNull(stored.Economics);
        Assert.Equal(1_000m, stored.Economics!.EstimatedCost.Amount);
        Assert.Equal(1_200m, stored.Economics.EstimatedRevenue.Amount);
        Assert.Equal("USD", stored.Economics.Currency.Code);

        Assert.NotNull(stored.Risk);
        Assert.Equal(ReversibilityClass.ReversibleWithCost, stored.Risk!.Reversibility);
        Assert.Single(stored.Risk.Evidence);

        Assert.NotNull(stored.Confidence);
        Assert.Equal(0.7m, stored.Confidence!.Value);

        Assert.NotNull(stored.Score);
        Assert.Equal("opportunity.composite-score", stored.Score!.Metric.Value);

        Assert.Equal(evidence, Assert.Single(stored.Evidence));
        Assert.Equal(proposalId, Assert.Single(stored.ProposalIds));
    }

    [SkippableFact]
    public async Task An_opportunity_read_back_moves_through_the_rest_of_its_lifecycle()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var opportunity = Proposed(ClaimId.New(), Guid.NewGuid());

        await SaveAsync(opportunity);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var repository = new EfOpportunityRepository(context);
        var stored = await repository.GetAsync(opportunity.OpportunityId);

        Assert.NotNull(stored);

        var tokenId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        stored!.Approve(tokenId, Now);
        stored.BeginExecution(Now);
        stored.Activate(executionId, Now);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await repository.AddAsync(stored);
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var reloaded = await verification.Opportunities
            .AsNoTracking()
            .FirstAsync(candidate => candidate.OpportunityId == opportunity.OpportunityId);

        Assert.Equal(OpportunityStatus.Active, reloaded.Status);
        Assert.Equal(tokenId, reloaded.ApprovalTokenId);
        Assert.Equal(executionId, reloaded.ExecutionId);
    }

    [SkippableFact]
    public async Task An_approval_token_round_trips_with_its_fingerprint_and_ceiling()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var opportunity = Proposed(ClaimId.New(), Guid.NewGuid());
        var proposal = Proposal(opportunity);
        var token = ApprovalToken.Issue(
            opportunity.OpportunityId,
            proposal,
            proposal.Economics.EstimatedExposure,
            "operator@example.test",
            Now,
            TimeSpan.FromHours(4));

        await StoreAsync(token);

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await new EfApprovalTokenStore(verification).GetAsync(token.ApprovalTokenId);

        Assert.NotNull(stored);
        Assert.Equal(token.Fingerprint.Value, stored!.Fingerprint.Value);
        Assert.Equal(token.MaxAmount.Amount, stored.MaxAmount.Amount);
        Assert.Equal("USD", stored.MaxAmount.Currency.Code);
        Assert.Equal(token.ExpiresAtUtc, stored.ExpiresAtUtc);
        Assert.False(stored.IsConsumed);
        Assert.True(stored.Fingerprint.Matches(proposal));
    }

    [SkippableFact]
    public async Task Only_one_of_two_concurrent_callers_can_consume_an_approval()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var opportunity = Proposed(ClaimId.New(), Guid.NewGuid());
        var proposal = Proposal(opportunity);
        var token = ApprovalToken.Issue(
            opportunity.OpportunityId,
            proposal,
            proposal.Economics.EstimatedExposure,
            "operator@example.test",
            Now,
            TimeSpan.FromHours(4));

        await StoreAsync(token);

        await using var first = _fixture.CreateContext(new ScopedWriteAuthorization());
        await using var second = _fixture.CreateContext(new ScopedWriteAuthorization());

        var results = await Task.WhenAll(
            new EfApprovalTokenStore(first).ConsumeAsync(
                token.ApprovalTokenId, opportunity.OpportunityId, proposal, Now),
            new EfApprovalTokenStore(second).ConsumeAsync(
                token.ApprovalTokenId, opportunity.OpportunityId, proposal, Now));

        Assert.Single(results, refusal => refusal == ApprovalRefusal.None);
        Assert.Single(results, refusal => refusal != ApprovalRefusal.None);
    }

    [SkippableFact]
    public async Task Ledger_entries_round_trip_and_the_stored_books_balance()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var opportunityId = OpportunityId.New();

        var entries = new[]
        {
            LedgerEntry.Post(
                LedgerAccount.Positions,
                LedgerAccount.Cash,
                Money.Create(1_000m, Currency.Usd),
                Now,
                "Bought 10 AAPL",
                opportunityId),
            LedgerEntry.Post(
                LedgerAccount.Fees,
                LedgerAccount.Cash,
                Money.Create(1m, Currency.Usd),
                Now,
                "Fees on AAPL",
                opportunityId),
        };

        var ledgerAuthorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(ledgerAuthorization))
        {
            using (ledgerAuthorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await new EfLedgerStore(context).AppendAsync(entries);
            }
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await new EfLedgerStore(verification).ListForAsync(opportunityId);

        Assert.Equal(2, stored.Count);
        Assert.True(CapitalLedger.IsBalanced(stored, Currency.Usd));
        Assert.Equal(
            1_000m,
            CapitalLedger.Balance(LedgerAccount.Positions, stored, Currency.Usd).Amount);
        Assert.Equal(
            LedgerAccountKind.Expense,
            stored.Single(entry => entry.Debit == LedgerAccount.Fees).Debit.Kind);
    }

    [SkippableFact]
    public async Task A_kill_switch_row_is_read_back_as_engaged()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                context.KillSwitchFlags.Add(
                    KillSwitchFlag.Create(null, engaged: true, "drill", Now));

                await context.SaveChangesAsync();
            }
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var engaged = await verification.KillSwitchFlags
            .AsNoTracking()
            .AnyAsync(flag => flag.Engaged && flag.Capability == null);

        Assert.True(engaged);
    }

    /// <summary>
    /// Stores a token inside an authorisation window.
    /// </summary>
    /// <remarks>
    /// An approval token is a domain write, so the persistence guard refuses it without a decision
    /// that permits it - which is the guard doing its job. In production the window is opened by the
    /// action gateway around the approval action itself; here it is opened explicitly so the test is
    /// about the mapping rather than about the seam, which <c>WriteGuardTests</c> already covers.
    /// </remarks>
    private async Task StoreAsync(ApprovalToken token)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfApprovalTokenStore(context).AddAsync(token);
        }
    }

    private async Task SaveAsync(Opportunity opportunity)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfOpportunityRepository(context).AddAsync(opportunity);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>An opportunity evaluated, ranked and with one proposal recorded against it.</summary>
    private static Opportunity Proposed(ClaimId evidence, Guid proposalId)
    {
        var opportunity = Opportunity.Draft(
            EquityOpportunity.Type,
            IngestionSubject.Create("Security", "AAPL"),
            OpportunitySource.Create("equity-screener", Now),
            "Buy 10 AAPL",
            "The screener found a gap between the entry price and the analyst target.",
            OpportunityDetail.Create(
                EquityOpportunity.Type,
                EquityDetail.ToJson("AAPL", 10m, 100m, 120m, "USD", 0.6m, 90)),
            Now,
            [evidence]);

        opportunity.Evaluate(
            new EquityEconomicsCalculator().Calculate(opportunity, Now),
            OpportunityRisk.Create(
                "A single-name equity position carries issuer and market risk.",
                ReversibilityClass.ReversibleWithCost,
                [ClaimId.New()]),
            Confidence.Create(0.7m),
            Now);

        opportunity.Rank(Score(), Now);
        opportunity.RecordProposal(proposalId, Now);

        return opportunity;
    }

    /// <summary>A dimensionless score, published before the cutoff so the look-ahead guard admits it.</summary>
    private static OpportunityScore Score(decimal value = 0.82m)
    {
        var published = Now.AddDays(-1);

        var provenance = Provenance.Create(
            SourceId.Create("scoring-engine"),
            published,
            published,
            Now);

        var context = CalculationContext.Create(
            IngestionSubject.Create("Security", "AAPL"),
            KnowledgeCutoff.At(Now),
            Now);

        return OpportunityScore.From(MetricResult.Create(
            context,
            MetricId.Create("opportunity.composite-score"),
            MetricValue.Ratio(value),
            "the shipped scoring specification",
            SourceId.Create("scoring-engine"),
            CalculationVersion.Create(1, 0),
            published,
            [CalculationInput.Create("financial-health", Claims.Fact(value, provenance), UnitOfMeasure.Ratio)]));
    }

    private static ActionProposal Proposal(Opportunity opportunity) =>
        Application.Execution.SimulatedExecutionProposal.For(
            opportunity,
            Application.Execution.VenueOrder.Create(
                "AAPL",
                Application.Execution.OrderSide.Buy,
                10m,
                Money.Create(100m, Currency.Usd),
                opportunity.OpportunityId,
                Guid.NewGuid(),
                Guid.NewGuid().ToString("n")),
            ProposedBy.Service("opportunity-executor", "1.0"),
            CorrelationId.New(),
            Now);
}
