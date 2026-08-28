using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Opportunities;

namespace AI.Investment.Application.Abstractions;

/// <summary>Appends ledger entries and reads them back.</summary>
/// <remarks>
/// Append and read only. There is no update and no delete, for the same reason there is none on the
/// audit trail: a ledger the application can rewrite is not a ledger. A correction is another entry.
/// </remarks>
public interface ILedgerStore
{
    /// <summary>
    /// Appends entries as one unit.
    /// </summary>
    /// <remarks>
    /// A double-entry posting is several rows that only balance together, so they are written
    /// together or not at all. Appending them one at a time would leave a window in which the books
    /// are visibly wrong, and a reader during that window cannot tell it from a real imbalance.
    /// </remarks>
    Task AppendAsync(IEnumerable<LedgerEntry> entries, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LedgerEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LedgerEntry>> ListForAsync(
        OpportunityId opportunityId,
        CancellationToken cancellationToken = default);
}
