using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// The registry entry for EODHD.
/// </summary>
/// <remarks>
/// <para>
/// A definition, not a registration: it returns an <strong>inactive</strong> source, and activation
/// stays a deliberate act through the Action/Policy seam.
/// </para>
/// <para>
/// <strong>The licensing terms come from configuration, for the same reason the operator's own
/// price history's do.</strong> EDGAR's terms are published and a government record is a government
/// record, so that definition can state them. EODHD's depend on which subscription this
/// installation bought, which this code has never seen. Whoever enables the connector states the
/// terms, and the options refuse to validate without them.
/// </para>
/// <para>
/// <see cref="SourceAuthority.Secondary"/> and <see cref="VerificationPolicy.RequiresCorroboration"/>
/// because that is what a vendor feed is: an accurate transcription of somebody else's closing
/// print, one step removed from the venue that produced it. Recording it as primary would let a
/// transcription error outrank a correction from the exchange itself.
/// </para>
/// </remarks>
public sealed class EodhdSource : ISourceDefinition
{
    private readonly EodhdOptions _options;

    public EodhdSource(IOptions<EodhdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    /// <inheritdoc />
    public SourceId SourceId => EodhdProvider.Id;

    /// <summary>Builds the registry entry, inactive.</summary>
    public DataSource Definition(DateTime nowUtc) =>
        DataSource.Register(
            EodhdProvider.Id,
            "EODHD end-of-day prices",
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
            "Daily open, high, low, close and volume from EODHD, fetched over HTTPS. The rows " +
            "carry a trading date and no times; the session close and publication delay recorded " +
            "against each observation are this installation's stated facts about the exchange.");

    /// <summary>
    /// What the registry records when the connector is defined but no terms were stated.
    /// </summary>
    /// <remarks>
    /// Unreachable through the registration path, because the options refuse to validate without
    /// notes when the connector is enabled. Present so that a definition built in a test or a tool
    /// says "nobody stated the terms" rather than asserting permissions nobody granted.
    /// </remarks>
    public const string UnstatedTerms =
        "No licensing terms were stated for this EODHD subscription. Treat storage and processing " +
        "as permitted only to the extent this installation's own agreement permits, and " +
        "redistribution as forbidden until somebody says otherwise in writing.";
}
