using System.Text;
using AI.Investment.Application.Companies;
using AI.Investment.Application.Securities;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Securities;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Identity;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Integration.Tests.Companies;

/// <summary>
/// G3 against a real database: both census cases, and the separation from Security population.
/// </summary>
/// <remarks>
/// <para>
/// The two cases are the point. An empty database and G2's post-state must both end at the same
/// number of companies by different routes - all created, or 77 found and the rest created - and a
/// writer that was idempotent only in the first case would pass a weaker test and fail in practice.
/// </para>
/// <para>
/// Every figure is read back from the database after the run. Nothing here calls a provider, and the
/// only input is the tracked declaration file.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class CompanyPopulationFromDeclarationTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CompanyPopulationFromDeclarationTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// CASE A — an empty database. Every eligible member becomes a company.
    /// </summary>
    /// <remarks>
    /// The totals are asserted as relationships against the declaration rather than as the literal
    /// 400, so a re-sealed declaration changes the count without quietly changing the rule.
    /// </remarks>
    [SkippableFact]
    public async Task An_empty_database_receives_every_eligible_member()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());
        var eligible = CompanyPopulationPolicy.Decide(declaration.Members).Count(d => d.IsEligible);

        var result = await PopulateAsync(declaration);

        Assert.Equal(declaration.Members.Count, result.MembersRead);
        Assert.Equal(eligible, result.Eligible);
        Assert.Equal(eligible, result.Created);
        Assert.Equal(0, result.AlreadyExisting);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(eligible, result.FinalCompanies);

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(eligible, await reader.Companies.CountAsync());

        // Every member of the sealed declaration is eligible, which is G3R's finding stated as a
        // derived property rather than as a number.
        Assert.Equal(declaration.Members.Count, eligible);

        _output.WriteLine(Census("CASE A - empty database", declaration, result));
    }

    /// <summary>
    /// CASE B — G2's post-state. The 77 it created are found, and the rest are added.
    /// </summary>
    [SkippableFact]
    public async Task The_g2_post_state_gains_only_the_companies_it_did_not_already_hold()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        // Reproduce G2 exactly, through its own writer rather than by seeding rows by hand.
        var g2 = await PopulateSecuritiesAsync(declaration);

        await using (var afterG2 = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            Assert.Equal(g2.SecuritiesCreated, await afterG2.Companies.CountAsync());
        }

        var result = await PopulateAsync(declaration);

        Assert.Equal(g2.SecuritiesCreated, result.AlreadyExisting);
        Assert.Equal(result.Eligible - g2.SecuritiesCreated, result.Created);
        Assert.Equal(result.Eligible, result.FinalCompanies);

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(result.Eligible, await reader.Companies.CountAsync());

        // G2's securities are untouched, and G3 created none of its own.
        Assert.Equal(g2.SecuritiesCreated, await reader.Securities.CountAsync());

        _output.WriteLine(Census("CASE B - G2 post-state", declaration, result));
        _output.WriteLine($"  G2 securities before and after G3 : {g2.SecuritiesCreated}");
    }

    /// <summary>
    /// C. Costamare is created, and gains no instrument on the way in.
    /// </summary>
    /// <remarks>
    /// The member G3R turned on. Its security identity is still unresolved - a vendor name match
    /// nobody authoritative made - and a company row says who the issuer is without saying anything
    /// about what it traded.
    /// </remarks>
    [SkippableFact]
    public async Task Costamare_becomes_a_company_and_gains_no_security()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        var member = declaration.Members.Single(m => m.Cik == "0001503584");

        // What the declaration says about it, restated so the test fails loudly if D6 is re-sealed
        // with different evidence for this member.
        Assert.Equal("COSTAMARE INC.", member.IssuerName);
        Assert.Equal("vendor-symbol-claim-only", member.IdentityClass);
        Assert.Null(member.VendorSeries);
        Assert.False(member.Symbol!.IsAccepted);

        await PopulateAsync(declaration);

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        // Compared as the whole value object. The converter maps Cik to a string column, so
        // reaching inside it with .Value gives EF an expression it cannot translate.
        var costamare = Cik.Create("0001503584");

        var company = await reader.Companies.SingleAsync(c => c.Cik == costamare);

        Assert.Equal("COSTAMARE INC.", company.Name);
        Assert.Equal("0001503584", company.Cik!.Value);
        Assert.Null(company.Sector);
        Assert.Null(company.Country);

        // No instrument was inferred from the company.
        Assert.False(await reader.Securities.AnyAsync(s => s.CompanyId == company.Id));
    }

    /// <summary>D, E. Canonical CIKs, leading zeros intact, and no duplicates survive.</summary>
    [SkippableFact]
    public async Task Every_stored_cik_is_canonical_distinct_and_keeps_its_leading_zeros()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await PopulateAsync(SecurityIdentityDeclaration.Parse(ReadDeclaration()));

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var ciks = await reader.Companies
            .Where(c => c.Cik != null)
            .Select(c => c.Cik!.Value)
            .ToListAsync();

        Assert.All(ciks, c => Assert.Matches("^[0-9]{10}$", c));
        Assert.All(ciks, c => Assert.StartsWith("0", c, StringComparison.Ordinal));
        Assert.Equal(ciks.Count, ciks.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>L. Running it twice against the same database changes nothing the second time.</summary>
    [SkippableFact]
    public async Task A_second_identical_run_is_a_no_op()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        var first = await PopulateAsync(declaration);
        var second = await PopulateAsync(declaration);

        Assert.Equal(0, second.Created);
        Assert.Equal(first.Created, second.AlreadyExisting);
        Assert.Equal(first.FinalCompanies, second.FinalCompanies);

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(first.FinalCompanies, await reader.Companies.CountAsync());
    }

    /// <summary>Q, R, S. Nothing outside the company aggregate is written.</summary>
    [SkippableFact]
    public async Task The_run_writes_no_security_identifier_listing_venue_or_determination()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await PopulateAsync(SecurityIdentityDeclaration.Parse(ReadDeclaration()));

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(0, await reader.Securities.CountAsync());
        Assert.Equal(0, await reader.Listings.CountAsync());
        Assert.Equal(0, await reader.ListingEvents.CountAsync());
        Assert.Equal(0, await reader.Venues.CountAsync());
        Assert.Equal(0, await reader.Determinations.CountAsync());
        Assert.Equal(0, await reader.Observations.CountAsync());
        Assert.Equal(0, await reader.IngestionRuns.CountAsync());

        var identifiers = (await reader.Securities
                .Include(s => s.Identifiers)
                .AsNoTracking()
                .ToListAsync())
            .SelectMany(s => s.Identifiers)
            .ToList();

        Assert.Empty(identifiers);
    }

    /// <summary>Creating a company outside an authorisation window is refused, as for any write.</summary>
    [SkippableFact]
    public async Task Populating_without_an_authorisation_window_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var populate = new PopulateCompaniesFromDeclaration(
            SecurityIdentityDeclaration.Parse(ReadDeclaration()),
            new CompanyRepository(context),
            new UnitOfWork(context));

        await Assert.ThrowsAsync<Application.Abstractions.UnauthorizedWriteException>(
            () => populate.ExecuteAsync(Now));
    }

    private async Task<CompanyPopulationResult> PopulateAsync(
        Application.Abstractions.ISecurityIdentityDeclaration declaration)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        var populate = new PopulateCompaniesFromDeclaration(
            declaration,
            new CompanyRepository(context),
            new UnitOfWork(context));

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            return await populate.ExecuteAsync(Now);
        }
    }

    private async Task<SecurityPopulationResult> PopulateSecuritiesAsync(
        Application.Abstractions.ISecurityIdentityDeclaration declaration)
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
            return await populate.ExecuteAsync(Now);
        }
    }

    private static string Census(
        string label,
        SecurityIdentityDeclaration declaration,
        CompanyPopulationResult result)
    {
        var report = new StringBuilder();

        report.AppendLine(label);
        report.AppendLine($"  DeclarationId   : {result.DeclarationId}");
        report.AppendLine($"  IdentityDigest  : {result.IdentityDigest}");
        report.AppendLine($"  EvidenceBase    : {declaration.EvidenceBaseFingerprint}");
        report.AppendLine($"  MembersRead     : {result.MembersRead}");
        report.AppendLine($"  Eligible        : {result.Eligible}");
        report.AppendLine($"  Created         : {result.Created}");
        report.AppendLine($"  AlreadyExisting : {result.AlreadyExisting}");
        report.AppendLine($"  Rejected        : {result.Rejected}");
        report.AppendLine($"  InvalidCik      : {result.InvalidCik}");
        report.AppendLine($"  MissingName     : {result.MissingName}");
        report.AppendLine($"  DuplicateInput  : {result.DuplicateInput}");
        report.AppendLine($"  FinalCompanies  : {result.FinalCompanies}");

        return report.ToString();
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
