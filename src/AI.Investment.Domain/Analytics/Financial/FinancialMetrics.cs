namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// The financial measures this build can compute.
/// </summary>
/// <remarks>
/// Gathered in one place so the catalogue of what the platform measures can be read at a glance,
/// and so a stored result's identifier has exactly one definition in the codebase.
/// </remarks>
public static class FinancialMetrics
{
    public static MetricId RevenueGrowth { get; } = MetricId.Create("financial.revenue-growth");

    public static MetricId EarningsGrowth { get; } = MetricId.Create("financial.earnings-growth");

    public static MetricId GrossMargin { get; } = MetricId.Create("financial.gross-margin");

    public static MetricId OperatingMargin { get; } = MetricId.Create("financial.operating-margin");

    public static MetricId NetMargin { get; } = MetricId.Create("financial.net-margin");

    public static MetricId Ebitda { get; } = MetricId.Create("financial.ebitda");

    public static MetricId FreeCashFlow { get; } = MetricId.Create("financial.free-cash-flow");

    public static MetricId FreeCashFlowMargin { get; } = MetricId.Create("financial.free-cash-flow-margin");

    public static MetricId NetDebt { get; } = MetricId.Create("financial.net-debt");

    public static MetricId DebtToEquity { get; } = MetricId.Create("financial.debt-to-equity");

    public static MetricId CurrentRatio { get; } = MetricId.Create("financial.current-ratio");

    public static MetricId QuickAssets { get; } = MetricId.Create("financial.quick-assets");

    public static MetricId QuickRatio { get; } = MetricId.Create("financial.quick-ratio");

    public static MetricId ReturnOnEquity { get; } = MetricId.Create("financial.return-on-equity");

    public static MetricId ReturnOnAssets { get; } = MetricId.Create("financial.return-on-assets");

    public static MetricId EarningsPerShareDiluted { get; } = MetricId.Create("financial.eps-diluted");

    public static MetricId CashConversion { get; } = MetricId.Create("financial.cash-conversion");

    // ---- valuation: what the market is paying for what the filings report --------------------

    public static MetricId EnterpriseValue { get; } = MetricId.Create("financial.enterprise-value");

    public static MetricId PriceToEarnings { get; } = MetricId.Create("financial.price-to-earnings");

    public static MetricId PriceToBook { get; } = MetricId.Create("financial.price-to-book");

    public static MetricId PriceToSales { get; } = MetricId.Create("financial.price-to-sales");

    public static MetricId EnterpriseValueToEbitda { get; } = MetricId.Create("financial.ev-to-ebitda");
}
