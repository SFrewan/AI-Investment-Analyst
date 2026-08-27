using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// The financial calculators this build ships, each bound to its formula and version.
/// </summary>
/// <remarks>
/// <para>
/// A catalogue rather than seventeen classes. Every measure here is one of three shapes - a
/// division, a signed sum, or a period-over-period change - so what actually distinguishes them is
/// which figures they read and what they are called, and that is exactly what this file states.
/// </para>
/// <para>
/// <strong>Three of these read a figure the platform computed rather than one a filer reported.</strong>
/// Free cash flow margin needs free cash flow; the quick ratio needs quick assets. The caller runs
/// the inner calculation, adds its result to the period with
/// <see cref="ReportedFigures.With"/>, and runs the outer one - so the derived figure carries its
/// own evidence chain rather than being recomputed silently inside another formula.
/// </para>
/// </remarks>
public static class FinancialCalculators
{
    /// <summary>The version every formula in this file is currently at.</summary>
    public static CalculationVersion Version1 { get; } = CalculationVersion.Create(1, 0);

    // ---- signed sums ------------------------------------------------------------------------

    public static SumMetricCalculator FreeCashFlow { get; } = new(
        FinancialMetrics.FreeCashFlow,
        Producer(FinancialMetrics.FreeCashFlow),
        Version1,
        UnitOfMeasure.Money,
        [
            SumTerm.Plus(FinancialFigures.OperatingCashFlow),
            SumTerm.Minus(FinancialFigures.CapitalExpenditure),
        ],
        $"{FinancialFigures.OperatingCashFlow} - {FinancialFigures.CapitalExpenditure}");

    public static SumMetricCalculator Ebitda { get; } = new(
        FinancialMetrics.Ebitda,
        Producer(FinancialMetrics.Ebitda),
        Version1,
        UnitOfMeasure.Money,
        [
            SumTerm.Plus(FinancialFigures.OperatingIncome),
            SumTerm.Plus(FinancialFigures.DepreciationAndAmortisation),
        ],
        $"{FinancialFigures.OperatingIncome} + {FinancialFigures.DepreciationAndAmortisation}");

    public static SumMetricCalculator NetDebt { get; } = new(
        FinancialMetrics.NetDebt,
        Producer(FinancialMetrics.NetDebt),
        Version1,
        UnitOfMeasure.Money,
        [
            SumTerm.Plus(FinancialFigures.TotalDebt),
            SumTerm.Minus(FinancialFigures.CashAndEquivalents),
        ],
        $"{FinancialFigures.TotalDebt} - {FinancialFigures.CashAndEquivalents}");

    public static SumMetricCalculator QuickAssets { get; } = new(
        FinancialMetrics.QuickAssets,
        Producer(FinancialMetrics.QuickAssets),
        Version1,
        UnitOfMeasure.Money,
        [
            SumTerm.Plus(FinancialFigures.CurrentAssets),
            SumTerm.Minus(FinancialFigures.Inventory),
        ],
        $"{FinancialFigures.CurrentAssets} - {FinancialFigures.Inventory}");

    // ---- margins ----------------------------------------------------------------------------

    public static RatioMetricCalculator GrossMargin { get; } = Ratio(
        FinancialMetrics.GrossMargin, FinancialFigures.GrossProfit, FinancialFigures.Revenue);

    public static RatioMetricCalculator OperatingMargin { get; } = Ratio(
        FinancialMetrics.OperatingMargin, FinancialFigures.OperatingIncome, FinancialFigures.Revenue);

    public static RatioMetricCalculator NetMargin { get; } = Ratio(
        FinancialMetrics.NetMargin, FinancialFigures.NetIncome, FinancialFigures.Revenue);

    /// <summary>Reads the computed free cash flow figure, not a reported one.</summary>
    public static RatioMetricCalculator FreeCashFlowMargin { get; } = Ratio(
        FinancialMetrics.FreeCashFlowMargin, FinancialFigures.FreeCashFlow, FinancialFigures.Revenue);

    // ---- liquidity and leverage --------------------------------------------------------------

    public static RatioMetricCalculator CurrentRatio { get; } = Ratio(
        FinancialMetrics.CurrentRatio, FinancialFigures.CurrentAssets, FinancialFigures.CurrentLiabilities);

    /// <summary>Reads the computed quick-assets figure, not a reported one.</summary>
    public static RatioMetricCalculator QuickRatio { get; } = Ratio(
        FinancialMetrics.QuickRatio, FinancialFigures.QuickAssets, FinancialFigures.CurrentLiabilities);

    public static RatioMetricCalculator DebtToEquity { get; } = Ratio(
        FinancialMetrics.DebtToEquity, FinancialFigures.TotalDebt, FinancialFigures.TotalEquity);

    // ---- returns and conversion ---------------------------------------------------------------

