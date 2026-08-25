namespace AI.Investment.Domain.Sources;

/// <summary>
/// What kind of organisation or system a source is.
/// </summary>
/// <remarks>
/// Distinct from <see cref="SourceAuthority"/>, which is about weight. A news organisation and a
/// research provider are both <see cref="SourceAuthority.Secondary"/> but behave differently -
/// different licensing, different cadence, different failure modes - and later phases route on
/// that difference.
/// <para>
/// Deliberately spans well beyond equities. The platform's first domain is U.S. public equities;
/// its architecture is a global intelligence plane, and a source taxonomy that only admits
/// financial venues would have to be replaced the first time a supplier catalogue is added.
/// </para>
/// </remarks>
public enum SourceType
{
    Unknown = 0,

    /// <summary>A securities or markets regulator - filings, enforcement, registrations.</summary>
    RegulatoryAuthority = 1,

    /// <summary>A government department or statistical agency.</summary>
    GovernmentAgency = 2,

    /// <summary>A central bank or monetary authority.</summary>
    CentralBank = 3,

    /// <summary>A recognised securities or commodities exchange.</summary>
    Exchange = 4,

    /// <summary>A company publishing about itself - investor relations, press releases.</summary>
    CompanyDisclosure = 5,

    /// <summary>An established news organisation.</summary>
    NewsOrganisation = 6,

    /// <summary>A research or ratings provider.</summary>
    ResearchProvider = 7,

    /// <summary>A commercial vendor redistributing data it did not originate.</summary>
    DataVendor = 8,

    /// <summary>A marketplace or commerce platform - future opportunity domains.</summary>
    Marketplace = 9,

    /// <summary>A supplier, distributor or logistics operator - future opportunity domains.</summary>
    SupplyChainOperator = 10,

    /// <summary>An industry body or standards organisation.</summary>
    IndustryBody = 11,

    /// <summary>Aggregated or community-contributed content. Never primary.</summary>
    CommunityOrAggregator = 12,

    /// <summary>
    /// The platform itself - a calculation, an analysis agent, an ingestion service.
    /// </summary>
    /// <remarks>
    /// Values the system produces are given an origin like any other, so that a calculated ratio
    /// or an AI interpretation can be traced back to the component and version that made it. A
    /// derived value whose producer cannot be identified is the kind of value that becomes
    /// impossible to explain six months later, when the question is why the system believed
    /// something.
    /// <para>
    /// Registering internal producers keeps one rule instead of two: every claim names a
    /// registered source, with no carve-out for the platform's own output.
    /// </para>
    /// </remarks>
    InternalDerivation = 13,
}
