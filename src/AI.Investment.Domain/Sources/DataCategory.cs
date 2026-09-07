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

    /// <summary>
    /// A cross-section of many filers at one period, rather than one filer's history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other category above answers a question about <em>a</em> subject: this company's
    /// filings, that instrument's prices. This one answers a question about a <em>period</em> -
    /// which companies reported a figure for it, and what they reported. EDGAR's XBRL frames serve
    /// exactly that, and nothing else in the taxonomy could carry it: a request whose subject is a
    /// quarter is not a request about a company, and filing it under
    /// <see cref="FinancialStatements"/> would let a market-wide document be read as one firm's
    /// accounts.
    /// </para>
    /// <para>
    /// It exists because an unbiased universe cannot be built from per-company endpoints. Asking
    /// each company in a list whether it was reporting can only ever describe the list, and a list
    /// of companies that exist today is a list of survivors. Asking a period who reported it
    /// includes the ones that later died, which is the whole point.
    /// </para>
    /// </remarks>
    MarketWideDisclosure = 18,

    // --- Future opportunity domains. Declared so the taxonomy is stable, NOT implemented.
    ProductCatalogue = 13,
    MarketplaceListings = 14,
    SupplierPricing = 15,
    ShippingAndLogistics = 16,
    CompetitorIntelligence = 17,
}
