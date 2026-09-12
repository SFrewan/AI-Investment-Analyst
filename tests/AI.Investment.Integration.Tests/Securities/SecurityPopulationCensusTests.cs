using System.Text;
using AI.Investment.Application.Securities;
using AI.Investment.Domain.Securities;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Identity;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Integration.Tests.Securities;

/// <summary>
/// Populates the test database from the sealed declaration and writes the census G2 reports.
/// </summary>
/// <remarks>
/// Every figure is read back from the database after the run rather than taken from the result
/// object, so a writer that reported more than it wrote would be caught here rather than believed.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SecurityPopulationCensusTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SecurityPopulationCensusTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task The_population_census_is_deterministic_and_matches_the_database()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var populate = new PopulateSecuritiesFromDeclaration(
            declaration,
            new SecurityRepository(context),
            new CompanyRepository(context),
            new UnitOfWork(context));

        SecurityPopulationResult result;

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            result = await populate.ExecuteAsync(Now);
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var securities = await reader.Securities.CountAsync();
        var companies = await reader.Companies.CountAsync();

        var identifiers = (await reader.Securities
                .Include(s => s.Identifiers)
                .AsNoTracking()
                .ToListAsync())
            .SelectMany(s => s.Identifiers)
            .ToList();

        var report = new StringBuilder();

        report.AppendLine("D6 DECLARATION");
        report.AppendLine($"  DeclarationId        : {result.DeclarationId}");
        report.AppendLine($"  IdentityDigest       : {result.IdentityDigest}");
        report.AppendLine($"  EvidenceBase         : {declaration.EvidenceBaseFingerprint}");
        report.AppendLine();
        report.AppendLine("POPULATION CENSUS (from the run)");
        report.AppendLine($"  members read         : {result.MembersRead}");
        report.AppendLine($"  securities created   : {result.SecuritiesCreated}");
        report.AppendLine($"  already present      : {result.AlreadyPresent}");
        report.AppendLine($"  refused              : {result.Refused}");
        report.AppendLine($"  vendor identifiers   : {result.VendorSymbolIdentifiersCreated}");
        report.AppendLine($"  ticker identifiers   : {result.TickerIdentifiersCreated}");
        report.AppendLine();
        report.AppendLine("REFUSALS BY REASON");

        foreach (var (reason, count) in result.RefusalsByReason.OrderByDescending(p => p.Value))
        {
            report.AppendLine($"  {reason,-28} {count}");
        }

        report.AppendLine();
        report.AppendLine("DATABASE AFTER THE RUN");
        report.AppendLine($"  securities rows      : {securities}");
        report.AppendLine($"  companies rows       : {companies}");
        report.AppendLine($"  identifier rows      : {identifiers.Count}");
        report.AppendLine(
            $"  identifier kinds     : {string.Join(", ", identifiers.Select(i => i.Kind).Distinct())}");
        report.AppendLine(
            $"  identifier sources   : {string.Join(", ", identifiers.Select(i => i.SourceId.Value).Distinct())}");

        _output.WriteLine(report.ToString());

        // The census and the database agree, because the census is not allowed to be the record.
        Assert.Equal(400, result.MembersRead);
        Assert.Equal(securities, result.SecuritiesCreated);
        Assert.Equal(securities, identifiers.Count);
        Assert.Equal(securities, companies);
        Assert.Equal(0, result.TickerIdentifiersCreated);
        Assert.DoesNotContain(identifiers, i => i.Kind != SecurityIdentifierKind.VendorSymbol);

        // Every member is accounted for exactly once, refusals included.
        Assert.Equal(
            result.MembersRead,
            result.SecuritiesCreated + result.AlreadyPresent + result.Refused);

        Assert.Equal(result.Refused, result.RefusalsByReason.Values.Sum());

        // Nothing was acquired and nothing corporate was attached by this run.
        Assert.Equal(0, await reader.IngestionRuns.CountAsync());
        Assert.Equal(0, await reader.Observations.CountAsync());
        Assert.Equal(0, await reader.Determinations.CountAsync());
        Assert.Equal(0, await reader.ListingEvents.CountAsync());
        Assert.Equal(0, await reader.Listings.CountAsync());
        Assert.Equal(0, await reader.Venues.CountAsync());
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
