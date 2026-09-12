using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Companies;
using AI.Investment.Application.Securities;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Identity;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Securities;

/// <summary>
/// Point-in-time resolution against the real population G2 and G3 produce.
/// </summary>
/// <remarks>
/// The unit tests prove the resolver decides correctly against a fake. These prove the half that
/// only a database can: that the interval predicate translates and is applied by PostgreSQL rather
/// than in memory, that the uniqueness invariant makes ambiguity unreachable in persistence, and
/// that resolution writes nothing.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SecurityResolutionTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
    // The registered end-of-day price feed, which is what the active declaration names as the
    // authority behind every vendor-symbol assertion. Not "eodhd", which is a vendor label and no
    // registered source at all.
    private static readonly SourceId Eodhd = SourceId.Create("eodhd-eod");
    private static readonly SourceId Edgar = SourceId.Create("sec-edgar");

    private readonly PostgresFixture _fixture;

    public SecurityResolutionTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A. Every security G2 populates resolves at a date inside its own vendor series.
    /// </summary>
    /// <remarks>
    /// All 77 rather than one, because a resolver that worked for the first row and not the
    /// thousandth would pass a sampled test. Each is asked at its own <c>ValidFrom</c>, which is
    /// also the lower boundary case for that member.
    /// </remarks>
    [SkippableFact]
    public async Task Every_populated_security_resolves_at_the_start_of_its_own_series()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var resolver = new SecurityResolver(new SecurityRepository(context));

        var expected = declaration.Members
            .Where(m => m.VendorSeries is not null
                && m.IdentityClass == SecurityIdentityEvidence.IssuerStatedSecurityIdentity
                && m.Symbol?.SecurityTitleObserved == SecurityIdentityEvidence.EquityTitle)
            .ToList();

        Assert.NotEmpty(expected);

        foreach (var member in expected)
        {
            var series = member.VendorSeries!;

            var result = await resolver.ResolveAsync(
                SecurityIdentifierKind.VendorSymbol,
                series.Value,
                Eodhd,
                series.FirstBarDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

            Assert.True(
                result.IsResolved,
                $"{series.Value} did not resolve at its own first bar {series.FirstBarDate:O}: "
                    + result.Failure);
        }

        // And the count matches what G2 created, so nothing resolved that should not exist.
        Assert.Equal(expected.Count, await context.Securities.CountAsync());
    }

    /// <summary>E, F, G. The boundaries, against a real row and a real interval.</summary>
    [SkippableFact]
    public async Task The_interval_boundaries_hold_against_the_database()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        // A member whose series ended - so both ends of the interval are real dates rather than an
        // open end that cannot be tested from above.
        var closed = declaration.Members
            .Where(m => m.VendorSeries is not null)
            .Select(m => m.VendorSeries!)
            .First(v => v.LastBarDate < new DateOnly(2026, 8, 31));

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var resolver = new SecurityResolver(new SecurityRepository(context));

        async Task<SecurityResolution> At(DateOnly date) =>
            await resolver.ResolveAsync(
                SecurityIdentifierKind.VendorSymbol,
                closed.Value,
                Eodhd,
                date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        Assert.True((await At(closed.FirstBarDate)).IsResolved);
        Assert.True((await At(closed.LastBarDate)).IsResolved);

        var before = await At(closed.FirstBarDate.AddDays(-1));
        var after = await At(closed.LastBarDate.AddDays(1));

        Assert.True(before.IsUnresolved);
        Assert.True(after.IsUnresolved);
        Assert.Equal(SecurityResolutionFailure.OutsideIdentifierValidity, before.Failure);
        Assert.Equal(SecurityResolutionFailure.OutsideIdentifierValidity, after.Failure);
    }

    /// <summary>B, C, D. Wrong source, wrong kind, unknown value.</summary>
    [SkippableFact]
    public async Task A_wrong_source_a_wrong_kind_and_an_unknown_value_all_fail_distinctly()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        var held = declaration.Members.First(m => m.VendorSeries is not null).VendorSeries!;

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var resolver = new SecurityResolver(new SecurityRepository(context));

        var inside = held.FirstBarDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var wrongSource = await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, held.Value, Edgar, inside);

        var wrongKind = await resolver.ResolveAsync(
            SecurityIdentifierKind.Ticker, held.Value, Eodhd, inside);

        var unknown = await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, "NOT-A-REAL-SYMBOL.US", Eodhd, inside);

        Assert.Equal(SecurityResolutionFailure.NoMatchingSource, wrongSource.Failure);
        Assert.Equal(SecurityResolutionFailure.NoTemporalIdentifierEvidence, wrongKind.Failure);
        Assert.Equal(SecurityResolutionFailure.IdentifierNotPersisted, unknown.Failure);
    }

    /// <summary>
    /// H. No ticker resolves at any date, because no ticker interval is evidenced.
    /// </summary>
    /// <remarks>
    /// Asked with the issuer-stated symbol the declaration really carries, so this is not a
    /// straw-man value. D6 records historical ticker validity as evidenced for 0 of 400, and this
    /// is what that means when something asks.
    /// </remarks>
    [SkippableFact]
    public async Task No_issuer_stated_ticker_resolves_at_any_date()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var resolver = new SecurityResolver(new SecurityRepository(context));

        var tickers = declaration.Members
            .Where(m => m.Symbol is { IsAccepted: true, IsIssuerStated: true })
            .Select(m => m.Symbol!.Value)
            .Take(25)
            .ToList();

        Assert.NotEmpty(tickers);

        foreach (var ticker in tickers)
        {
            foreach (var year in new[] { 2021, 2023, 2026 })
            {
                var result = await resolver.ResolveAsync(
                    SecurityIdentifierKind.Ticker,
                    ticker,
                    Edgar,
                    new DateTime(year, 9, 1, 0, 0, 0, DateTimeKind.Utc));

                Assert.True(result.IsUnresolved, $"{ticker} resolved as a ticker at {year}");
                Assert.Equal(SecurityResolutionFailure.NoTemporalIdentifierEvidence, result.Failure);
            }
        }

        Assert.Equal(0, await CountIdentifiersOfKind(context, SecurityIdentifierKind.Ticker));
    }

    /// <summary>
    /// I, N. Company population does not make an unresolved security resolvable.
    /// </summary>
    /// <remarks>
    /// The whole point of G3 being independent of G2. After G3 there are 400 companies and still
    /// only 77 securities, and the members whose instruments were never evidenced - the ambiguous
    /// six among them - resolve to nothing at any date.
    /// </remarks>
    [SkippableFact]
    public async Task Populating_companies_does_not_make_an_ambiguous_member_resolvable()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);
        await PopulateCompaniesAsync(declaration);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(declaration.Members.Count, await context.Companies.CountAsync());

        var resolver = new SecurityResolver(new SecurityRepository(context));

        var ambiguous = declaration.Members
            .Where(m => m.AmbiguityStatus == "refused-multiple-candidates")
            .ToList();

        Assert.NotEmpty(ambiguous);

        foreach (var member in ambiguous)
        {
            Assert.Null(member.VendorSeries);

            // Whatever candidate the recovery recorded, asked both ways, at three dates.
            var candidate = member.Symbol?.Value ?? member.RejectedSymbol ?? member.Cik;

            foreach (var kind in new[] { SecurityIdentifierKind.VendorSymbol, SecurityIdentifierKind.Ticker })
            {
                foreach (var year in new[] { 2021, 2023, 2026 })
                {
                    var result = await resolver.ResolveAsync(
                        kind, candidate, Eodhd, new DateTime(year, 9, 1, 0, 0, 0, DateTimeKind.Utc));

                    Assert.False(
                        result.IsResolved,
                        $"ambiguous member {member.Cik} resolved via {kind} {candidate} at {year}");
                }
            }
        }
    }

    /// <summary>K. The database makes two assertions of one kind and value unreachable.</summary>
    /// <remarks>
    /// Section 8 of the brief asks that a uniqueness invariant be verified rather than worked
    /// around. This is that verification: ambiguity is a state the resolver handles and the schema
    /// refuses to produce.
    /// </remarks>
    [SkippableFact]
    public async Task The_schema_refuses_a_second_assertion_of_the_same_kind_and_value()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        var held = declaration.Members.First(m => m.VendorSeries is not null).VendorSeries!;

        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            var company = await context.Companies.FirstAsync();
            var rival = Security.Create(SecurityId.New(), company.Id, Now);

            rival.AssertIdentifier(SecurityIdentifier.Assert(
                SecurityIdentifierKind.VendorSymbol,
                held.Value,
                held.FirstBarDate,
                held.LastBarDate,
                Eodhd));

            context.Securities.Add(rival);

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    /// <summary>M. Repeated resolution against one database state gives one answer.</summary>
    [SkippableFact]
    public async Task Repeated_resolution_against_the_same_state_is_identical()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        var held = declaration.Members.First(m => m.VendorSeries is not null).VendorSeries!;

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var resolver = new SecurityResolver(new SecurityRepository(context));

        var instant = held.FirstBarDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var answers = new List<SecurityId?>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            answers.Add((await resolver.ResolveAsync(
                SecurityIdentifierKind.VendorSymbol, held.Value, Eodhd, instant)).SecurityId);
        }

        Assert.Single(answers.Distinct());
        Assert.NotNull(answers[0]);
    }

    /// <summary>O, Q, R, S. Resolution mutates nothing.</summary>
    [SkippableFact]
    public async Task Resolution_writes_nothing_at_all()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        await PopulateSecuritiesAsync(declaration);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var before = (
            Securities: await context.Securities.CountAsync(),
            Companies: await context.Companies.CountAsync(),
            Identifiers: await CountIdentifiersOfKind(context, SecurityIdentifierKind.VendorSymbol),
            Listings: await context.Listings.CountAsync(),
            Venues: await context.Venues.CountAsync(),
            Determinations: await context.Determinations.CountAsync(),
            Observations: await context.Observations.CountAsync());

        var resolver = new SecurityResolver(new SecurityRepository(context));
        var held = declaration.Members.First(m => m.VendorSeries is not null).VendorSeries!;

        // Resolve a hit, a miss and a refusal, with no authorisation window open anywhere.
        await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, held.Value, Eodhd,
            held.FirstBarDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, "NOPE.US", Eodhd, Now);

        await resolver.ResolveAsync(SecurityIdentifierKind.Ticker, "TBCH", Edgar, Now);

        var after = (
            Securities: await context.Securities.CountAsync(),
            Companies: await context.Companies.CountAsync(),
            Identifiers: await CountIdentifiersOfKind(context, SecurityIdentifierKind.VendorSymbol),
            Listings: await context.Listings.CountAsync(),
            Venues: await context.Venues.CountAsync(),
            Determinations: await context.Determinations.CountAsync(),
            Observations: await context.Observations.CountAsync());

        Assert.Equal(before, after);

        // And nothing was even staged, so a later unrelated save could not flush a resolution.
        Assert.DoesNotContain(context.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    private static async Task<int> CountIdentifiersOfKind(AppDbContext context, SecurityIdentifierKind kind) =>
        (await context.Securities.Include(s => s.Identifiers).AsNoTracking().ToListAsync())
            .SelectMany(s => s.Identifiers)
            .Count(i => i.Kind == kind);

    private async Task PopulateSecuritiesAsync(ISecurityIdentityDeclaration declaration)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var populate = new PopulateSecuritiesFromDeclaration(
            declaration,
            new SecurityRepository(context),
            new CompanyRepository(context),
            new UnitOfWork(context));

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await populate.ExecuteAsync(Now);
        }
    }

    private async Task PopulateCompaniesAsync(ISecurityIdentityDeclaration declaration)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var populate = new PopulateCompaniesFromDeclaration(
            declaration,
            new CompanyRepository(context),
            new UnitOfWork(context));

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await populate.ExecuteAsync(Now);
        }
    }

    private static string ReadDeclaration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, SecurityIdentityDeclaration.RelativePath)))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No repository root containing {SecurityIdentityDeclaration.RelativePath} was found.");

        return File.ReadAllText(
            Path.Combine(directory!.FullName, SecurityIdentityDeclaration.RelativePath));
    }
}
