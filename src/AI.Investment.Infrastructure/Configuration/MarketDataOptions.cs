using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// Configuration for the operator-supplied price-history connector.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no market-data vendor in this repository, and there is not going to be one by
/// accident.</strong> Every usable source of daily closes is licensed, and choosing one is a
/// commercial decision with terms attached - which is exactly the kind of decision the platform
/// refuses to make on its own. What it can do is read a price history the operator already holds a
/// licence for, from a directory the operator names, through the same connector contract every
/// other source uses. Connecting a vendor API later is one more <c>IDataProvider</c> and a
/// normaliser for that vendor's wire format; nothing above the connector changes.
/// </para>
/// <para>
/// <see cref="Enabled"/> defaults to <c>false</c> and <see cref="HistoryDirectory"/> has no
/// default, so an installation that has configured nothing gets no connector rather than a
/// connector pointed somewhere arbitrary. The ingestion gateway then refuses runs for that source
/// and records the refusal, which is visible and explained.
/// </para>
/// </remarks>
public sealed class MarketDataOptions : IValidatableObject
{
    public const string SectionName = "Providers:PriceHistory";

    /// <summary>Whether the connector is registered at all. False unless deliberately enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The directory holding one price-history file per instrument, named
    /// <c>{identifier}.csv</c>.
    /// </summary>
    /// <remarks>
    /// A directory rather than a connection string because the licence for a price series usually
    /// permits an export and nothing more. The connector reads; it never writes here, and it never
    /// reaches outside this directory - the identifier is validated as a bare instrument symbol
    /// before it becomes part of a path.
    /// </remarks>
    [MaxLength(400)]
    public string HistoryDirectory { get; init; } = string.Empty;

    /// <summary>
    /// The licensing note recorded against the source in the registry.
    /// </summary>
    /// <remarks>
    /// Required when enabled, and deliberately so. The platform cannot know what the operator's
    /// vendor permits, and a registry entry that guessed would be a licensing claim nobody made.
    /// Whoever switches this on states the terms.
    /// </remarks>
    [MaxLength(2000)]
    public string LicensingNotes { get; init; } = string.Empty;

    /// <summary>Whether the operator's licence permits redistributing the series.</summary>
    /// <remarks>Defaults to the restrictive answer, like every other licensing default.</remarks>
    public bool RedistributionAllowed { get; init; }

    /// <summary>
    /// How long the licence permits the raw payloads to be retained, in days. Null for unlimited.
    /// </summary>
    public int? RetentionDays { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(HistoryDirectory))
        {
            yield return new ValidationResult(
                "A history directory is required when the price-history connector is enabled. " +
                "Without one there is nothing for it to read, and defaulting to a path would make " +
                "the connector's source of truth a guess.",
                [nameof(HistoryDirectory)]);
        }

        if (string.IsNullOrWhiteSpace(LicensingNotes))
        {
            yield return new ValidationResult(
                "Licensing notes are required when the price-history connector is enabled. The " +
                "platform cannot know what the operator's vendor permits, and a registry entry that " +
                "guessed would record a licensing claim nobody made.",
                [nameof(LicensingNotes)]);
        }

        if (RetentionDays is { } days && days < 1)
        {
            yield return new ValidationResult(
                "A retention limit must be at least one day. Use null for a licence that imposes none.",
                [nameof(RetentionDays)]);
        }
    }
}
