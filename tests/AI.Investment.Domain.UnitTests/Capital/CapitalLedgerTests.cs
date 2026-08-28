using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Capital;

/// <summary>
/// Double entry, and the invariants that make a balance mean something.
/// </summary>
/// <remarks>
/// The architecture's rule is that balances are projections of immutable entries and that no
/// settable balance field exists anywhere in the model. The second half is asserted by the
/// compiler - there is no setter to call - and the first half is what these tests measure.
/// </remarks>
public sealed class CapitalLedgerTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    [Fact]
    public void An_entry_moves_a_positive_amount_and_states_its_direction_by_account()
    {
        var entry = LedgerEntry.Post(
            LedgerAccount.Positions,
            LedgerAccount.Cash,
            Usd(1_000m),
            Now,
            "Bought 10 AAPL");

        Assert.Equal(1_000m, entry.EffectOn(LedgerAccount.Positions).Amount);
        Assert.Equal(-1_000m, entry.EffectOn(LedgerAccount.Cash).Amount);
        Assert.True(entry.EffectOn(LedgerAccount.Fees).IsZero);
    }

    [Fact]
    public void An_entry_may_not_be_negative_because_direction_is_not_a_sign()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(-1m), Now, "backwards"));

        Assert.Equal("LedgerEntry.PositiveAmount", error.Rule);
    }

    [Fact]
    public void A_zero_entry_records_nothing_and_is_refused()
    {
        Assert.Throws<DomainRuleViolationException>(() =>
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(0m), Now, "nothing"));
    }

    [Fact]
    public void An_entry_cannot_debit_and_credit_the_same_account()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            LedgerEntry.Post(LedgerAccount.Cash, LedgerAccount.Cash, Usd(1m), Now, "circular"));

        Assert.Equal("LedgerEntry.TwoSides", error.Rule);
    }

    [Fact]
    public void An_entry_must_say_what_it_is_for()
    {
        Assert.Throws<DomainValidationException>(() =>
            LedgerEntry.Post(LedgerAccount.Cash, LedgerAccount.Positions, Usd(1m), Now, "  "));
    }

    [Fact]
    public void An_entry_timestamp_must_be_utc()
    {
        Assert.Throws<DomainValidationException>(() =>
            LedgerEntry.Post(
                LedgerAccount.Cash,
                LedgerAccount.Positions,
                Usd(1m),
                DateTime.SpecifyKind(Now, DateTimeKind.Unspecified),
                "not utc"));
    }

    [Fact]
    public void An_account_with_no_sign_convention_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            LedgerAccount.Create("mystery", LedgerAccountKind.Unknown));
    }

    [Fact]
    public void A_purchase_and_its_fee_leave_the_books_balanced()
    {
        var opportunity = OpportunityId.New();

        var entries = new[]
        {
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(1_000m), Now, "Bought", opportunity),
            LedgerEntry.Post(LedgerAccount.Fees, LedgerAccount.Cash, Usd(1m), Now, "Fees", opportunity),
        };

        Assert.True(CapitalLedger.IsBalanced(entries, Currency.Usd));
        Assert.Equal(1_000m, CapitalLedger.Balance(LedgerAccount.Positions, entries, Currency.Usd).Amount);
        Assert.Equal(-1_001m, CapitalLedger.Balance(LedgerAccount.Cash, entries, Currency.Usd).Amount);
        Assert.Equal(1m, CapitalLedger.Balance(LedgerAccount.Fees, entries, Currency.Usd).Amount);
    }

    [Fact]
    public void A_disposal_at_a_gain_leaves_the_books_balanced()
    {
        var entries = new[]
        {
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(1_000m), Now, "Bought"),
            LedgerEntry.Post(LedgerAccount.Cash, LedgerAccount.Positions, Usd(1_200m), Now, "Sold"),
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.RealisedGains, Usd(200m), Now, "Gain"),
        };

        Assert.True(CapitalLedger.IsBalanced(entries, Currency.Usd));
        Assert.True(CapitalLedger.Balance(LedgerAccount.Positions, entries, Currency.Usd).IsZero);
        Assert.Equal(200m, CapitalLedger.Balance(LedgerAccount.Cash, entries, Currency.Usd).Amount);
    }

    [Fact]
    public void A_disposal_at_a_loss_leaves_the_books_balanced()
    {
        var entries = new[]
        {
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(1_000m), Now, "Bought"),
            LedgerEntry.Post(LedgerAccount.Cash, LedgerAccount.Positions, Usd(800m), Now, "Sold"),
            LedgerEntry.Post(LedgerAccount.RealisedLosses, LedgerAccount.Positions, Usd(200m), Now, "Loss"),
        };

        Assert.True(CapitalLedger.IsBalanced(entries, Currency.Usd));
        Assert.True(CapitalLedger.Balance(LedgerAccount.Positions, entries, Currency.Usd).IsZero);
        Assert.Equal(200m, CapitalLedger.Balance(LedgerAccount.RealisedLosses, entries, Currency.Usd).Amount);
    }

    [Fact]
    public void An_empty_ledger_balances_and_holds_nothing()
    {
        Assert.True(CapitalLedger.IsBalanced([], Currency.Usd));
        Assert.True(CapitalLedger.Balance(LedgerAccount.Cash, [], Currency.Usd).IsZero);
        Assert.Empty(CapitalLedger.Balances([], Currency.Usd));
    }

    [Fact]
    public void A_balance_across_currencies_is_refused_rather_than_converted()
    {
        var entries = new[]
        {
            LedgerEntry.Post(
                LedgerAccount.Positions,
                LedgerAccount.Cash,
                Money.Create(10m, Currency.Create("EUR")),
                Now,
                "Bought in euros"),
        };

        var error = Assert.Throws<DomainRuleViolationException>(() =>
            CapitalLedger.Balance(LedgerAccount.Cash, entries, Currency.Usd));

        Assert.Equal("CapitalLedger.OneCurrency", error.Rule);
    }

    [Fact]
    public void Every_account_touched_appears_in_the_balances()
    {
        var entries = new[]
        {
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(100m), Now, "Bought"),
            LedgerEntry.Post(LedgerAccount.Fees, LedgerAccount.Cash, Usd(2m), Now, "Fees"),
        };

        var balances = CapitalLedger.Balances(entries, Currency.Usd);

        Assert.Equal(3, balances.Count);
        Assert.Contains(LedgerAccount.Cash, balances.Keys);
        Assert.Contains(LedgerAccount.Positions, balances.Keys);
        Assert.Contains(LedgerAccount.Fees, balances.Keys);
    }
}
