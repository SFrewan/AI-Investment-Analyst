using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Capital;

/// <summary>One account and what it holds.</summary>
public sealed record LedgerBalanceDto(string Account, string Kind, decimal Amount, string Currency);

/// <summary>One posting, as a reconciliation view shows it.</summary>
public sealed record LedgerEntryDto(
    Guid LedgerEntryId,
    string DebitAccount,
    string CreditAccount,
    decimal Amount,
    string Currency,
    DateTime OccurredAtUtc,
    string Description,
    Guid? OpportunityId,
    Guid? ExecutionId);

/// <summary>
/// The capital read model: balances, whether the books balance, and the postings behind them.
/// </summary>
/// <remarks>
/// <c>IsBalanced</c> is reported rather than assumed. Double entry is only a guarantee while
/// something checks it, and a reconciliation view whose first line is "the books balance: false" is
/// the cheapest possible detector of a defect in the posting rules.
/// </remarks>
public sealed record LedgerReportDto(
    string Currency,
    bool IsBalanced,
    int EntryCount,
    IReadOnlyList<LedgerBalanceDto> Balances);

/// <summary>Reads the ledger. No write path exists on this interface.</summary>
public interface ILedgerReport
{
    Task<LedgerReportDto> GetAsync(Currency currency, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LedgerEntryDto>> GetEntriesAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class LedgerReport : ILedgerReport
{
    private readonly ILedgerStore _store;

    public LedgerReport(ILedgerStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<LedgerReportDto> GetAsync(
        Currency currency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currency);

        var all = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var entries = all.Where(entry => entry.Amount.Currency == currency).ToList();

        var balances = CapitalLedger.Balances(entries, currency)
            .Select(pair => new LedgerBalanceDto(
                pair.Key.Name,
                pair.Key.Kind.ToString(),
                pair.Value.Amount,
                pair.Value.Currency.Code))
            .OrderBy(balance => balance.Account, StringComparer.Ordinal)
            .ToList();

        return new LedgerReportDto(
            currency.Code,
            CapitalLedger.IsBalanced(entries, currency),
            entries.Count,
            balances);
    }

    public async Task<IReadOnlyList<LedgerEntryDto>> GetEntriesAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default)
    {
        var entries = await _store
            .ListForAsync(opportunityId, cancellationToken)
            .ConfigureAwait(false);

        return entries
            .Select(entry => new LedgerEntryDto(
                entry.LedgerEntryId,
                entry.Debit.Name,
                entry.Credit.Name,
                entry.Amount.Amount,
                entry.Amount.Currency.Code,
                entry.OccurredAtUtc,
                entry.Description,
                entry.OpportunityId?.Value,
                entry.ExecutionId))
            .ToList();
    }
}
