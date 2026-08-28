using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Autonomy;

/// <summary>
/// The bounded-autonomy tables against a real PostgreSQL, through the real migrations.
/// </summary>
/// <remarks>
/// <para>
/// Three claims here cannot be established anywhere else. That a warrant round-trips with its
/// evidence intact - the run id, the fingerprint, the ceilings - because a warrant that loses the
/// argument behind it on the way to the database is a permission with nothing underneath it. That one
/// venue in one environment has at most one authorisation, because only a unique index can enforce
/// that against two writers. And that the write guard refuses to delete either, because the record
/// of what was permitted has to survive the permission being withdrawn.
/// </para>
/// <para>
/// Both tables are expected to be empty in a running installation. They are written here so the shape
/// is exercised at all rather than first discovered on the day somebody needs it.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class Phase8PersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = JustifiedEvidence.Now;

    private readonly PostgresFixture _fixture;

    public Phase8PersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A warrant keeps the evidence it was argued from across a round trip.</summary>
    [SkippableFact]
    public async Task A_warrant_round_trips_with_the_evidence_behind_it()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var warrant = JustifiedEvidence.Warrant(actionType: "execution.simulated-order");

        await WriteAsync(context => context.PromotionWarrants.AddAsync(warrant).AsTask());

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await new EfPromotionWarrantStore(verification)
            .FindAsync(warrant.PromotionWarrantId);

        Assert.NotNull(stored);
        Assert.Equal(warrant.Capability, stored!.Capability);
        Assert.Equal(warrant.ActionType, stored.ActionType);
        Assert.Equal(AutonomyMode.AutoExecuteBounded, stored.MaxMode);
        Assert.Equal(RiskTier.Low, stored.MaxRiskTier);
        Assert.Equal(warrant.MaxExposure.Amount, stored.MaxExposure.Amount);
        Assert.Equal(warrant.MaxExposure.Currency, stored.MaxExposure.Currency);
        Assert.Equal(warrant.ValidationRunId, stored.ValidationRunId);
        Assert.Equal(warrant.BenchmarkFingerprint, stored.BenchmarkFingerprint);
        Assert.Equal(warrant.IssuedBy, stored.IssuedBy);
        Assert.True(stored.IsActive(Now));
    }

    /// <summary>
    /// A grant written under a warrant keeps the reference, which is what lets the circuit breaker
    /// ask later whether the evidence still holds.
    /// </summary>
    [SkippableFact]
    public async Task A_bounded_grant_keeps_its_warrant_reference()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var warrant = JustifiedEvidence.Warrant();

        var grant = AutonomyGrant.IssueBounded(
            warrant, null, "Test", AutonomyMode.AutoExecuteBounded, RiskTier.Low,
            JustifiedEvidence.Usd(1_000m), "limits.default", "operator@example.test",
            Now, TimeSpan.FromDays(7));

        await WriteAsync(async context =>
        {
            await context.PromotionWarrants.AddAsync(warrant);
            await context.AutonomyGrants.AddAsync(grant);
        });

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await verification.AutonomyGrants
            .FirstOrDefaultAsync(g => g.AutonomyGrantId == grant.AutonomyGrantId);

        Assert.NotNull(stored);
        Assert.Equal(warrant.PromotionWarrantId, stored!.PromotionWarrantId);
        Assert.Equal(AutonomyMode.AutoExecuteBounded, stored.GrantedMode);
    }

    /// <summary>
    /// One venue in one environment has at most one authorisation. Only the database can enforce
    /// that against two writers, and two sets of signatures over the same real money is exactly the
    /// ambiguity that must not exist.
    /// </summary>
    [SkippableFact]
    public async Task A_venue_may_have_only_one_authorisation_in_an_environment()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var warrant = JustifiedEvidence.Warrant();

        await WriteAsync(async context =>
        {
            await context.PromotionWarrants.AddAsync(warrant);
            await context.LiveVenueAuthorizations.AddAsync(Authorization(warrant));
        });

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            WriteAsync(context => context.LiveVenueAuthorizations.AddAsync(Authorization(warrant)).AsTask()));
    }

    /// <summary>
    /// The record of what was permitted survives the permission being withdrawn. A warrant is
    /// revoked, never deleted, and the guard refuses the delete at the context.
    /// </summary>
    [SkippableFact]
    public async Task A_warrant_is_revoked_rather_than_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var warrant = JustifiedEvidence.Warrant();

        await WriteAsync(context => context.PromotionWarrants.AddAsync(warrant).AsTask());

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var stored = await context.PromotionWarrants
            .FirstAsync(w => w.PromotionWarrantId == warrant.PromotionWarrantId);

        context.PromotionWarrants.Remove(stored);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            // Refused even inside an authorisation window. Authorisation permits an effect; it has
            // never permitted erasing the record of a permission that was once in force.
            var error = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => context.SaveChangesAsync());

            Assert.Contains("never by deleting them", error.Message, StringComparison.Ordinal);
        }

        context.ChangeTracker.Clear();

        // Revoking is the supported path, and it leaves the row in place with a reason on it.
        var revocable = await context.PromotionWarrants
            .FirstAsync(w => w.PromotionWarrantId == warrant.PromotionWarrantId);

        revocable.Revoke("the evidence was re-examined and did not hold.", Now.AddHours(1));

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await context.SaveChangesAsync();
        }

        await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

        var after = await verification.PromotionWarrants
            .FirstAsync(w => w.PromotionWarrantId == warrant.PromotionWarrantId);

        Assert.True(after.IsRevoked);
        Assert.Contains("did not hold", after.RevocationReason!, StringComparison.Ordinal);
        Assert.False(after.IsActive(Now.AddHours(2)));
    }

    /// <summary>
    /// The state a running installation is actually in: both tables empty, and the store says so
    /// rather than failing.
    /// </summary>
    [SkippableFact]
    public async Task With_nothing_promoted_both_tables_are_empty()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Empty(await new EfPromotionWarrantStore(context).GetAllAsync());
        Assert.Empty(await new EfLiveVenueAuthorizationStore(context).GetAllAsync());

        Assert.Null(await new EfLiveVenueAuthorizationStore(context).FindForAsync("venue-x", "Test"));

        Assert.Empty(await new EfPromotionWarrantStore(context)
            .GetActiveAsync(Capability.SimulatedExecution, "Test", Now));
    }

    private static LiveVenueAuthorization Authorization(PromotionWarrant warrant) =>
        LiveVenueAuthorization.Create(
            "venue-x",
            "Test",
            warrant,
            "first@example.test",
            "second@example.test",
            "both of us have read the evidence.",
            JustifiedEvidence.Usd(1_000m),
            Now,
            TimeSpan.FromDays(1));

    /// <summary>
    /// Writes through the seam, because a warrant is ordinary domain state rather than seam
    /// bookkeeping - the guard requires an authorisation window for it, exactly as it should.
    /// </summary>
    private async Task WriteAsync(Func<Infrastructure.Persistence.AppDbContext, Task> write)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        await write(context);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await context.SaveChangesAsync();
        }
    }
}
