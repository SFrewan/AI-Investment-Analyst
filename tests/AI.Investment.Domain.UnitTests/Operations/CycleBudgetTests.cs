using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Operations;

/// <summary>
/// A budget is a promise that a runaway cycle costs a bounded amount, so every way of running away
/// has a ceiling and every ceiling is checked.
/// </summary>
public sealed class CycleBudgetTests
{
    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    private static CycleBudget Budget() =>
        CycleBudget.Create(TimeSpan.FromMinutes(10), Usd(1m), 20, 2);

    [Fact]
    public void A_cycle_inside_every_ceiling_is_within_budget()
    {
        var verdict = Budget().Check(
            CycleConsumption.Create(Usd(0.50m), 10, 1),
            TimeSpan.FromMinutes(5));

        Assert.False(verdict.IsExhausted);
        Assert.Equal(BudgetKind.None, verdict.Kind);
        Assert.Equal("Within every configured budget.", verdict.Explanation);
    }

    [Theory]
    [InlineData(11, 0.5, 10, 1, BudgetKind.WallClock)]
    [InlineData(5, 2.0, 10, 1, BudgetKind.ModelSpend)]
    [InlineData(5, 0.5, 21, 1, BudgetKind.ProviderCalls)]
    [InlineData(5, 0.5, 10, 3, BudgetKind.Actions)]
    public void Every_ceiling_is_checked(
        int minutes,
        double spend,
        int calls,
        int actions,
        BudgetKind expected)
    {
        var verdict = Budget().Check(
            CycleConsumption.Create(Usd((decimal)spend), calls, actions),
            TimeSpan.FromMinutes(minutes));

        Assert.True(verdict.IsExhausted);
        Assert.Equal(expected, verdict.Kind);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Explanation));
    }

    /// <summary>
    /// Ignoring an unbudgeted currency would make it the cheapest way to spend without limit.
    /// </summary>
    [Fact]
    public void Spend_in_a_currency_the_budget_is_not_in_reports_as_exhausted()
    {
        var verdict = Budget().Check(
            CycleConsumption.Create(Money.Create(0.01m, "EUR"), 0, 0),
            TimeSpan.Zero);

        Assert.True(verdict.IsExhausted);
        Assert.Equal(BudgetKind.ModelSpend, verdict.Kind);
        Assert.Contains("cannot be compared", verdict.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_must_have_a_wall_clock_ceiling()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            CycleBudget.Create(TimeSpan.Zero, Usd(1m), 1, 1));

        Assert.Contains("run forever", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_may_not_be_negative()
    {
        Assert.Throws<DomainValidationException>(() =>
            CycleBudget.Create(TimeSpan.FromMinutes(1), Usd(-1m), 1, 1));

        Assert.Throws<DomainValidationException>(() =>
            CycleBudget.Create(TimeSpan.FromMinutes(1), Usd(1m), -1, 1));

        Assert.Throws<DomainValidationException>(() =>
            CycleBudget.Create(TimeSpan.FromMinutes(1), Usd(1m), 1, -1));
    }

    /// <summary>
    /// A cycle that can return budget it has already spent has no ceiling at all.
    /// </summary>
    [Fact]
    public void Consumption_only_increases()
    {
        var consumption = CycleConsumption.None(Currency.Usd).Plus(Usd(0.25m), 2, 1);

        Assert.Equal(0.25m, consumption.ModelSpend.Amount);

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            consumption.Plus(Usd(-0.10m), 0, 0));

        Assert.Equal("CycleConsumption.Monotonic", error.Rule);

        Assert.Throws<DomainRuleViolationException>(() => consumption.Plus(Usd(0m), -1, 0));
        Assert.Throws<DomainRuleViolationException>(() => consumption.Plus(Usd(0m), 0, -1));
    }

    [Fact]
    public void Consumption_refuses_a_currency_it_is_not_denominated_in() =>
        Assert.Throws<CurrencyMismatchException>(() =>
            CycleConsumption.None(Currency.Usd).Plus(Money.Create(1m, "EUR"), 0, 0));

    [Fact]
    public void An_exhausted_verdict_must_say_which_ceiling_was_reached() =>
        Assert.Throws<DomainValidationException>(() =>
            BudgetVerdict.Exhausted(BudgetKind.Actions, "  "));
}
