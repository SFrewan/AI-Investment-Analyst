using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Universe;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Universe;

/// <summary>
/// The D2 universe foundation against a real database: schema, round-trip, append-only.
/// </summary>
/// <remarks>
/// The jsonb cohort arrays are the part that cannot be proved in memory - a value converter plus a
/// comparer is exactly the arrangement that looks right in a unit test and loses its ordering on the
/// way back from PostgreSQL. That is what these are for.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class UniversePersistenceTests : IAsyncLifetime
{
    private const string SealedFingerprint = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SealedAt = new(2026, 9, 4, 0, 34, 9, DateTimeKind.Utc);

    private static readonly DateOnly[] Cuts =
    [
        new(2021, 8, 31), new(2022, 8, 31), new(2023, 8, 31), new(2024, 8, 31), new(2025, 8, 31),
    ];

    private readonly PostgresFixture _fixture;

    public UniversePersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task The_universe_tables_exist_and_store_no_span_and_no_current_flag()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(0, await context.Universes.CountAsync());
        Assert.Equal(0, await context.UniverseMemberships.CountAsync());

        var membership = context.Model.FindEntityType(typeof(UniverseMembership))!;
        var columns = membership.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.Contains("cohort_cuts", columns);
        Assert.DoesNotContain(columns, c => c.Contains("span", StringComparison.OrdinalIgnoreCase));

        var universe = context.Model.FindEntityType(typeof(Domain.Universe.Universe))!;
        var universeColumns = universe.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.DoesNotContain(
            universeColumns,
            c => c.Contains("current", StringComparison.OrdinalIgnoreCase)
                || c.Contains("latest", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task A_sealed_universe_and_its_membership_round_trip_with_cuts_intact()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, [Cuts[0], Cuts[1], Cuts[2]]);
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var universe = await reader.Universes.SingleAsync();

        Assert.Equal(SealedFingerprint, universe.Id.Value);
        Assert.Equal(Cuts, universe.CohortCutDates);
        Assert.Equal(new DateOnly(2025, 8, 31), universe.FinalCut);
        Assert.Equal(new DateOnly(2021, 9, 1), universe.WindowFrom);
        Assert.Equal(SealedAt, universe.SealedAtUtc);

        var membership = await reader.UniverseMemberships.SingleAsync();

        Assert.Equal(securityId, membership.SecurityId);
        Assert.Equal([Cuts[0], Cuts[1], Cuts[2]], membership.CohortCuts);
        Assert.False(membership.SurvivedTo(universe.FinalCut));

        // The join D2 exists to make possible: stored cuts, derived span, existing domain rule.
        Assert.Equal(
            (universe.WindowFrom, new DateOnly(2023, 8, 31)),
            MembershipSpan.Derive(
                membership.CohortCuts, universe.FinalCut, universe.WindowFrom, universe.WindowTo));
    }

    [SkippableFact]
    public async Task A_security_cannot_be_a_member_of_one_sealed_version_twice()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await SeedAsync(context, securityId, Cuts);

            context.UniverseMemberships.Add(UniverseMembership.Record(
                Guid.NewGuid(), UniverseId.Create(SealedFingerprint), securityId, [Cuts[0]], Now));

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task A_membership_cannot_name_a_security_that_does_not_exist()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Universes.Add(SealUniverse());

            context.UniverseMemberships.Add(UniverseMembership.Record(
                Guid.NewGuid(), UniverseId.Create(SealedFingerprint), SecurityId.New(), Cuts, Now));

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task A_sealed_universe_cannot_be_modified_or_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                context.Universes.Add(SealUniverse());
                await context.SaveChangesAsync();
            }
        }

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        var stored = await editor.Universes.SingleAsync();
        editor.Entry(stored).Property(u => u.MembershipRule).CurrentValue = "rewritten";

        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            var thrown = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => editor.SaveChangesAsync());

            Assert.Contains("append-only", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task A_stored_membership_cannot_be_modified_or_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, Cuts);
            }
        }

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        editor.UniverseMemberships.Remove(await editor.UniverseMemberships.SingleAsync());

        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await Assert.ThrowsAsync<UnauthorizedWriteException>(() => editor.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task Sealing_a_universe_without_an_authorisation_window_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        context.Universes.Add(SealUniverse());

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// Point-in-time selection against stored rows: the version sealed by the instant, not the newest.
    /// </summary>
    [SkippableFact]
    public async Task A_reader_selects_the_version_sealed_at_or_before_its_instant()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                context.Universes.Add(SealUniverse("older@aaaaaaaaaaaa", older));
                context.Universes.Add(SealUniverse("newer@bbbbbbbbbbbb", newer));
                await context.SaveChangesAsync();
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var asOf = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var selected = await reader.Universes
            .Where(u => u.SealedAtUtc <= asOf)
            .OrderByDescending(u => u.SealedAtUtc)
            .FirstAsync();

        Assert.Equal("older@aaaaaaaaaaaa", selected.Id.Value);
    }

    private static Domain.Universe.Universe SealUniverse(
        string fingerprint = SealedFingerprint,
        DateTime? sealedAtUtc = null) =>
        Domain.Universe.Universe.Seal(
            UniverseId.Create(fingerprint),
            new DateOnly(2021, 9, 1),
            new DateOnly(2026, 8, 31),
            Cuts,
            sealedAtUtc ?? SealedAt,
            ContentHash.Compute("manifest"u8),
            "Assets, ranked 2020, systematic sample",
            "A member is in the universe at a cut if it met the rule at that cut.",
            Now);

    private static async Task SeedAsync(
        AppDbContext context,
        SecurityId securityId,
        IEnumerable<DateOnly> cuts)
    {
        var company = Company.Create(CompanyId.New(), "Example Issuer", Now);

        context.Companies.Add(company);
        context.Securities.Add(Security.Create(securityId, company.Id, Now));
        context.Universes.Add(SealUniverse());

        context.UniverseMemberships.Add(UniverseMembership.Record(
            Guid.NewGuid(), UniverseId.Create(SealedFingerprint), securityId, cuts, Now));

        await context.SaveChangesAsync();
    }
}
