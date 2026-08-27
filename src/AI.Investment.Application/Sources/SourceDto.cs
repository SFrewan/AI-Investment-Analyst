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
/// <param name="Cadence">
/// The cadence <em>kind</em> - a stable name such as <c>Daily</c> - never the value object's
/// <c>ToString()</c>, which is a human sentence and would put unstable prose on the wire.
/// </param>
/// <param name="ExpectedIntervalSeconds">
/// How often the source is expected to publish, or null when it cannot be late (event-driven and
/// on-demand sources). Null is a stated fact, not a missing value.
/// </param>
/// <param name="VerificationPolicy">
/// A stable name for the policy - <c>Authoritative</c>, <c>RequiresCorroboration</c>,
/// <c>Cautious</c>, or <c>Custom</c> for one built with <c>VerificationPolicy.Create</c>.
/// </param>
/// <param name="CanConfirmAlone">Whether this source alone can produce confirmed information.</param>
/// <param name="RequiredIndependentSources">How many sources must agree when it cannot.</param>
public sealed record SourceDto(
    string Id,
    string Name,
    string Type,
    string Authority,
    string Region,
    IReadOnlyList<string> Categories,
    string Cadence,
    int? ExpectedIntervalSeconds,
    bool IsActive,
    SourceLicensingDto Licensing,
    string VerificationPolicy,
    bool CanConfirmAlone,
    int RequiredIndependentSources,
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

            // The kind, not the value object's ToString(). `UpdateCadence.ToString()` renders
            // "Daily (~1.00:00:00)" - a sentence for a human reading a log, and unstable prose for
            // anything reading this DTO.
            source.Cadence.Kind.ToString(),
            source.Cadence.ExpectedInterval is { } interval ? (int)interval.TotalSeconds : null,
            source.IsActive,
            ToDto(source.Licensing),
            NameOf(source.Verification),
            source.Verification.CanConfirmAlone,
            source.Verification.RequiredIndependentSources,
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

    /// <summary>A stable name for a verification policy.</summary>
    /// <remarks>
    /// <para>
    /// <c>VerificationPolicy.ToString()</c> renders "confirms alone" or "requires N independent
    /// sources" - a sentence written for a human reading a log. Putting it on the wire was a
    /// defect: the wording can be changed without anyone realising a client depended on it, and
    /// two genuinely different policies can read almost identically.
    /// </para>
    /// <para>
    /// The name alone is not enough either, because <c>VerificationPolicy.Create</c> admits
    /// policies that are none of the three well-known ones. So the DTO carries the name <em>and</em>
    /// the two facts the policy actually consists of - the same choice the persistence layer
    /// already made, where verification is an owned type with a column per component, and the same
    /// choice <see cref="SourceLicensingDto"/> makes for permissions.
    /// </para>
    /// </remarks>
    private static string NameOf(VerificationPolicy policy)
    {
        if (policy == VerificationPolicy.Authoritative)
        {
            return nameof(VerificationPolicy.Authoritative);
        }

        if (policy == VerificationPolicy.RequiresCorroboration)
        {
            return nameof(VerificationPolicy.RequiresCorroboration);
        }

        if (policy == VerificationPolicy.Cautious)
        {
            return nameof(VerificationPolicy.Cautious);
        }

        // Built by Create with values matching none of the three. Named honestly rather than
        // forced into the nearest one: CanConfirmAlone and RequiredIndependentSources carry the
        // actual policy, and a caller that cares can read them.
        return "Custom";
    }
}
