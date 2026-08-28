using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Capital;

/// <summary>
/// One immutable double-entry line: an amount moved from one account to another.
/// </summary>
/// <remarks>
/// <para>
/// There is no setter, no update method and no delete method, and none will be added - for the same
/// reason there are none on an audit record. More to the point, <strong>there is no balance field
/// anywhere in this model</strong>. A balance is a projection of the entries, computed on demand.
/// A stored balance is a number that can be wrong while every entry behind it is right, and once it
/// is wrong nothing in the data says so.
/// </para>
/// <para>
/// Debit and credit are both required and must differ. A single-sided entry does not balance, and
/// the whole reason for double entry is that an error shows up as an imbalance rather than as a
/// plausible number.
/// </para>
/// </remarks>
public sealed class LedgerEntry
{
    public const int MaxDescriptionLength = 500;

    private LedgerEntry(
        Guid ledgerEntryId,
        LedgerAccount debit,
        LedgerAccount credit,
        Money amount,
        DateTime occurredAtUtc,
        string description,
        OpportunityId? opportunityId,
        Guid? executionId)
    {
        LedgerEntryId = ledgerEntryId;
        Debit = debit;
        Credit = credit;
        Amount = amount;
        OccurredAtUtc = occurredAtUtc;
        Description = description;
        OpportunityId = opportunityId;
        ExecutionId = executionId;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private LedgerEntry()
    {
        Debit = null!;
        Credit = null!;
        Amount = null!;
        Description = string.Empty;
    }

    public Guid LedgerEntryId { get; private set; }

    /// <summary>The account money moves into.</summary>
    public LedgerAccount Debit { get; private set; }

    /// <summary>The account money moves out of.</summary>
    public LedgerAccount Credit { get; private set; }

    public Money Amount { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public string Description { get; private set; }

    /// <summary>The opportunity this entry belongs to, when it has one.</summary>
    public OpportunityId? OpportunityId { get; private set; }

    /// <summary>The execution that produced it, when there was one.</summary>
    public Guid? ExecutionId { get; private set; }

    public static LedgerEntry Post(
        LedgerAccount debit,
        LedgerAccount credit,
        Money amount,
        DateTime occurredAtUtc,
        string description,
        OpportunityId? opportunityId = null,
        Guid? executionId = null)
    {
        ArgumentNullException.ThrowIfNull(debit);
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(amount);
        DateRange.EnsureUtc(occurredAtUtc, nameof(occurredAtUtc));

        if (debit == credit)
        {
            throw new DomainRuleViolationException(
                "LedgerEntry.TwoSides",
                $"An entry cannot debit and credit the same account ('{debit}'). It would balance " +
                "trivially and record nothing.");
        }

        if (!amount.IsPositive)
        {
            throw new DomainRuleViolationException(
                "LedgerEntry.PositiveAmount",
                "A ledger entry moves a positive amount. Direction is expressed by which account is " +
                "debited, not by the sign - allowing both makes every balance two ways of being wrong.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new DomainValidationException(
                nameof(description),
                "A ledger entry must say what it is for. An unexplained movement is one nobody can " +
                "reconcile later.");
        }

        var trimmed = description.Trim();

        return new LedgerEntry(
            Guid.NewGuid(),
            debit,
            credit,
            amount,
            occurredAtUtc,
            trimmed.Length <= MaxDescriptionLength ? trimmed : trimmed[..MaxDescriptionLength],
            opportunityId,
            executionId);
    }

    /// <summary>This entry's effect on <paramref name="account"/>, signed by its convention.</summary>
    public Money EffectOn(LedgerAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (account == Debit)
        {
            return account.IncreasedByDebit ? Amount : Amount.Negate();
        }

        if (account == Credit)
        {
            return account.IncreasedByDebit ? Amount.Negate() : Amount;
        }

        return Money.Zero(Amount.Currency);
    }

    public override string ToString() =>
        $"{OccurredAtUtc:O} {Amount} {Credit} -> {Debit} ({Description})";
}
