using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Securities;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Ingestion;
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
/// G2 against a real database: the identifier schema, the population run, and its idempotency.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests prove the writer decides correctly. These prove the other half - that the schema
/// holds what it produces, that the uniqueness is enforced by the database rather than only by the
/// writer's memory, and that the sealed declaration on disk parses, verifies and populates.
/// </para>
/// <para>
/// Nothing here calls a provider or consumes an acquisition authorisation. The only input is the
/// tracked declaration file, and the rows are created by these tests and truncated between them.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SecurityIdentityPopulationTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public SecurityIdentityPopulationTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The migration produced the identifier table with its interval, source and uniqueness.</summary>
    [SkippableFact]
    public async Task The_identifier_table_exists_with_an_interval_a_source_and_a_uniqueness_rule()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var identifier = context.Model.FindEntityType(typeof(Security))!
            .GetNavigations()
            .Single(n => n.Name == nameof(Security.Identifiers))
            .TargetEntityType;

        var columns = identifier.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.Contains("kind", columns);
        Assert.Contains("value", columns);
        Assert.Contains("valid_from", columns);
        Assert.Contains("valid_to", columns);
        Assert.Contains("source_id", columns);

        // The interval is part of the assertion: an identifier with no start is not evidence.
        Assert.False(identifier.FindProperty(nameof(SecurityIdentifier.ValidFrom))!.IsNullable);

        // An open end is a different statement from an end that never comes.
        Assert.True(identifier.FindProperty(nameof(SecurityIdentifier.ValidTo))!.IsNullable);

        var unique = identifier.GetIndexes()
            .Single(i => i.GetDatabaseName() == "ux_security_identifiers_kind_value");

        Assert.True(unique.IsUnique);
        Assert.Equal(
            ["kind", "value"],
            unique.Properties.Select(p => p.GetColumnName()));
    }

    /// <summary>B. An assertion round-trips with its interval and source intact.</summary>
    [SkippableFact]
    public async Task A_vendor_symbol_assertion_round_trips_through_the_database()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, "AE.US");
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var security = await reader.Securities
            .Include(s => s.Identifiers)
            .SingleAsync(s => s.Id == securityId);

        var identifier = Assert.Single(security.Identifiers);

        Assert.Equal(SecurityIdentifierKind.VendorSymbol, identifier.Kind);
        Assert.Equal("AE.US", identifier.Value);
        Assert.Equal(new DateOnly(2021, 9, 1), identifier.ValidFrom);
        Assert.Equal(new DateOnly(2025, 2, 4), identifier.ValidTo);
        Assert.Equal("eodhd", identifier.SourceId.Value);

        // The point-in-time read the assertion exists to serve.
        Assert.Single(security.IdentifiersAsAt(SecurityIdentifierKind.VendorSymbol, new DateOnly(2023, 1, 1)));
        Assert.Empty(security.IdentifiersAsAt(SecurityIdentifierKind.VendorSymbol, new DateOnly(2026, 1, 1)));
    }

    /// <summary>J. The database refuses a second security claiming the same vendor key.</summary>
    /// <remarks>
    /// The writer already checks, and this is the second mechanism: a uniqueness rule enforced only
    /// in application code is enforced only until the next caller.
    /// </remarks>
    [SkippableFact]
    public async Task Two_securities_cannot_claim_the_same_vendor_symbol()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await SeedAsync(context, SecurityId.New(), "AE.US");

            var company = await context.Companies.FirstAsync();
            var rival = Security.Create(SecurityId.New(), company.Id, Now);

            rival.AssertIdentifier(SecurityIdentifier.Assert(
                SecurityIdentifierKind.VendorSymbol,
                "AE.US",
                new DateOnly(2021, 9, 1),
                null,
                SourceId.Create("eodhd")));

            context.Securities.Add(rival);

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    /// <summary>A stored identifier assertion cannot be rewritten or removed.</summary>
    [SkippableFact]
    public async Task A_stored_identifier_assertion_is_append_only()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, "AE.US");
            }
        }

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        var security = await editor.Securities.Include(s => s.Identifiers).SingleAsync();

        editor.Entry(security.Identifiers[0]).Property(i => i.Value).CurrentValue = "REWRITTEN";

        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            var thrown = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => editor.SaveChangesAsync());

            Assert.Contains("append-only", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The whole stage end to end: the sealed declaration on disk populates the test database.
    /// </summary>
    /// <remarks>
    /// The counts are asserted as relationships rather than as literals wherever the relationship
    /// is what matters - one identifier per created security, no ticker assertions, nothing created
    /// for a refused class - so that re-sealing the declaration with more evidence changes the
    /// totals without silently changing what the run is allowed to do.
    /// </remarks>
    [SkippableFact]
    public async Task The_sealed_declaration_populates_and_a_second_run_changes_nothing()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        Assert.Equal("security-identity-sample400-2021-2026-v2", declaration.DeclarationId);
        Assert.Equal(400, declaration.Members.Count);

        var first = await PopulateAsync(declaration);

        Assert.Equal(400, first.MembersRead);
        Assert.Equal(0, first.TickerIdentifiersCreated);
        Assert.Equal(first.SecuritiesCreated, first.VendorSymbolIdentifiersCreated);
        Assert.Equal(0, first.AlreadyPresent);

        await using (var reader = _fixture.CreateContext(new ScopedWriteAuthorization()))
        {
            Assert.Equal(first.SecuritiesCreated, await reader.Securities.CountAsync());
            Assert.Equal(first.SecuritiesCreated, await reader.Companies.CountAsync());

            // Materialise the owners, then their identifiers. Projecting an owned collection on its
            // own leaves EF with owned rows and no owner to attach them to, which it refuses.
            var identifiers = (await reader.Securities
                    .Include(s => s.Identifiers)
                    .AsNoTracking()
                    .ToListAsync())
                .SelectMany(s => s.Identifiers)
                .ToList();

            Assert.Equal(first.SecuritiesCreated, identifiers.Count);
            Assert.All(identifiers, i => Assert.Equal(SecurityIdentifierKind.VendorSymbol, i.Kind));
            // The active declaration sources every vendor assertion to the registered eodhd-eod feed.
            // "eodhd" is the vendor label and names no registered source - the F1a correction.
            Assert.All(identifiers, i => Assert.Equal("eodhd-eod", i.SourceId.Value));
            Assert.DoesNotContain(identifiers, i => i.Kind == SecurityIdentifierKind.Ticker);
        }

        // I. The same evidence, run again, writes nothing further.
        var second = await PopulateAsync(declaration);

        Assert.Equal(0, second.SecuritiesCreated);
        Assert.Equal(first.SecuritiesCreated, second.AlreadyPresent);
        Assert.Equal(first.Refused, second.Refused);

        await using var after = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(first.SecuritiesCreated, await after.Securities.CountAsync());
        Assert.Equal(first.SecuritiesCreated, await after.Companies.CountAsync());
    }

    /// <summary>L. The reader refuses a declaration whose content no longer matches its seal.</summary>
    [SkippableFact]
    public void A_tampered_declaration_is_refused_rather_than_read()
    {
        var json = ReadDeclaration();

        // One accepted symbol changed, and nothing else. This is the smallest edit that would
        // silently redirect a security to a different instrument.
        var tampered = json.Replace(
            "\"Value\": \"AE\"",
            "\"Value\": \"AEX\"",
            StringComparison.Ordinal);

        Assert.NotEqual(json, tampered);

        var thrown = Assert.ThrowsAny<Exception>(() => SecurityIdentityDeclaration.Parse(tampered));

        Assert.Contains("digest", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // And the untouched file still reads, so the refusal is about the edit and not the check.
        Assert.Equal(400, SecurityIdentityDeclaration.Parse(json).Members.Count);
    }

    /// <summary>L. The census names the declaration and digest that produced it.</summary>
    /// <remarks>
    /// The digest changed at F1b because the active declaration changed, not because anything was
    /// recomputed: <c>RelativePath</c> now points at the F1a superseding file, whose seal covers the
    /// identifier source and interval as well. The predecessor's digest is unchanged and is pinned
    /// by <c>SecurityIdentityDeclarationSchemaTests</c>.
    /// </remarks>
    [SkippableFact]
    public void The_declaration_carries_the_digest_the_verification_artifact_recorded()
    {
        var declaration = SecurityIdentityDeclaration.Parse(ReadDeclaration());

        Assert.Equal(
            "86c610e2de640f8f759f3cc5cb78489027c9f5605ee404b0ed18e9a84eb9fd28",
            declaration.IdentityDigest);

        Assert.Equal(
            "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962",
            declaration.EvidenceBaseFingerprint);
    }

    private async Task<SecurityPopulationResult> PopulateAsync(ISecurityIdentityDeclaration declaration)
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

    private static async Task SeedAsync(AppDbContext context, SecurityId securityId, string symbol)
    {
        var company = Company.Create(CompanyId.New(), "Example Issuer", Now);

        context.Companies.Add(company);

        var security = Security.Create(securityId, company.Id, Now);

        security.AssertIdentifier(SecurityIdentifier.Assert(
            SecurityIdentifierKind.VendorSymbol,
            symbol,
            new DateOnly(2021, 9, 1),
            new DateOnly(2025, 2, 4),
            SourceId.Create("eodhd")));

        context.Securities.Add(security);

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The tracked declaration, read from the repository rather than from a fixture copy.
    /// </summary>
    /// <remarks>
    /// Reading the real file is the point: a copy would drift, and the thing being verified is that
    /// the sealed evidence this repository carries is the evidence the writer consumes.
    /// </remarks>
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
            $"No repository root containing {SecurityIdentityDeclaration.RelativePath} was found " +
            $"above {AppContext.BaseDirectory}.");

        return File.ReadAllText(Path.Combine(directory!.FullName, SecurityIdentityDeclaration.RelativePath));
    }
}
