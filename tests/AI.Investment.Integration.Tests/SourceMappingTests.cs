using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests;

/// <summary>
/// The registry survives a round trip, including the parts nothing else would notice.
/// </summary>
/// <remarks>
/// <para>
/// Written after an EF model defect that no other kind of test could have caught:
/// <c>LicensingTerms.Retention</c> - the licensed retention cap the whole approved Option C model
/// depends on - was not mapped by any means. No <c>Property</c>, no <c>OwnsOne</c>, no
/// <c>Ignore</c>. The cap never reached the database, so a source licensed for 365 days reloaded
/// with no cap at all, and <c>RetentionPolicy</c> would have decided every payload could be kept
/// forever while the licence said otherwise.
/// </para>
/// <para>
/// Unit tests could not see it: they construct <c>LicensingTerms</c> in memory, where the property
/// is always present. Only a save-and-reload against a real provider proves a value is persisted.
/// That is what these do.
/// </para>
/// <para>
/// Like the write-guard tests, these skip when no database is reachable - and a skip here means
/// the mapping is <strong>unproven</strong>, not fine.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SourceMappingTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public SourceMappingTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Empties the shared test database before each test in this class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>vendor-capped</c>, <c>regulator-open</c> and <c>full-fidelity</c> each name the licence
    /// shape their test is about, and a source identifier is a primary key. A database that
    /// outlived the run therefore made every run after the first fail on <c>23505 duplicate key
    /// value violates unique constraint "PK_data_sources"</c> - a fact about leftover rows, not
    /// about the mapping these tests exist to prove. Emptying first removes the leftovers and
    /// keeps the names saying what they say.
    /// </para>
    /// </remarks>
    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static DataSource Source(string id, LicensingTerms licensing) =>
        DataSource.Register(
            SourceId.Create(id),
            $"Source {id}",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.CompanyProfile],
            UpdateCadence.Daily(),
            licensing,
            VerificationPolicy.Authoritative,
            Now);

    private async Task<DataSource?> RoundTripAsync(DataSource source)
    {
        var authorization = new ScopedWriteAuthorization();
        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.DataSources.Add(source);
            await context.SaveChangesAsync();
        }

        // A second context, so what comes back is read from the database rather than from the
        // first context's identity map. An identity-map hit would prove nothing at all.
        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await reader.DataSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == source.Id);
    }

    [SkippableFact]
    public async Task A_licensed_retention_cap_survives_a_round_trip()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var licensing = LicensingTerms.Create(
            storageAllowed: true,
            redistributionAllowed: false,
            automatedProcessingAllowed: true,
            attributionRequired: true,
            retention: RetentionLimit.OfDays(365));

        var reloaded = await RoundTripAsync(Source("vendor-capped", licensing));

        Assert.NotNull(reloaded);

        // The defect this test exists for: the cap came back absent, so retention would have
        // concluded the payload could be kept indefinitely.
        Assert.True(reloaded!.Licensing.Retention.IsBounded);
        Assert.Equal(TimeSpan.FromDays(365), reloaded.Licensing.Retention.MaximumAge);
    }

    [SkippableFact]
    public async Task An_unlimited_retention_licence_reloads_as_Unlimited_and_never_as_null()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var reloaded = await RoundTripAsync(Source("regulator-open", LicensingTerms.OpenData()));

        Assert.NotNull(reloaded);

        // Unlimited is a stated value, not an absence. Stored as a nullable column it would have
        // reloaded as a null RetentionLimit - a NullReferenceException inside the one rule that
        // destroys evidence - which is why the column is NOT NULL and carries a word.
        Assert.NotNull(reloaded!.Licensing.Retention);
        Assert.False(reloaded.Licensing.Retention.IsBounded);
        Assert.Null(reloaded.Licensing.Retention.MaximumAge);
    }

    /// <summary>
    /// The whole registry aggregate, not only the part that was broken.
    /// </summary>
    /// <remarks>
    /// Three owned types hang off <c>DataSource</c> - cadence, licensing and verification - and
    /// the defect above was in one of them. Asserting the other two here is cheap and means the
    /// next unmapped property has somewhere to be caught.
    /// </remarks>
    [SkippableFact]
    public async Task Cadence_verification_and_licensing_all_survive_a_round_trip()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var reloaded = await RoundTripAsync(Source("full-fidelity", LicensingTerms.OpenData()));

        Assert.NotNull(reloaded);

        Assert.Equal(CadenceKind.Daily, reloaded!.Cadence.Kind);
        Assert.Equal(TimeSpan.FromDays(1), reloaded.Cadence.ExpectedInterval);

        Assert.True(reloaded.Verification.CanConfirmAlone);
        Assert.Equal(1, reloaded.Verification.RequiredIndependentSources);

        Assert.True(reloaded.Licensing.StorageAllowed);
        Assert.True(reloaded.Licensing.AutomatedProcessingAllowed);

        Assert.Contains(DataCategory.CompanyProfile, reloaded.Categories);
        Assert.False(reloaded.IsActive);
    }
}
