using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Securities;

/// <summary>
/// The D1 reference model against a real database: schema, round-trip, and append-only.
/// </summary>
/// <remarks>
/// <para>
/// The domain tests prove the rules hold in memory. These prove the other half - that the schema
/// the migration produced can actually hold them, and that the write guard refuses to rewrite a
/// listing history even when a caller asks it to. Two mechanisms, because one can be forgotten at a
/// call site.
/// </para>
/// <para>
/// Nothing here is backfilled and no existing table is read or written. The rows are created by
/// these tests and truncated between them.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SecurityMasterPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId Source = SourceId.Create("sec-edgar");
    private static readonly VenueId Nasdaq = VenueId.Create("XNAS");

    private readonly PostgresFixture _fixture;

    public SecurityMasterPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// M. The migration produced the four tables the model needs, with the pairing key.
    /// </summary>
    [SkippableFact]
    public async Task The_reference_model_tables_exist_with_the_expected_shape()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        // Reaching each set proves the table is there and the mapping resolves against it.
        Assert.Equal(0, await context.Securities.CountAsync());
        Assert.Equal(0, await context.Venues.CountAsync());
        Assert.Equal(0, await context.Listings.CountAsync());
        Assert.Equal(0, await context.ListingEvents.CountAsync());

        var listing = context.Model.FindEntityType(typeof(Listing))!;
        var key = listing.FindPrimaryKey()!;

        Assert.Equal(
            ["security_id", "venue_id"],
            key.Properties.Select(p => p.GetColumnName()).OrderBy(n => n, StringComparer.Ordinal));

        // The absence that matters: no status column anywhere on the listing itself.
        Assert.DoesNotContain(
            listing.GetProperties().Select(p => p.GetColumnName()),
            n => n.Contains("status", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// L. A security, a venue, a listing and its events survive a round-trip intact.
    /// </summary>
    [SkippableFact]
    public async Task The_reference_model_round_trips_through_the_database()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();
        var hash = ContentHash.Compute("form-25"u8);

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, hash);
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var listing = await reader.Listings
            .Include(l => l.Events)
            .SingleAsync(l => l.SecurityId == securityId);

        Assert.Equal(Nasdaq, listing.VenueId);
        Assert.Equal(2, listing.Events.Count);

        var removal = listing.Events.Single(e => e.Status == ListingStatus.Removed);

        Assert.Equal(new DateOnly(2023, 6, 1), removal.EffectiveDate);
        Assert.Equal(hash, removal.EvidenceContentHash);
        Assert.Equal(Source, removal.SourceId);
        Assert.Equal("form-25", removal.ReasonCode);

        var security = await reader.Securities.SingleAsync(s => s.Id == securityId);
        Assert.NotEqual(default, security.CompanyId);

        var venue = await reader.Venues.SingleAsync();
        Assert.Equal("US", venue.CountryCode);
        Assert.Null(venue.ValidTo);
    }

    /// <summary>
    /// I. The point-in-time read, proven against rows rather than against a list in memory.
    /// </summary>
    [SkippableFact]
    public async Task A_status_is_reconstructed_from_stored_events_without_seeing_the_future()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, evidence: null);
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var listing = await reader.Listings
            .Include(l => l.Events)
            .SingleAsync(l => l.SecurityId == securityId);

        var asOf = new DateOnly(2024, 1, 1);

        // Standing before the removal was published, the removal is invisible.
        Assert.Equal(
            ListingStatus.Listed,
            listing.StatusAsAt(asOf, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        // Standing after it, it is not.
        Assert.Equal(ListingStatus.Removed, listing.StatusAsAt(asOf, Now));

        // And before anything was said at all, the answer is Unknown - not Listed.
        Assert.Equal(ListingStatus.Unknown, listing.StatusAsAt(new DateOnly(2020, 1, 1), Now));
    }

    /// <summary>
    /// N. The database refuses to rewrite a listing history, authorised or not.
    /// </summary>
    [SkippableFact]
    public async Task A_stored_listing_event_cannot_be_modified()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, evidence: null);
            }
        }

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        var stored = await editor.ListingEvents.FirstAsync(e => e.SecurityId == securityId);

        editor.Entry(stored).Property(e => e.ReasonCode).CurrentValue = "rewritten";

        // Inside an authorisation window, which is the case that matters: permission to have an
        // effect has never been permission to edit the account of it.
        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            var thrown = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => editor.SaveChangesAsync());

            Assert.Contains("append-only", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// N, the other half. Deletion is refused for the same reason.
    /// </summary>
    [SkippableFact]
    public async Task A_stored_listing_event_cannot_be_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();
        var securityId = SecurityId.New();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                await SeedAsync(context, securityId, evidence: null);
            }
        }

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        editor.ListingEvents.Remove(await editor.ListingEvents.FirstAsync(e => e.SecurityId == securityId));

        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await Assert.ThrowsAsync<UnauthorizedWriteException>(() => editor.SaveChangesAsync());
        }
    }

    /// <summary>
    /// Reference data is a domain write: it still needs an authorisation window to be created.
    /// </summary>
    /// <remarks>
    /// The distinction from the seam's own bookkeeping, asserted so that adding listing events to
    /// the append-only set cannot quietly have exempted them from the guard as well.
    /// </remarks>
    [SkippableFact]
    public async Task Recording_reference_data_without_an_authorisation_window_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        context.Venues.Add(Venue.Create(Nasdaq, "Nasdaq", new DateOnly(2000, 1, 1), Now, "US"));

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    private static async Task SeedAsync(AppDbContext context, SecurityId securityId, ContentHash? evidence)
    {
        var company = Company.Create(
            CompanyId.New(), "Example Issuer", Now);

        context.Companies.Add(company);
        context.Venues.Add(Venue.Create(Nasdaq, "Nasdaq", new DateOnly(2000, 1, 1), Now, "US"));
        context.Securities.Add(Security.Create(securityId, company.Id, Now));

        var listing = Listing.Open(securityId, Nasdaq, Now);

        listing.Append(ListingEvent.Record(
            Guid.NewGuid(),
            securityId,
            Nasdaq,
            ListingStatus.Listed,
            new DateOnly(2021, 1, 4),
            "admitted",
            Source,
            new DateTime(2021, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2021, 1, 4, 0, 0, 0, DateTimeKind.Utc)));

        listing.Append(ListingEvent.Record(
            Guid.NewGuid(),
            securityId,
            Nasdaq,
            ListingStatus.Removed,
            new DateOnly(2023, 6, 1),
            "form-25",
            Source,
            new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            evidence));

        context.Listings.Add(listing);

        await context.SaveChangesAsync();
    }
}
