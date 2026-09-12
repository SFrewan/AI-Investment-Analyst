using AI.Investment.Domain.Companies;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Companies;

/// <summary>
/// The D3 bridge against a real database: nullable CIK, unique only where present.
/// </summary>
/// <remarks>
/// "Unique when present" is the part that cannot be proved in memory. PostgreSQL permits many NULLs
/// under a plain unique index, so an unfiltered index would appear to pass every test here while
/// saying something different from what was meant. These tests assert the filter itself as well as
/// the behaviour.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class CompanyCikPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public CompanyCikPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task The_schema_carries_a_nullable_cik_with_a_unique_when_present_index()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var indexes = await context.Database
            .SqlQuery<string>($"select indexdef as \"Value\" from pg_indexes where tablename = 'companies'")
            .ToListAsync();

        var cikIndex = Assert.Single(indexes, i => i.Contains("ix_companies_cik", StringComparison.Ordinal));

        Assert.Contains("UNIQUE", cikIndex, StringComparison.Ordinal);
        Assert.Contains("WHERE (cik IS NOT NULL)", cikIndex, StringComparison.Ordinal);

        // The ticker index went with the ticker at D4; the company table no longer carries one.
        Assert.DoesNotContain(indexes, i => i.Contains("ix_companies_ticker", StringComparison.Ordinal));
        Assert.DoesNotContain(indexes, i => i.Contains("(ticker)", StringComparison.Ordinal));

        var nullable = await context.Database
            .SqlQuery<string>($"select is_nullable as \"Value\" from information_schema.columns where table_name = 'companies' and column_name = 'cik'")
            .SingleAsync();

        Assert.Equal("YES", nullable);
    }

    [SkippableFact]
    public async Task A_cik_round_trips_with_its_leading_zeroes_intact()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var id = CompanyId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                context.Companies.Add(Company.Create(
                    id, "Apple Inc.", Now, cik: Cik.Create("0000320193")));

                await context.SaveChangesAsync();
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await reader.Companies.SingleAsync(c => c.Id == id);

        Assert.Equal("0000320193", stored.Cik!.Value);

        // Read back through the raw column too: a conversion that trimmed the padding would still
        // satisfy the line above if it trimmed on the way in as well.
        var raw = await reader.Database
            .SqlQuery<string>($"select cik as \"Value\" from companies where cik is not null")
            .SingleAsync();

        Assert.Equal("0000320193", raw);
    }

    [SkippableFact]
    public async Task A_company_without_a_cik_persists_as_null()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                context.Companies.Add(Company.Create(
                    CompanyId.New(), "Contoso", Now));

                await context.SaveChangesAsync();
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Null((await reader.Companies.SingleAsync()).Cik);
    }

    [SkippableFact]
    public async Task Many_companies_may_have_no_cik_at_all()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            foreach (var ticker in new[] { "AAA", "BBB", "CCC" })
            {
                context.Companies.Add(Company.Create(
                    CompanyId.New(), $"Company {ticker}", Now));
            }

            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(3, await reader.Companies.CountAsync(c => c.Cik == null));
    }

    [SkippableFact]
    public async Task Two_companies_cannot_claim_the_same_sec_filer()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Companies.Add(Company.Create(
                CompanyId.New(), "First", Now, cik: Cik.Create("0000320193")));

            await context.SaveChangesAsync();

            context.Companies.Add(Company.Create(
                CompanyId.New(), "Second", Now, cik: Cik.Create("0000320193")));

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task Different_ciks_coexist_freely()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Companies.Add(Company.Create(
                CompanyId.New(), "Apple", Now, cik: Cik.Create("0000320193")));

            context.Companies.Add(Company.Create(
                CompanyId.New(), "Microsoft", Now, cik: Cik.Create("0000789019")));

            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(2, await reader.Companies.CountAsync(c => c.Cik != null));
    }
}
