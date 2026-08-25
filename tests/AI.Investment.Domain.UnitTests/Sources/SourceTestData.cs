using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.UnitTests.Sources;

/// <summary>
/// Builders for source fixtures. Named parameters at the call site keep each test's subject
/// visible; everything a given test does not care about takes a sane default here.
/// </summary>
internal static class SourceTestData
{
    public static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public static DataSource Register(
        string id = "test-source",
        SourceType type = SourceType.RegulatoryAuthority,
        SourceAuthority authority = SourceAuthority.Primary,
        Region? region = null,
        IEnumerable<DataCategory>? categories = null,
        UpdateCadence? cadence = null,
        LicensingTerms? licensing = null,
        VerificationPolicy? verification = null,
        DateTime? nowUtc = null) =>
        DataSource.Register(
            SourceId.Create(id),
            $"Source {id}",
            type,
            authority,
            region ?? Region.UnitedStates,
            categories ?? [DataCategory.RegulatoryFilings],
            cadence ?? UpdateCadence.EventDriven,
            licensing ?? LicensingTerms.OpenData(),
            verification ?? VerificationPolicy.Authoritative,
            nowUtc ?? Now);

    /// <summary>A registered source that has also been switched on.</summary>
    public static DataSource Active(
        string id = "test-source",
        SourceType type = SourceType.RegulatoryAuthority,
        SourceAuthority authority = SourceAuthority.Primary,
        Region? region = null,
        IEnumerable<DataCategory>? categories = null,
        LicensingTerms? licensing = null,
        VerificationPolicy? verification = null)
    {
        var source = Register(id, type, authority, region, categories, null, licensing, verification);
        source.Activate(Now);
        return source;
    }

    /// <summary>Storage permitted, automated processing not.</summary>
    public static LicensingTerms StorageOnly() =>
        LicensingTerms.Create(
            storageAllowed: true,
            redistributionAllowed: false,
            automatedProcessingAllowed: false,
            attributionRequired: true);

    /// <summary>Automated processing permitted, storage not.</summary>
    public static LicensingTerms ProcessingOnly() =>
        LicensingTerms.Create(
            storageAllowed: false,
            redistributionAllowed: false,
            automatedProcessingAllowed: true,
            attributionRequired: true);
}
