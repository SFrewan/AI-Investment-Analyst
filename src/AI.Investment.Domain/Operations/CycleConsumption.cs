using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Operations;

/// <summary>What a cycle has actually used so far. Monotonic: it only ever goes up.</summary>
/// <remarks>
/// Immutable, with <see cref="Plus"/> returning a new value rather than mutating. A running total
/// that can be assigned is a total that can be assigned downwards, and a cycle that can reset its
/// own consumption has no budget at all.
/// </remarks>
public sealed record CycleConsumption
{
    private CycleConsumption(Money modelSpend, int providerCalls, int actions)
    {
        ModelSpend = modelSpend;
        ProviderCalls = providerCalls;
        Actions = actions;
    }

    public Money ModelSpend { get; }

    public int ProviderCalls { get; }

    public int Actions { get; }

    public static CycleConsumption None(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new CycleConsumption(Money.Zero(currency), 0, 0);
    }

    public static CycleConsumption Create(Money modelSpend, int providerCalls, int actions)
    {
        ArgumentNullException.ThrowIfNull(modelSpend);

        if (modelSpend.IsNegative)
        {
            throw new DomainValidationException(
                nameof(modelSpend),
                "Consumption may not be negative. A negative entry would buy back budget that was " +
                "already spent.");
        }

        if (providerCalls < 0)
        {
            throw new DomainValidationException(nameof(providerCalls), "Consumption may not be negative.");
        }

        if (actions < 0)
        {
            throw new DomainValidationException(nameof(actions), "Consumption may not be negative.");
        }

        return new CycleConsumption(modelSpend, providerCalls, actions);
    }

    /// <summary>Adds usage. Refuses a negative contribution and a currency the total is not in.</summary>
    public CycleConsumption Plus(Money modelSpend, int providerCalls, int actions)
    {
        ArgumentNullException.ThrowIfNull(modelSpend);

        if (modelSpend.IsNegative || providerCalls < 0 || actions < 0)
        {
            throw new DomainRuleViolationException(
                "CycleConsumption.Monotonic",
                "Consumption only increases. A negative contribution would return budget the cycle " +
                "has already spent, which is how a ceiling stops being one.");
        }

        if (modelSpend.Currency != ModelSpend.Currency)
        {
            throw new CurrencyMismatchException(ModelSpend.Currency.Code, modelSpend.Currency.Code);
        }

        return new CycleConsumption(
            ModelSpend.Add(modelSpend),
            ProviderCalls + providerCalls,
            Actions + actions);
    }

    public override string ToString() =>
        $"{ModelSpend}, {ProviderCalls} calls, {Actions} actions";
}
