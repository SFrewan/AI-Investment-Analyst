using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.ValueObjects;

public sealed class MoneyTests
{
    private static readonly Currency Usd = Currency.Create("USD");
    private static readonly Currency Eur = Currency.Create("EUR");

    [Fact]
    public void Amounts_in_the_same_currency_add() =>
        Assert.Equal(Money.Create(30m, Usd), Money.Create(10m, Usd) + Money.Create(20m, Usd));

    [Fact]
    public void Amounts_in_the_same_currency_subtract() =>
        Assert.Equal(Money.Create(-10m, Usd), Money.Create(10m, Usd) - Money.Create(20m, Usd));

    /// <summary>
    /// The most important test in this file. Silent currency coercion is one of the classic
    /// sources of expensive, invisible error in financial software; there must be no ambient
    /// exchange rate anywhere.
    /// </summary>
    [Fact]
    public void Adding_different_currencies_throws_rather_than_converting() =>
        Assert.Throws<CurrencyMismatchException>(() => Money.Create(10m, Usd) + Money.Create(10m, Eur));

    [Fact]
    public void Subtracting_different_currencies_throws() =>
        Assert.Throws<CurrencyMismatchException>(() => Money.Create(10m, Usd) - Money.Create(10m, Eur));

    [Fact]
    public void Comparing_different_currencies_throws_rather_than_ordering_arbitrarily() =>
        Assert.Throws<CurrencyMismatchException>(() => Money.Create(10m, Usd).IsGreaterThan(Money.Create(1m, Eur)));

    [Fact]
    public void Same_amount_in_different_currencies_is_not_equal() =>
        Assert.NotEqual(Money.Create(10m, Usd), Money.Create(10m, Eur));

    [Fact]
    public void Equality_is_by_value() =>
        Assert.Equal(Money.Create(10.50m, Usd), Money.Create(10.50m, Usd));

    [Fact]
    public void Multiplication_by_a_scalar_keeps_the_currency()
    {
        var result = Money.Create(10m, Usd) * 3m;

        Assert.Equal(30m, result.Amount);
        Assert.Equal(Usd, result.Currency);
    }

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(1, false, true, false)]
    [InlineData(-1, false, false, true)]
    public void Sign_predicates_are_correct(decimal amount, bool zero, bool positive, bool negative)
    {
        var money = Money.Create(amount, Usd);

        Assert.Equal(zero, money.IsZero);
        Assert.Equal(positive, money.IsPositive);
        Assert.Equal(negative, money.IsNegative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    public void Currency_rejects_codes_that_are_not_iso_4217_shaped(string code) =>
        Assert.Throws<DomainValidationException>(() => Currency.Create(code));

    [Fact]
    public void Currency_normalises_case() =>
        Assert.Equal(Currency.Create("USD"), Currency.Create("usd"));
}
