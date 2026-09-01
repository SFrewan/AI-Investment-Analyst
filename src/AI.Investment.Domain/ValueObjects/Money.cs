using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// An amount of a specific currency.
/// </summary>
/// <remarks>
/// <para>
/// Three rules are deliberate and non-negotiable:
/// </para>
/// <list type="number">
/// <item>An amount never exists without a currency. There is no constructor that takes a bare
/// <see cref="decimal"/>.</item>
/// <item>Arithmetic between different currencies throws
/// <see cref="CurrencyMismatchException"/> rather than converting. There is no ambient
/// exchange rate anywhere in this system, and Phase 1 deliberately builds no FX mechanism -
/// conversion, when it exists, will be an explicit operation carrying its own rate, source and
/// timestamp, and will produce a <c>Claim</c> with provenance like any other derived value.</item>
/// <item>No implicit conversion to or from <see cref="decimal"/> exists. An implicit conversion
/// would let a currency-free number re-enter the model silently, which is the failure this type
/// exists to prevent.</item>
/// </list>
/// <para>
/// Rounding is not applied here. The stored amount is exactly what was supplied; rounding is a
/// presentation and settlement concern that depends on the currency's minor unit and on the
/// venue's rules, and applying it early destroys information.
/// </para>
/// </remarks>
public sealed record Money
{
    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    public static Money Create(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(amount, currency);
    }

    public static Money Create(decimal amount, string currencyCode) =>
        new(amount, Currency.Create(currencyCode));

    public static Money Zero(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(0m, currency);
    }

    /// <summary>Zero US dollars. Convenience for actions with no financial effect.</summary>
    /// <remarks>
    /// <strong>A fresh instance on every access, not a cached singleton.</strong> This type is
    /// mapped as an owned entity, and the persistence provider associates an owned instance with
    /// its owner by reference. One shared instance held by two owners in the same save is one
    /// object with two owners, which the provider resolves by writing one of them as null - it
    /// surfaced here as a not-null violation the first time two sources were seeded together.
    /// Value equality is unaffected: this is a record, so two instances with the same values are
    /// equal and hash alike. Same rule, same reason, as <c>LedgerAccount</c>.
    /// </remarks>
    public static Money ZeroUsd => new(0m, Currency.Usd);

    public Money Add(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(this, other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(this, other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money MultiplyBy(decimal factor) => new(Amount * factor, Currency);

    public Money Negate() => new(-Amount, Currency);

    public Money Abs() => new(Math.Abs(Amount), Currency);

    /// <summary>
    /// Compares two amounts of the SAME currency. Throws for a currency mismatch rather than
    /// returning an arbitrary ordering.
    /// </summary>
    public bool IsGreaterThan(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(this, other);
        return Amount > other.Amount;
    }

    public static Money operator +(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Add(right);
    }

    public static Money operator -(Money left, Money right)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.Subtract(right);
    }

    public static Money operator *(Money left, decimal factor)
    {
        ArgumentNullException.ThrowIfNull(left);
        return left.MultiplyBy(factor);
    }

    public static Money operator -(Money value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Negate();
    }

    /// <summary>
    /// The amount and its currency, in a form that does not depend on the amount's scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This text is hashed.</strong> <c>ActionFingerprint</c> takes a proposal's estimated
    /// cost and exposure through here, and an approval token is bound to that hash - so the string
    /// has to be a function of the value alone. It was not: a decimal carries its scale, and a
    /// money column declared <c>numeric(18,4)</c> hands every amount back at scale four. The same
    /// zero was <c>"0 USD"</c> before a save and <c>"0.0000 USD"</c> after one, which would have
    /// made every approval fail against a reloaded proposal.
    /// </para>
    /// <para>
    /// Equality and hashing were never affected - <see cref="decimal"/> already compares and
    /// hashes across scales. Only the text moved, which is precisely why nothing caught it. See
    /// <see cref="CanonicalNumber"/> for the formatting rule and its reasoning.
    /// </para>
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{CanonicalNumber.Text(Amount)} {Currency.Code}");

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency.Code, right.Currency.Code);
        }
    }
}
