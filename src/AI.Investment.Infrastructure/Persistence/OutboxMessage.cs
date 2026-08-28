using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>Where a queued message is in its life.</summary>
public enum OutboxStatus
{
    /// <summary>Not determined. Never dispatched: a message in this state is a defect, not a queue entry.</summary>
    Unknown = 0,

    /// <summary>Waiting to be delivered, or waiting for its next attempt.</summary>
    Pending = 1,

    /// <summary>Delivered exactly once.</summary>
    Dispatched = 2,

    /// <summary>Out of attempts. Never silently: abandoning one raises an escalation.</summary>
    Abandoned = 3,
}

/// <summary>
/// One message written in the same transaction as the state change that caused it.
/// </summary>
/// <remarks>
/// <para>
/// The transactional outbox, and the problem it solves: a database commit and an external call must
/// not be able to disagree about whether something happened. Writing the message as an ordinary row
/// in the same transaction as the change means the two either both exist or neither does; delivery
/// then becomes a separate, retryable step that cannot lose the fact it is delivering.
/// </para>
/// <para>
/// Infrastructure-only, exactly like <see cref="ProcessedAction"/>. This is a delivery mechanism
/// rather than something the business has an opinion about, and putting it in the domain would make
/// every domain type aware of how the platform happens to talk to itself.
/// </para>
/// <para>
/// <strong>Delivered at least once, processed at most once.</strong> The queue guarantees the first;
/// <see cref="DedupKey"/> and the handler's own idempotency guarantee the second. A design that
/// claimed exactly-once delivery would be claiming something the network does not offer.
/// </para>
/// <para>
/// The database context permits exactly seven fields of this row to change afterwards - the delivery
/// state - and freezes the rest. A message whose payload could be edited after it was queued would
/// make the atomicity above decorative.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    public const int MaxMessageTypeLength = 100;

    public const int MaxDedupKeyLength = 200;

    public const int MaxErrorLength = 500;

    public const int MaxWorkerLength = 120;

    /// <summary>Mirrors <c>CorrelationId.MaxLength</c>; stated here so the column has a fixed size.</summary>
    public const int MaxCorrelationLength = 128;

    private OutboxMessage(
        Guid outboxMessageId,
        string messageType,
        string payload,
        string dedupKey,
        string correlationId,
        Guid? cycleId,
        DateTime occurredAtUtc)
    {
        OutboxMessageId = outboxMessageId;
        MessageType = messageType;
        Payload = payload;
        DedupKey = dedupKey;
        CorrelationId = correlationId;
        CycleId = cycleId;
        OccurredAtUtc = occurredAtUtc;
        Status = OutboxStatus.Pending;
        NextAttemptAtUtc = occurredAtUtc;
    }

    private OutboxMessage()
    {
        MessageType = string.Empty;
        Payload = string.Empty;
        DedupKey = string.Empty;
        CorrelationId = string.Empty;
    }

    public Guid OutboxMessageId { get; private set; }

    /// <summary>A versioned name, so a handler can refuse a shape it does not understand.</summary>
    public string MessageType { get; private set; }

    public string Payload { get; private set; }

    /// <summary>
    /// What makes this message this message. Unique, so enqueuing the same fact twice queues it once.
    /// </summary>
    public string DedupKey { get; private set; }

    public string CorrelationId { get; private set; }

    public Guid? CycleId { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When it becomes eligible again. The backoff lives here rather than in a sleep.</summary>
    public DateTime NextAttemptAtUtc { get; private set; }

    public DateTime? DispatchedAtUtc { get; private set; }

    /// <summary>The last failure, as a type name. Never a message: these rows are permanent.</summary>
    public string? LastError { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAtUtc { get; private set; }

    public bool IsPending => Status == OutboxStatus.Pending;

    public static OutboxMessage Create(
        string messageType,
        string payload,
        string dedupKey,
        string correlationId,
        DateTime nowUtc,
        Guid? cycleId = null)
    {
        EnsureUtc(nowUtc);

        return new OutboxMessage(
            Guid.NewGuid(),
            Text(messageType, nameof(messageType), MaxMessageTypeLength,
                "A queued message must be typed, so a handler can refuse a shape it does not know."),
            payload ?? string.Empty,
            Text(dedupKey, nameof(dedupKey), MaxDedupKeyLength,
                "A queued message must carry a deduplication key. Without one, a retry of the step " +
                "that queued it queues it again and the recipient hears the same thing twice."),
            Text(correlationId, nameof(correlationId), MaxCorrelationLength,
                "A queued message must carry the correlation of the work that produced it."),
            cycleId,
            nowUtc);
    }

    /// <summary>Claims the message for one dispatcher for a bounded period.</summary>
    public bool TryLease(string worker, DateTime nowUtc, TimeSpan leaseFor)
    {
        EnsureUtc(nowUtc);

        var owner = Text(worker, nameof(worker), MaxWorkerLength, "A lease must name its worker.");

        if (leaseFor <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(leaseFor),
                "A lease must expire, or a dispatcher that dies takes the message with it.");
        }

        if (Status != OutboxStatus.Pending || NextAttemptAtUtc > nowUtc)
        {
            return false;
        }

        if (LeaseOwner is not null &&
            !string.Equals(LeaseOwner, owner, StringComparison.Ordinal) &&
            LeaseExpiresAtUtc is not null &&
            LeaseExpiresAtUtc > nowUtc)
        {
            return false;
        }

        LeaseOwner = owner;
        LeaseExpiresAtUtc = nowUtc.Add(leaseFor);

        return true;
    }

    /// <summary>Records a successful delivery. Idempotent: dispatching twice is not an error.</summary>
    public void MarkDispatched(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);

        if (Status == OutboxStatus.Dispatched)
        {
            // A dispatcher that delivered and then crashed before committing will deliver again on
            // recovery. Treating the second as an error would turn an at-least-once queue into a
            // dead one; the handler's own idempotency is what makes the repeat harmless.
            return;
        }

        Status = OutboxStatus.Dispatched;
        DispatchedAtUtc = nowUtc;
        LastError = null;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one, or abandons the message.
    /// </summary>
    /// <returns>True when the message was abandoned and somebody needs to be told.</returns>
    public bool MarkFailed(string error, DateTime nowUtc, TimeSpan baseDelay, int maxAttempts)
    {
        EnsureUtc(nowUtc);

        if (maxAttempts < 1)
        {
            throw new DomainValidationException(
                nameof(maxAttempts),
                "A message must be attempted at least once.");
        }

        Attempts++;
        LastError = Text(error, nameof(error), MaxErrorLength, "A failure must be described.");
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;

        if (Attempts >= maxAttempts)
        {
            Status = OutboxStatus.Abandoned;

            return true;
        }

        // Exponential, capped, and computed rather than slept. A dispatcher holding a thread open
        // during a backoff is a dispatcher that stops when the process restarts.
        var multiplier = Math.Min(1 << Math.Min(Attempts - 1, 10), 1024);

        NextAttemptAtUtc = nowUtc.Add(baseDelay * multiplier);

        return false;
    }

    public override string ToString() =>
        $"outbox {OutboxMessageId} [{MessageType}] {Status} attempts={Attempts}";

    /// <summary>
    /// The same UTC rule the domain enforces, restated here.
    /// </summary>
    /// <remarks>
    /// <c>DateRange.EnsureUtc</c> is internal to the domain assembly, and reaching for it from
    /// infrastructure would mean widening its visibility for the sake of one delivery mechanism. The
    /// rule matters here for the same reason it matters there: a lease and a backoff are arithmetic
    /// on a timestamp, and a local one shifts them by an offset that changes twice a year.
    /// </remarks>
    private static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A timestamp must be UTC (DateTimeKind.Utc). Received Kind={value.Kind}.");
        }
    }

    private static string Text(string? value, string parameterName, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, message);
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
