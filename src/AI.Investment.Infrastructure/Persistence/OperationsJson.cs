using System.Text.Json;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// Stores a cycle's budget and its consumption as one column each.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not owned entity types. An owned value is tracked as its own change-tracker entry,
/// and the write guard's rule for cycles is a list of column names - "the cycle may record its
/// progress" has to be a statement about named columns, not about a graph whose shape decides what
/// the rule covers. One converted column each keeps the budget frozen and the consumption writable,
/// visibly and by construction.
/// </para>
/// <para>
/// Round-trips through the domain factories rather than through property setters, so a row that has
/// been edited by hand into something the domain would refuse - a negative ceiling, a consumption in
/// a currency the budget is not in - fails on read instead of becoming a cycle operating outside
/// rules it appears to satisfy.
/// </para>
/// </remarks>
internal static class OperationsJson
{
    private sealed record BudgetDto(
        long MaxWallClockTicks,
        decimal MaxModelSpend,
        string Currency,
        int MaxProviderCalls,
        int MaxActions);

    private sealed record ConsumptionDto(
        decimal ModelSpend,
        string Currency,
        int ProviderCalls,
        int Actions);

    internal static string Write(CycleBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        return JsonSerializer.Serialize(new BudgetDto(
            budget.MaxWallClock.Ticks,
            budget.MaxModelSpend.Amount,
            budget.MaxModelSpend.Currency.Code,
            budget.MaxProviderCalls,
            budget.MaxActions));
    }

    internal static CycleBudget ReadBudget(string value)
    {
        var dto = JsonSerializer.Deserialize<BudgetDto>(value)
            ?? throw new InvalidOperationException("A stored cycle budget could not be read.");

        return CycleBudget.Create(
            TimeSpan.FromTicks(dto.MaxWallClockTicks),
            Money.Create(dto.MaxModelSpend, dto.Currency),
            dto.MaxProviderCalls,
            dto.MaxActions);
    }

    internal static string Write(CycleConsumption consumption)
    {
        ArgumentNullException.ThrowIfNull(consumption);

        return JsonSerializer.Serialize(new ConsumptionDto(
            consumption.ModelSpend.Amount,
            consumption.ModelSpend.Currency.Code,
            consumption.ProviderCalls,
            consumption.Actions));
    }

    internal static CycleConsumption ReadConsumption(string value)
    {
        var dto = JsonSerializer.Deserialize<ConsumptionDto>(value)
            ?? throw new InvalidOperationException("A stored cycle consumption could not be read.");

        return CycleConsumption.Create(
            Money.Create(dto.ModelSpend, dto.Currency),
            dto.ProviderCalls,
            dto.Actions);
    }
}
