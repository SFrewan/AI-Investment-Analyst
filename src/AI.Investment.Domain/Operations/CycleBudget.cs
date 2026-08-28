using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Operations;

/// <summary>
/// What one cycle is allowed to spend before it stops and asks.
/// </summary>
/// <remarks>
/// <para>
/// Four ceilings, because a runaway cycle can run away in four different ways: it can take forever,
/// it can spend money at a model provider, it can exhaust a provider's rate limit, and it can take
/// too many actions. A budget covering only cost would let the third and fourth happen unnoticed.
/// </para>
/// <para>
/// <strong>Exceeding a budget suspends the cycle and escalates. It never truncates the analysis and
/// proceeds.</strong> That distinction is the whole point: a cycle that quietly analyses half its
/// evidence and then decides is more dangerous than one that stops, because its output looks exactly
/// like a complete one.
/// </para>
/// </remarks>
public sealed record CycleBudget
{
    private CycleBudget(
        TimeSpan maxWallClock,
        Money maxModelSpend,
        int maxProviderCalls,
        int maxActions)
    {
        MaxWallClock = maxWallClock;
        MaxModelSpend = maxModelSpend;
        MaxProviderCalls = maxProviderCalls;
        MaxActions = maxActions;
    }

    public TimeSpan MaxWallClock { get; }

    public Money MaxModelSpend { get; }

    public int MaxProviderCalls { get; }

    public int MaxActions { get; }

    public static CycleBudget Create(
        TimeSpan maxWallClock,
        Money maxModelSpend,
        int maxProviderCalls,
        int maxActions)
    {
        ArgumentNullException.ThrowIfNull(maxModelSpend);

        if (maxWallClock <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(maxWallClock),
                "A cycle must have a wall-clock ceiling. A cycle that can run forever holds a worker " +
                "slot forever, and the first symptom is a queue that stops moving.");
        }

        if (maxModelSpend.IsNegative)
        {
            throw new DomainValidationException(
                nameof(maxModelSpend),
                "A spend ceiling may not be negative.");
        }

        if (maxProviderCalls < 0)
        {
            throw new DomainValidationException(
                nameof(maxProviderCalls),
                "A provider-call ceiling may not be negative.");
        }

        if (maxActions < 0)
        {
            throw new DomainValidationException(
                nameof(maxActions),
                "An action ceiling may not be negative.");
        }

        return new CycleBudget(maxWallClock, maxModelSpend, maxProviderCalls, maxActions);
    }

    /// <summary>
    /// Whether the consumption so far is still inside every ceiling.
    /// </summary>
    /// <remarks>
    /// Fail-closed on currency: a spend recorded in a currency the budget is not denominated in is
    /// reported as exhausted rather than ignored. Ignoring it would make an unbudgeted currency the
    /// cheapest way to spend without limit.
    /// </remarks>
    public BudgetVerdict Check(CycleConsumption consumption, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(consumption);

        if (elapsed > MaxWallClock)
        {
            return BudgetVerdict.Exhausted(
                BudgetKind.WallClock,
                $"the cycle has run for {elapsed} against a ceiling of {MaxWallClock}");
        }

        if (consumption.ModelSpend.Currency != MaxModelSpend.Currency)
        {
            return BudgetVerdict.Exhausted(
                BudgetKind.ModelSpend,
                $"spend is recorded in {consumption.ModelSpend.Currency} but the budget is in " +
                $"{MaxModelSpend.Currency}; a ceiling that cannot be compared has not been shown to hold");
        }

        if (consumption.ModelSpend.IsGreaterThan(MaxModelSpend))
        {
            return BudgetVerdict.Exhausted(
                BudgetKind.ModelSpend,
                $"the cycle has spent {consumption.ModelSpend} against a ceiling of {MaxModelSpend}");
        }

        if (consumption.ProviderCalls > MaxProviderCalls)
        {
            return BudgetVerdict.Exhausted(
                BudgetKind.ProviderCalls,
                $"the cycle has made {consumption.ProviderCalls} provider calls against a ceiling of " +
                $"{MaxProviderCalls}");
        }

        if (consumption.Actions > MaxActions)
        {
            return BudgetVerdict.Exhausted(
                BudgetKind.Actions,
                $"the cycle has taken {consumption.Actions} actions against a ceiling of {MaxActions}");
        }

        return BudgetVerdict.Within;
    }

    public override string ToString() =>
        $"{MaxWallClock} / {MaxModelSpend} / {MaxProviderCalls} calls / {MaxActions} actions";
}

/// <summary>Which ceiling a cycle reached.</summary>
public enum BudgetKind
{
    None = 0,
    WallClock = 1,
    ModelSpend = 2,
    ProviderCalls = 3,
    Actions = 4,
}

/// <summary>Whether a cycle is still inside its budget, and if not, which ceiling it reached.</summary>
public sealed record BudgetVerdict
{
    private BudgetVerdict(bool isExhausted, BudgetKind kind, string explanation)
    {
        IsExhausted = isExhausted;
        Kind = kind;
        Explanation = explanation;
    }

    public bool IsExhausted { get; }

    public BudgetKind Kind { get; }

    public string Explanation { get; }

    public static BudgetVerdict Within { get; } =
        new(false, BudgetKind.None, "Within every configured budget.");

    public static BudgetVerdict Exhausted(BudgetKind kind, string explanation)
    {
        if (string.IsNullOrWhiteSpace(explanation))
        {
            throw new DomainValidationException(
                nameof(explanation),
                "An exhausted budget must say which ceiling was reached and by how much.");
        }

        return new BudgetVerdict(true, kind, explanation.Trim());
    }

    public override string ToString() => Explanation;
}
