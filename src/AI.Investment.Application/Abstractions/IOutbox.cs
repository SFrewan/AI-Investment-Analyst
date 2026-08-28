namespace AI.Investment.Application.Abstractions;

/// <summary>
/// One fact to be delivered, written in the same transaction as the change that caused it.
/// </summary>
/// <param name="MessageType">A versioned name, so a handler can refuse a shape it does not know.</param>
/// <param name="Payload">The body. Structured text; never a secret, because these rows are permanent.</param>
/// <param name="DedupKey">
/// What makes this message this message. Enqueuing the same fact twice queues it once, which is what
/// makes the step that produced it safe to retry.
/// </param>
/// <param name="CorrelationId">The work this belongs to.</param>
/// <param name="CycleId">The operating cycle, when one produced it.</param>
public sealed record OutboxEnvelope(
    string MessageType,
    string Payload,
    string DedupKey,
    string CorrelationId,
    Guid? CycleId = null);

/// <summary>A queued message being handed to its handler.</summary>
public sealed record DeliveredMessage(
    Guid OutboxMessageId,
    string MessageType,
    string Payload,
    string CorrelationId,
    Guid? CycleId,
    DateTime OccurredAtUtc,
    int Attempts);

/// <summary>Queues a message for delivery.</summary>
/// <remarks>
/// <para>
/// The queue, and the only one. Anything leaving the process goes through here rather than being
/// called directly, so that a database commit and an external effect cannot disagree about whether
/// something happened.
/// </para>
/// <para>
/// Deliberately narrow: enqueue only. Reading and dispatching belong to the dispatcher, and giving
/// application code a way to pull messages off the queue would put two things in the position of
/// believing they had delivered one.
/// </para>
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Stages a message. Returns false when its deduplication key is already queued.
    /// </summary>
    Task<bool> EnqueueAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>Handles one class of queued message.</summary>
/// <remarks>
/// <strong>Handlers must be idempotent.</strong> The queue delivers at least once - a dispatcher
/// that delivered and died before recording it will deliver again - so a handler that is not safe to
/// run twice turns a recovered crash into a duplicate effect.
/// </remarks>
public interface IOutboxHandler
{
    /// <summary>The message type this handler is for.</summary>
    string MessageType { get; }

    Task HandleAsync(DeliveredMessage message, CancellationToken cancellationToken = default);
}

/// <summary>What one pass of the dispatcher did.</summary>
/// <param name="Claimed">Messages leased for delivery.</param>
/// <param name="Dispatched">Messages delivered and recorded as delivered.</param>
/// <param name="Failed">Messages whose handler threw, and which will be retried.</param>
/// <param name="Abandoned">Messages out of attempts. Every one raises an escalation.</param>
/// <param name="Unhandled">Messages whose type no registered handler claims.</param>
public sealed record OutboxDispatchSummary(
    int Claimed,
    int Dispatched,
    int Failed,
    int Abandoned,
    int Unhandled)
{
    /// <summary>True when this pass found nothing to do.</summary>
    public bool WasIdle => Claimed == 0;
}

/// <summary>Delivers queued messages to their handlers.</summary>
public interface IOutboxDispatcher
{
    Task<OutboxDispatchSummary> DispatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default);
}
