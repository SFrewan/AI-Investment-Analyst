using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operations;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Infrastructure.Operations;

/// <summary>
/// Delivers an operations message by writing the receipt into the append-only audit trail.
/// </summary>
/// <remarks>
/// <para>
/// The destination this phase has. There is no email, no pager and no chat integration here, and
/// inventing one would be building the notification plane on the way past rather than deliberately.
/// What exists is the trail and the escalation queue the API exposes, and both are real, durable and
/// queryable - which is more than most first notification integrations manage.
/// </para>
/// <para>
/// The receipt is a separate entry from the one written when the thing happened, deliberately.
/// "We decided to escalate" and "the escalation was delivered" are different facts, and the gap
/// between them is exactly what an outbox exists to make visible.
/// </para>
/// <para>
/// Idempotent, because the queue delivers at least once: the audit sink is append-only, so a repeat
/// writes a second receipt rather than corrupting the first, and the message identifier in the
/// details makes the pair recognisable as one delivery retried.
/// </para>
/// </remarks>
public sealed class AuditNotificationHandler : IOutboxHandler
{
    private readonly IAuditSink _audit;
    private readonly IClock _clock;

    public AuditNotificationHandler(string messageType, IAuditSink audit, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);

        MessageType = messageType;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string MessageType { get; }

    public async Task HandleAsync(
        DeliveredMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["outbox.messageId"] = message.OutboxMessageId.ToString("d", System.Globalization.CultureInfo.InvariantCulture),
            ["outbox.messageType"] = message.MessageType,
            ["outbox.attempts"] = OperationsMessages.Number(message.Attempts),
            ["outbox.occurredAtUtc"] = OperationsMessages.Instant(message.OccurredAtUtc),
        };

        foreach (var entry in OperationsMessages.Read(message.Payload))
        {
            details[entry.Key] = entry.Value;
        }

        await _audit.RecordAsync(
            AuditRecord.ForOperation(
                CorrelationId.Create(message.CorrelationId),
                AuditEventType.OutboxDispatched,
                "operations.outbox",
                $"Delivered {message.MessageType} queued at {message.OccurredAtUtc:O}.",
                _clock.UtcNow,
                message.CycleId,
                capability: null,
                details),
            cancellationToken).ConfigureAwait(false);
    }
}
