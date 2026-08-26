using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// The registry entry for SEC EDGAR.
/// </summary>
/// <remarks>
/// <para>
/// A definition, not a registration. This returns an <strong>inactive</strong> source, because
/// <see cref="DataSource"/> registers inactive by design and activation is a deliberate act
/// performed through the Action/Policy seam. A connector shipping in the box does not get to
/// switch itself on.
/// </para>
/// <para>
/// The licensing terms are recorded rather than assumed. EDGAR filings are U.S. government
/// records in the public domain, so storage, redistribution and automated processing are all
/// permitted - but the SEC's fair-access policy attaches an identification requirement, and that
/// is written into the notes so anyone reading the registry sees the obligation rather than only
/// the permission.
/// </para>
/// </remarks>
public sealed class SecEdgarSource : ISourceDefinition
{
    public const string LicensingNotes =
        "U.S. government records, public domain. Storage, redistribution and automated processing " +
        "are permitted. The SEC's fair-access policy requires every request to carry a User-Agent " +
        "identifying the application and a contact e-mail, and caps request rate; both are handled " +
        "by the connector and are conditions of use rather than optional.";

    /// <inheritdoc />
    public SourceId SourceId => SecEdgarProvider.Id;

    /// <summary>
    /// Builds the registry entry, inactive.
    /// </summary>
    public DataSource Definition(DateTime nowUtc) =>
        DataSource.Register(
            SecEdgarProvider.Id,
            "U.S. Securities and Exchange Commission - EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [
                DataCategory.RegulatoryFilings,
                DataCategory.CompanyProfile,
                DataCategory.FinancialStatements,
                DataCategory.EarningsDisclosure,
            ],

            // Filings arrive when companies file them. A daily cadence would report a source
            // behaving exactly as expected as overdue every weekend.
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: true,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: LicensingNotes,

                // Public-domain government records carry no retention obligation. Stated
                // explicitly rather than defaulted, so the registry records a fact about the
                // licence instead of the absence of a decision.
                retention: RetentionLimit.Unlimited),

            // The originating record. Nothing corroborates a filing better than the filing.
            VerificationPolicy.Authoritative,
            nowUtc,
            "The originating record for U.S. public company disclosure: registration statements, " +
            "periodic reports, ownership filings and the XBRL facts derived from them.");
}
