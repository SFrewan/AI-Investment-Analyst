namespace AI.Investment.Domain.Sources;

/// <summary>
/// A kind of information a source can supply.
/// </summary>
/// <remarks>
/// <para>
/// The extension point that keeps this a global data plane rather than a market-data module.
/// Adding commodities, shipping rates or supplier catalogues later means adding members here and
/// registering sources that carry them - not reworking the ingestion foundation.
/// </para>
/// <para>
/// Not a <c>[Flags]</c> enum. A source supplies a SET of categories, and that set is modelled as
/// a set. Flags run out at 32 members, and this list is expected to outgrow that.
/// </para>
/// </remarks>
public enum DataCategory
{
    Unknown = 0,

    // --- The first production domain: U.S. public equities -----------------------------
    MarketPrices = 1,
    CorporateActions = 2,
    CompanyProfile = 3,
    FinancialStatements = 4,
    RegulatoryFilings = 5,
    EarningsDisclosure = 6,
    OwnershipAndInsiders = 7,

    // --- Broader financial context ------------------------------------------------------
    News = 8,
    EconomicIndicators = 9,
    InterestRates = 10,
    ForeignExchange = 11,
    Commodities = 12,

    // --- Future opportunity domains. Declared so the taxonomy is stable, NOT implemented.
    ProductCatalogue = 13,
    MarketplaceListings = 14,
    SupplierPricing = 15,
    ShippingAndLogistics = 16,
    CompetitorIntelligence = 17,
}
