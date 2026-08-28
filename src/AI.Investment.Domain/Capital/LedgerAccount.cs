using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Capital;

/// <summary>An account in the double-entry ledger: a name and the side of the books it sits on.</summary>
/// <remarks>
/// The well-known accounts below are the minimum a simulated venue needs to record a complete
/// round trip. They are properties rather than an enum for the same reason opportunity types are:
/// a new account should not require a change to the core, and per-strategy or per-venue accounts
/// are a normal thing to want.
/// </remarks>
public sealed record LedgerAccount
{
    public const int MaxNameLength = 60;

    private LedgerAccount(string name, LedgerAccountKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>Uninvested capital.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A fresh instance on every access, not a cached singleton.</strong> These accounts are
    /// mapped as owned entities, and the persistence provider tracks an owned instance against the
    /// entity that owns it. A shared instance referenced by two entries in the same save is one
    /// object with two owners, and the provider resolves that by writing one of them as null - which
    /// surfaced as a not-null violation on <c>credit_account</c> when a purchase and its fee were
    /// appended together.
    /// </para>
    /// <para>
    /// Value equality is unaffected: <see cref="LedgerAccount"/> is a record, so two instances naming
    /// the same account are equal, hash the same, and key a balance dictionary identically.
    /// </para>
    /// </remarks>
    public static LedgerAccount Cash => Create("cash", LedgerAccountKind.Asset);

    /// <summary>The market value committed to open positions.</summary>
    public static LedgerAccount Positions => Create("positions", LedgerAccountKind.Asset);

    /// <summary>Capital put in by the owner.</summary>
    public static LedgerAccount ContributedCapital => Create("contributed-capital", LedgerAccountKind.Equity);

    /// <summary>Gains that have been realised, not merely marked.</summary>
    public static LedgerAccount RealisedGains => Create("realised-gains", LedgerAccountKind.Income);

    /// <summary>Losses that have been realised.</summary>
    public static LedgerAccount RealisedLosses => Create("realised-losses", LedgerAccountKind.Expense);

    /// <summary>Commissions, spreads and venue charges.</summary>
    public static LedgerAccount Fees => Create("fees", LedgerAccountKind.Expense);

    public string Name { get; }

    public LedgerAccountKind Kind { get; }

    /// <summary>True when a debit increases this account.</summary>
    public bool IncreasedByDebit => Kind is LedgerAccountKind.Asset or LedgerAccountKind.Expense;

    public static LedgerAccount Create(string name, LedgerAccountKind kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(nameof(name), "A ledger account requires a name.");
        }

        if (kind == LedgerAccountKind.Unknown || !Enum.IsDefined(kind))
        {
            throw new DomainValidationException(
                nameof(kind),
                $"'{kind}' is not a usable account kind. Without one, the account has no sign " +
                "convention and every balance computed from it is wrong by a sign.");
        }

        var normalised = name.Trim().ToLowerInvariant();

        if (normalised.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"A ledger account name may not exceed {MaxNameLength} characters.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                throw new DomainValidationException(
                    nameof(name),
                    $"A ledger account name may contain only lower-case letters, digits and '-'. " +
                    $"Received '{name}'.");
            }
        }

        return new LedgerAccount(normalised, kind);
    }

    public override string ToString() => Name;
}
