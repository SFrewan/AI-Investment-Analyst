using AI.Investment.Domain.Portfolio;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Where applied fills are kept. Append-only, and keyed by the venue's own reference.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="AppendAsync"/> is idempotent by contract, not by convention.</strong> It
/// answers whether the event was new. Applying a fill twice must not double a holding, and the
/// place to make that impossible is the uniqueness constraint on the venue reference - not a
/// check-then-insert in application code, which two concurrent callers pass simultaneously.
/// </para>
/// <para>
/// There is deliberately no update and no delete. A position is replayed from these rows; a model
/// whose history could be edited would be a model whose holdings could be edited, and the ledger
/// beside it cannot be.
/// </para>
/// </remarks>
public interface IPositionEventStore
{
    /// <summary>
    /// Records a fill's effect on a holding. Returns false when this venue reference is already
    /// recorded, in which case nothing is written.
    /// </summary>
    Task<bool> AppendAsync(PositionEvent positionEvent, CancellationToken cancellationToken = default);

    /// <summary>Every recorded event, in no guaranteed order - the calculator orders them.</summary>
    Task<IReadOnlyList<PositionEvent>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The events for one instrument.</summary>
    Task<IReadOnlyList<PositionEvent>> ListForAsync(
        string instrument,
        CancellationToken cancellationToken = default);
}