    public static RatioMetricCalculator ReturnOnEquity { get; } = Ratio(
        FinancialMetrics.ReturnOnEquity, FinancialFigures.NetIncome, FinancialFigures.TotalEquity);

    public static RatioMetricCalculator ReturnOnAssets { get; } = Ratio(
        FinancialMetrics.ReturnOnAssets, FinancialFigures.NetIncome, FinancialFigures.TotalAssets);

    public static RatioMetricCalculator CashConversion { get; } = Ratio(
        FinancialMetrics.CashConversion, FinancialFigures.OperatingCashFlow, FinancialFigures.NetIncome);

    /// <summary>Money over a share count, so the result is money per share.</summary>
    public static RatioMetricCalculator EarningsPerShareDiluted { get; } = new(
        FinancialMetrics.EarningsPerShareDiluted,
        Producer(FinancialMetrics.EarningsPerShareDiluted),
        Version1,
        UnitOfMeasure.Money,
        FinancialFigures.NetIncome,
        FinancialFigures.DilutedShares,
        $"{FinancialFigures.NetIncome} / {FinancialFigures.DilutedShares}");

    // ---- valuation -------------------------------------------------------------------------
    // Every one of these reads a market figure against a reported one, so all of them stay silent
    // until market data is supplied for the period. That is the correct behaviour: a valuation
    // ratio computed without a price is not a cheaper valuation, it is a different number.

    /// <summary>Reads the computed net-debt figure, not a reported one.</summary>
    public static SumMetricCalculator EnterpriseValue { get; } = new(
        FinancialMetrics.EnterpriseValue,
        Producer(FinancialMetrics.EnterpriseValue),
        Version1,
        UnitOfMeasure.Money,
        [
            SumTerm.Plus(FinancialFigures.MarketCapitalisation),
            SumTerm.Plus(FinancialFigures.NetDebt),
        ],
        $"{FinancialFigures.MarketCapitalisation} + {FinancialFigures.NetDebt}");

    public static RatioMetricCalculator PriceToEarnings { get; } = Ratio(
        FinancialMetrics.PriceToEarnings, FinancialFigures.MarketCapitalisation, FinancialFigures.NetIncome);

    public static RatioMetricCalculator PriceToBook { get; } = Ratio(
        FinancialMetrics.PriceToBook, FinancialFigures.MarketCapitalisation, FinancialFigures.TotalEquity);

    public static RatioMetricCalculator PriceToSales { get; } = Ratio(
        FinancialMetrics.PriceToSales, FinancialFigures.MarketCapitalisation, FinancialFigures.Revenue);

    /// <summary>Reads the computed enterprise-value and EBITDA figures, not reported ones.</summary>
    public static RatioMetricCalculator EnterpriseValueToEbitda { get; } = Ratio(
        FinancialMetrics.EnterpriseValueToEbitda, FinancialFigures.EnterpriseValue, FinancialFigures.Ebitda);

    // ---- period over period --------------------------------------------------------------------

    public static GrowthMetricCalculator RevenueGrowth { get; } = Growth(
        FinancialMetrics.RevenueGrowth, FinancialFigures.Revenue);

    public static GrowthMetricCalculator EarningsGrowth { get; } = Growth(
        FinancialMetrics.EarningsGrowth, FinancialFigures.NetIncome);

    /// <summary>
    /// Every calculator above, for cataloguing and for the tests that hold the set honest.
    /// </summary>
    /// <remarks>
    /// Declared last on purpose: static initialisers run in declaration order, so a list declared
    /// before its members would be a list of nulls.
    /// </remarks>
    public static IReadOnlyList<IMetricCalculator> All { get; } = new IMetricCalculator[]
    {
        FreeCashFlow,
        Ebitda,
        NetDebt,
        QuickAssets,
        GrossMargin,
        OperatingMargin,
        NetMargin,
        FreeCashFlowMargin,
        CurrentRatio,
        QuickRatio,
        DebtToEquity,
        ReturnOnEquity,
        ReturnOnAssets,
        CashConversion,
        EarningsPerShareDiluted,
        EnterpriseValue,
        PriceToEarnings,
        PriceToBook,
        PriceToSales,
        EnterpriseValueToEbitda,
        RevenueGrowth,
        EarningsGrowth,
    };

    /// <summary>
    /// The producing identity of a calculator, derived from what it measures so that a stored
    /// result's provenance names something a reader can look up.
    /// </summary>
    private static SourceId Producer(MetricId metric) => SourceId.Create($"calc.{metric.Value}");

    private static RatioMetricCalculator Ratio(MetricId metric, string numerator, string denominator) =>
        new(
            metric,
            Producer(metric),
            Version1,
            UnitOfMeasure.Ratio,
            numerator,
            denominator,
            $"{numerator} / {denominator}");

    private static GrowthMetricCalculator Growth(MetricId metric, string attribute) =>
        new(
            metric,
            Producer(metric),
            Version1,
            attribute,
            $"(current - prior) / |prior| of {attribute}");
}
