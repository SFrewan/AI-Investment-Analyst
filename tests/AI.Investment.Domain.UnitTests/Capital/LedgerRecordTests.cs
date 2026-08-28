using System.Reflection;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Capital;

/// <summary>
/// The guards, edges and wording of the ledger primitives.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CapitalLedgerTests"/> covers the accounting: entries balance, balances are projections,
/// currencies do not mix. This file covers the parts around it that mutation testing found unpinned -
/// the argument guards, the exact truncation edge, the sign convention on an account that decreases
/// when debited, and what each refusal actually says.
/// </para>
/// <para>
/// The messages are asserted because a ledger refusal is read by somebody trying to work out whether
/// the books are wrong or the caller was. "An entry cannot debit and credit the same account
/// ('positions')" answers that; an empty string does not, and an empty string denies just as firmly.
/// </para>
/// </remarks>
public sealed class LedgerRecordTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    // ---- Posting: argument guards --------------------------------------------------------------

    [Fact]
    public void An_entry_cannot_be_posted_without_a_debit_account() =>
        Assert.Throws<ArgumentNullException>(() =>
            LedgerEntry.Post(null!, LedgerAccount.Cash, Usd(1m), Now, "no debit"));

    [Fact]
    public void An_entry_cannot_be_posted_without_a_credit_account() =>
        Assert.Throws<ArgumentNullException>(() =>
            LedgerEntry.Post(LedgerAccount.Positions, null!, Usd(1m), Now, "no credit"));

    [Fact]
    public void An_entry_cannot_be_posted_without_an_amount() =>
        Assert.Throws<ArgumentNullException>(() =>
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, null!, Now, "no amount"));

    // ---- Posting: refusals, and what they say ---------------------------------------------------

    [Fact]
    public void A_single_sided_entry_names_the_account_and_says_it_records_nothing()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            LedgerEntry.Post(
                LedgerAccount.Positions,
                LedgerAccount.Positions,
                Usd(1m),
                Now,
                "circular"));

        Assert.Contains("'positions'", error.Message, StringComparison.Ordinal);
        Assert.Contains("record nothing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_positive_entry_says_direction_is_expressed_by_the_account_not_the_sign()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(-1m), Now, "backwards"));

        Assert.Contains("moves a positive amount", error.Message, StringComparison.Ordinal);
        Assert.Contains("two ways of being wrong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexplained_entry_says_nobody_will_be_able_to_reconcile_it()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(1m), Now, "   "));

        Assert.Contains("must say what it is for", error.Message, StringComparison.Ordinal);
        Assert.Contains("reconcile later", error.Message, StringComparison.Ordinal);
    }

    // ---- Description length ---------------------------------------------------------------------

    [Fact]
    public void A_description_of_exactly_the_maximum_length_is_kept_whole()
    {
        var description = new string('d', LedgerEntry.MaxDescriptionLength);

        var entry = LedgerEntry.Post(
            LedgerAccount.Positions,
            LedgerAccount.Cash,
            Usd(1m),
            Now,
            description);

        Assert.Equal(description, entry.Description);
    }

    /// <summary>
    /// A longer description is truncated rather than refused. An entry that failed to post because
    /// somebody wrote too much would leave the books unbalanced, which is worse than a clipped
    /// sentence.
    /// </summary>
    [Fact]
    public void A_longer_description_is_truncated_rather_than_refused()
    {
        var entry = LedgerEntry.Post(
            LedgerAccount.Positions,
            LedgerAccount.Cash,
            Usd(1m),
            Now,
            new string('d', LedgerEntry.MaxDescriptionLength + 25));

        Assert.Equal(LedgerEntry.MaxDescriptionLength, entry.Description.Length);
    }

    // ---- Sign convention --------------------------------------------------------------------------

    [Fact]
    public void The_effect_on_no_account_at_all_is_refused()
    {
        var entry = LedgerEntry.Post(LedgerAccount.Positions, LedgerAccount.Cash, Usd(1m), Now, "bought");

        Assert.Throws<ArgumentNullException>(() => entry.EffectOn(null!));
    }

    /// <summary>
    /// An account that decreases when debited must decrease when debited.
    /// </summary>
    /// <remarks>
    /// Every entry in the covering tests debits an asset or an expense, both of which increase on the
    /// debit side, so the other half of the sign convention was never exercised. Returning capital to
    /// the owner is the ordinary case that does exercise it: contributed capital is equity, and
    /// debiting equity reduces it. Getting this backwards would leave every balance wrong by a sign
    /// while every entry looked reasonable.
    /// </remarks>
    [Fact]
    public void Debiting_an_account_that_increases_on_the_credit_side_reduces_it()
    {
        var entry = LedgerEntry.Post(
            LedgerAccount.ContributedCapital,
            LedgerAccount.Cash,
            Usd(500m),
            Now,
            "Capital returned to the owner");

        Assert.False(LedgerAccount.ContributedCapital.IncreasedByDebit);
        Assert.Equal(-500m, entry.EffectOn(LedgerAccount.ContributedCapital).Amount);
        Assert.Equal(-500m, entry.EffectOn(LedgerAccount.Cash).Amount);
    }

    // ---- Description and materialisation ------------------------------------------------------------

    [Fact]
    public void An_entry_describes_itself_for_a_human_reading_a_log()
    {
        var entry = LedgerEntry.Post(
            LedgerAccount.Positions,
            LedgerAccount.Cash,
            Usd(1_000m),
            Now,
            "Bought 10 AAPL");

        var described = entry.ToString();

        Assert.Contains("Bought 10 AAPL", described, StringComparison.Ordinal);
        Assert.Contains("positions", described, StringComparison.Ordinal);
        Assert.Contains("cash", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The persistence constructor must leave no non-nullable string null.
    /// </summary>
    [Fact]
    public void The_persistence_constructor_leaves_no_null_description()
    {
        var constructor = typeof(LedgerEntry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        Assert.NotNull(constructor);

        var entry = (LedgerEntry)constructor!.Invoke(null);

        Assert.Equal(string.Empty, entry.Description);
    }

    // ---- Projections: argument guards ----------------------------------------------------------------

    [Fact]
    public void A_balance_cannot_be_computed_for_no_account() =>
        Assert.Throws<ArgumentNullException>(() =>
            CapitalLedger.Balance(null!, [], Currency.Usd));

    [Fact]
    public void A_balance_cannot_be_computed_over_no_entries() =>
        Assert.Throws<ArgumentNullException>(() =>
            CapitalLedger.Balance(LedgerAccount.Cash, null!, Currency.Usd));

    [Fact]
    public void Every_balance_cannot_be_computed_over_no_entries() =>
        Assert.Throws<ArgumentNullException>(() =>
            CapitalLedger.Balances(null!, Currency.Usd));

    /// <summary>
    /// The currency check guards the projection over every account, not only the single-account one.
    /// </summary>
    [Fact]
    public void Balances_across_currencies_are_refused_rather_than_converted()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            CapitalLedger.Balances(Euros(), Currency.Usd));

        Assert.Equal("CapitalLedger.OneCurrency", error.Rule);
    }

    [Fact]
    public void A_currency_mismatch_names_both_currencies_and_the_rate_nobody_recorded()
    {
        var error = Assert.Throws<DomainRuleViolationException>(() =>
            CapitalLedger.Balance(LedgerAccount.Cash, Euros(), Currency.Usd));

        Assert.Contains("EUR", error.Message, StringComparison.Ordinal);
        Assert.Contains("USD", error.Message, StringComparison.Ordinal);
        Assert.Contains("exchange rate nobody recorded", error.Message, StringComparison.Ordinal);
    }

    private static LedgerEntry[] Euros() =>
        [
            LedgerEntry.Post(
                LedgerAccount.Positions,
                LedgerAccount.Cash,
                Money.Create(10m, Currency.Create("EUR")),
                Now,
                "Bought in euros"),
        ];
}
