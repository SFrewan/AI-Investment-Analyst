namespace AI.Investment.Domain.Capital;

/// <summary>
/// Which side of the books an account sits on, which is what fixes its sign convention.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is zero and is never a usable account kind. Without it, an account whose
/// kind was never set would silently adopt whichever convention happened to be first, and every
/// balance computed from it would be wrong by a sign - the kind of error that reconciles to zero
/// overall and is invisible per account.
/// </remarks>
public enum LedgerAccountKind
{
    /// <summary>Never valid on a real account.</summary>
    Unknown = 0,

    /// <summary>Cash, positions, receivables. Increased by a debit.</summary>
    Asset = 1,

    /// <summary>Amounts owed. Increased by a credit.</summary>
    Liability = 2,

    /// <summary>Capital contributed and retained. Increased by a credit.</summary>
    Equity = 3,

    /// <summary>Realised gains. Increased by a credit.</summary>
    Income = 4,

    /// <summary>Fees, commissions, provider costs. Increased by a debit.</summary>
    Expense = 5,
}
