namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// The reported line items these calculators know how to read.
/// </summary>
/// <remarks>
/// <para>
/// These are <see cref="Observations.Observation.Attribute"/> values, not a new identifier type.
/// A financial statement is a set of named line items, and Phase 2 already records exactly that -
/// introducing a parallel name for the same thing would guarantee the two eventually disagreed
/// about whether <c>revenue</c> and <c>revenue</c> were the same figure.
/// </para>
/// <para>
/// The naming follows the convention the EDGAR normaliser established - lower-case, dotted family,
/// hyphenated words - so a figure produced by ingestion and a figure looked for by a calculator are
/// spelled identically without anyone having to translate between them.
/// </para>
/// <para>
/// Three of these are <em>computed</em> rather than reported. They are named here because a derived
/// figure feeding another calculation - free cash flow into its own margin - has to be addressable
/// by the same mechanism as a reported one.
/// </para>
/// </remarks>
public static class FinancialFigures
{
    public const string Prefix = "financials.";

    // ---- income statement ----------------------------------------------------------------
    public const string Revenue = "financials.revenue";
    public const string GrossProfit = "financials.gross-profit";
    public const string OperatingIncome = "financials.operating-income";
    public const string NetIncome = "financials.net-income";
    public const string DepreciationAndAmortisation = "financials.depreciation-and-amortisation";

    // ---- cash flow -----------------------------------------------------------------------
    public const string OperatingCashFlow = "financials.operating-cash-flow";
    public const string CapitalExpenditure = "financials.capital-expenditure";

    // ---- balance sheet -------------------------------------------------------------------
    public const string CashAndEquivalents = "financials.cash-and-equivalents";
    public const string TotalDebt = "financials.total-debt";
    public const string CurrentAssets = "financials.current-assets";
    public const string CurrentLiabilities = "financials.current-liabilities";
    public const string Inventory = "financials.inventory";
    public const string TotalAssets = "financials.total-assets";
    public const string TotalEquity = "financials.total-equity";

    // ---- share counts --------------------------------------------------------------------
    public const string DilutedShares = "financials.diluted-shares";

    // ---- market ----------------------------------------------------------------------------
    // Not statement line items, but evidenced figures about the same subject, carried in the same
    // bag so a valuation ratio can put a price beside an earnings figure without a second mechanism.
    public const string MarketPrefix = "market.";
    public const string MarketCapitalisation = "market.market-capitalisation";
    public const string EnterpriseValue = "market.enterprise-value";

    // ---- computed, and addressable so they can feed further calculations -------------------
    public const string FreeCashFlow = "financials.free-cash-flow";
    public const string QuickAssets = "financials.quick-assets";
    public const string Ebitda = "financials.ebitda";
    public const string NetDebt = "financials.net-debt";
}
