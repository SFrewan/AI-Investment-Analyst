using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// The registry entry for EODHD's corporate actions.
/// </summary>
/// <remarks>
/// <para>
/// A definition, not a registration: it returns an <strong>inactive</strong> source, and activation
/// stays a deliberate act through the Action/Policy seam. Registering the connector grants nothing.
/// </para>
/// <para>
/// <strong><see cref="UpdateCadence.EventDriven"/>, and that is the difference from the price
/// entry.</strong> A daily price series that stops is broken, which is why that source is Daily and
/// why a gap in it is a fault worth raising. Splits are not like that: an instrument can go ten
/// years without one, and silence is the ordinary answer. Recording this feed as daily would have
/// the freshness monitor reporting a perfectly healthy source as stale every day but a handful,
/// and a monitor that cries wolf is a monitor nobody reads.
/// </para>
/// <para>
/// The licensing terms are the price source's, because it is the same subscription and the same
/// agreement. They are read from the same options for that reason rather than restated.
/// </para>
/// </remarks>
public sealed class EodhdSplitsSource : ISourceDefinition
{
    private readonly EodhdOptions _options;

    public EodhdSplitsSource(IOptions<EodhdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    /// <inheritdoc />
    public SourceId SourceId => EodhdSplitsProvider.Id;

    /// <summary>Builds the registry entry, inactive.</summary>
    public DataSource Definition(DateTime nowUtc) =>
        DataSource.Register(
            EodhdSplitsProvider.Id,
            "EODHD share splits",
            SourceType.DataVendor,
            SourceAuthority.Secondary,
            Region.Global,
            [DataCategory.CorporateActions],
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: _options.RedistributionAllowed,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: string.IsNullOrWhiteSpace(_options.LicensingNotes)
                    ? EodhdSource.UnstatedTerms
                    : _options.LicensingNotes,
                retention: _options.RetentionDays is { } days
                    ? RetentionLimit.OfDays(days)
                    : RetentionLimit.Unlimited),
            VerificationPolicy.RequiresCorroboration,
            nowUtc,
            "Share splits from EODHD, fetched over HTTPS. Each row carries an effective date and a " +
            "ratio expressed as new shares over old. The platform stores the raw close rather than " +
            "the vendor's adjusted one, so these are what make a series spanning a split readable " +
            "at all - and a series carrying a step no split here explains is refused rather than " +
            "screened.");
}
