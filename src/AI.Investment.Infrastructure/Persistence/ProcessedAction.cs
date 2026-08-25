namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// A claimed idempotency key. Infrastructure-only: this is a deduplication mechanism, not a
/// domain concept, so it has no place in the domain model.
/// </summary>
/// <remarks>
/// The key is the primary key. Claiming is an INSERT, so uniqueness is enforced by the database
/// rather than by a read-then-write in application code, which would race under concurrency -
/// exactly the condition retries create.
/// </remarks>
public sealed class ProcessedAction
{
    public ProcessedAction(string idempotencyKey, Guid proposalId, DateTime claimedAtUtc)
    {
        IdempotencyKey = idempotencyKey;
        ProposalId = proposalId;
        ClaimedAtUtc = claimedAtUtc;
    }

    private ProcessedAction()
    {
        IdempotencyKey = string.Empty;
    }

    public string IdempotencyKey { get; private set; }

    public Guid ProposalId { get; private set; }

    public DateTime ClaimedAtUtc { get; private set; }
}
