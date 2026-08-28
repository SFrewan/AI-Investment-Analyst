using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Capital;

/// <summary>
/// Balances, computed from entries. Nothing here stores one.
/// </summary>
/// <remarks>
/// <para>
/// A projection rather than an aggregate, and deliberately a static one: there is no object holding
/// a running total that could drift from the entries behind it. Every figure this produces is
/// recomputed from the entries it was given, so a wrong balance means a wrong entry, which is
/// findable.
/// </para>
/// <para>
/// <see cref="IsBalanced"/> is the property double entry exists for. Every entry moves one amount
/// between two accounts, so the signed effects across every account must sum to zero. A non-zero
/// total is not a rounding artifact - decimal arithmetic here is exact - it is a defect.
/// </para>
/// </remarks>
public static class CapitalLedger
{
    /// <summary>The balance of one account over the entries supplied.</summary>
    public static Money Balance(LedgerAccount account, IEnumerable<LedgerEntry> entries, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(entries);

        var total = Money.Zero(currency);

        foreach (var entry in entries)
        {
            EnsureCurrency(entry, currency);

            total = total.Add(entry.EffectOn(account));
        }

        return total;
    }

    /// <summary>
    /// Every account touched, with its balance.
    /// </summary>
    /// <remarks>
    /// Returned as a concrete dictionary rather than an interface: it is built here, handed to one
    /// caller, and the interface bought an indirection on every lookup (CA1859).
    /// </remarks>
    public static Dictionary<LedgerAccount, Money> Balances(
        IEnumerable<LedgerEntry> entries,
        Currency currency)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var balances = new Dictionary<LedgerAccount, Money>();
        var materialised = entries.ToList();

        foreach (var entry in materialised)
        {
            EnsureCurrency(entry, currency);

            Accumulate(balances, entry.Debit, entry.EffectOn(entry.Debit), currency);
            Accumulate(balances, entry.Credit, entry.EffectOn(entry.Credit), currency);
        }

        return balances;
    }

    /// <summary>
    /// True when the signed effects across every account sum to zero, as double entry requires.
    /// </summary>
    /// <summary>
    /// True when the books satisfy the accounting identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Debit-natured balances are added and credit-natured ones subtracted</strong>, which is
    /// the identity assets + expenses = liabilities + equity + income restated as a sum that must
    /// come to zero. Adding every balance with the same sign - which an earlier version of this
    /// method did - happens to come to zero for a purchase and its fee, and comes to twice the gain
    /// the moment a disposal credits income. A check that passes on the entries you happen to write
    /// first and fails on the ones that make money is worse than no check, because it is trusted.
    /// </para>
    /// <para>
    /// It is reported rather than asserted, and the capital read model shows it. Double entry is a
    /// guarantee only while something checks it.
    /// </para>
    /// </remarks>
    public static bool IsBalanced(IEnumerable<LedgerEntry> entries, Currency currency)
    {
        var total = Money.Zero(currency);

        foreach (var balance in Balances(entries, currency))
        {
            total = balance.Key.IncreasedByDebit
                ? total.Add(balance.Value)
                : total.Subtract(balance.Value);
        }

        return total.IsZero;
    }

    private static void Accumulate(
        Dictionary<LedgerAccount, Money> balances,
        LedgerAccount account,
        Money effect,
        Currency currency)
    {
        balances[account] = balances.TryGetValue(account, out var running)
            ? running.Add(effect)
            : Money.Zero(currency).Add(effect);
    }

    private static void EnsureCurrency(LedgerEntry entry, Currency currency)
    {
        if (entry.Amount.Currency != currency)
        {
            throw new DomainRuleViolationException(
                "CapitalLedger.OneCurrency",
                $"Entry {entry.LedgerEntryId} is in {entry.Amount.Currency} but the balance is being " +
                $"computed in {currency}. A total across currencies is not a quantity of anything, and " +
                "converting one silently would bury an exchange rate nobody recorded.");
        }
    }
}
