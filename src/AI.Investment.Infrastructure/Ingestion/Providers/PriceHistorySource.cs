using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// The registry entry for the operator's price history.
/// </summary>
/// <remarks>
/// <para>
/// A definition, not a registration: it returns an <strong>inactive</strong> source, and activation
/// stays a deliberate act through the Action/Policy seam.
/// </para>
/// <para>
/// <strong>The licensing terms come from configuration, not from here.</strong> Every other source
/// definition in this repository states terms the platform can read for itself - EDGAR's are
/// published, and public-domain government records are public-domain government records. A price
/// series is licensed by whoever the operator bought it from, on terms this code has never seen,
/// and a registry entry that filled them in would be recording a claim nobody made. So the notes,
/// the redistribution permission and the retention limit are all supplied by the operator who
/// enabled the connector, and the connector refuses to be enabled without them.
/// </para>
/// <para>
/// <see cref="SourceAuthority.Secondary"/> and <see cref="VerificationPolicy.RequiresCorroboration"/>
/// because that is what a vendor export is: an accurate transcription of somebody else's closing
/// print, one step removed from the venue that produced it. Recording it as primary would let a
/// transcription error outrank a correction from the exchange itself.
/// </para>
/// </remarks>
public sealed class PriceHistorySource : ISourceDefinition
{
    private readonly MarketDataOptions _options;

    public PriceHistorySource(IOptions<MarketDataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    /// <inheritdoc />
    public SourceId SourceId => PriceHistoryFileProvider.Id;

    /// <summary>Builds the registry entry, inactive.</summary>
    public DataSource Definition(DateTime nowUtc) =>
        DataSource.Register(
            PriceHistoryFileProvider.Id,
            "Operator-supplied daily price history",
            SourceType.DataVendor,
            SourceAuthority.Secondary,
            Region.Global,
            [DataCategory.MarketPrices],

            // A daily series is expected daily, which is what makes a gap in it detectable. An
            // event-driven cadence would report a feed that stopped a fortnight ago as healthy.
            UpdateCadence.Daily(),
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: _options.RedistributionAllowed,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: string.IsNullOrWhiteSpace(_options.LicensingNotes)
                    ? UnstatedTerms
                    : _options.LicensingNotes,
                retention: _options.RetentionDays is { } days
                    ? RetentionLimit.OfDays(days)
                    : RetentionLimit.Unlimited),
            VerificationPolicy.RequiresCorroboration,
            nowUtc,
            "Daily closing prices exported from whichever market-data vendor this installation " +
            "licenses, read from a directory rather than fetched. The transport is local; the terms " +
            "are the operator's and are recorded above.");

    /// <summary>
    /// What the registry records when the connector is defined but no terms were stated.
    /// </summary>
    /// <remarks>
    /// Unreachable through the registration path, because the options refuse to validate without
    /// notes when the connector is enabled. Present so that a definition built in a test or a tool
    /// says "nobody stated the terms" rather than asserting permissions the operator never granted.
    /// </remarks>
    public const string UnstatedTerms =
        "No licensing terms were stated for this price history. Treat storage and processing as " +
        "permitted only to the extent the operator's own agreement with their vendor permits, and " +
        "redistribution as forbidden until somebody says otherwise in writing.";
}
