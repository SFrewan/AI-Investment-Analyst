using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Sources;

/// <summary>A registered source as returned across the application boundary.</summary>
/// <remarks>
/// <para>
/// A separate shape from the <c>DataSource</c> aggregate, for the same reason
/// <see cref="Companies.CompanyDto"/> is: serialising the aggregate would expose its internals and
/// make a domain refactor a breaking API change.
/// </para>
/// <para>
/// <strong>Licensing is reported as the four permissions, not as prose.</strong> Whether a source
/// may be stored and processed is the thing an operator needs to see before activating it, and
/// burying it in a notes field would put a compliance decision behind a reading-comprehension
/// exercise.
/// </para>
/// </remarks>
public sealed record SourceDto(
    string Id,
    string Name,
    string Type,
    string Authority,
    string Region,
    IReadOnlyList<string> Categories,
    string Cadence,
    bool IsActive,
    SourceLicensingDto Licensing,
    string VerificationPolicy,
    string ReliabilityGrade,
    DateTime RegisteredAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>What a source's terms permit.</summary>
/// <param name="AllowsStorage">Whether its content may be kept.</param>
/// <param name="AllowsAutomatedProcessing">Whether it may be read by machine.</param>
/// <param name="AllowsRedistribution">Whether its content may be passed on.</param>
/// <param name="RequiresAttribution">Whether use must credit the source.</param>
/// <param name="RetentionLimitDays">
/// How long its content may be kept, or null for no licensed limit. Null is a stated fact about
/// the licence, not a missing value.
/// </param>
/// <param name="Notes">Anything the terms say that the flags above do not capture.</param>
public sealed record SourceLicensingDto(
    bool AllowsStorage,
    bool AllowsAutomatedProcessing,
    bool AllowsRedistribution,
    bool RequiresAttribution,
    int? RetentionLimitDays,
    string? Notes);

/// <summary>Maps registry aggregates to their wire shape.</summary>
public static class SourceMapper
{
    public static SourceDto ToDto(DataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SourceDto(
            source.Id.Value,
            source.Name,
            source.Type.ToString(),
            source.Authority.ToString(),
            source.Region.Code,
            source.Categories.Select(c => c.ToString()).ToList(),
            source.Cadence.ToString(),
            source.IsActive,
            ToDto(source.Licensing),
            source.Verification.ToString(),
            source.Reliability.ToString(),
            source.RegisteredAtUtc,
            source.UpdatedAtUtc);
    }

    public static SourceLicensingDto ToDto(LicensingTerms licensing)
    {
        ArgumentNullException.ThrowIfNull(licensing);

        return new SourceLicensingDto(
            licensing.StorageAllowed,
            licensing.AutomatedProcessingAllowed,
            licensing.RedistributionAllowed,
            licensing.AttributionRequired,

            // Days rather than a TimeSpan: retention limits are stated in licences as periods of
            // days or years, and a serialised TimeSpan is a shape nobody reads correctly.
            licensing.Retention.IsBounded ? (int)licensing.Retention.MaximumAge!.Value.TotalDays : null,
            licensing.Notes);
    }
}
