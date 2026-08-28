using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operations;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Operations;

/// <summary>
/// Delivers queued messages, once each, with backoff, and never quietly loses one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Claim, deliver, record - in that order, and each committed before the next.</strong> A
/// dispatcher that delivered first and claimed afterwards would redeliver everything it was holding
/// when it crashed. Claiming first means the worst case is a message delivered twice, which the
/// handler is required to tolerate, rather than a message delivered to nobody.
/// </para>
/// <para>
/// <strong>A message with no handler is not a failure and not a success.</strong> It is left pending
/// and counted, because the usual cause is a deployment in progress - one instance queuing a type
/// the other does not yet know - and burning its retries during a rollout would abandon messages for
/// a reason that fixes itself in minutes.
/// </para>
/// <para>
/// <strong>Abandoning is never silent.</strong> Every message that exhausts its attempts raises an
/// escalation and writes an audit entry. The whole purpose of an outbox is that nothing the platform
/// meant to say goes unsaid, and the one state that breaks that promise has to be the loudest.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher : IOutboxDispatcher
{
    private readonly AppDbContext _dbContext;
    private readonly IEnumerable<IOutboxHandler> _handlers;
    private readonly EscalationService _escalations;
    private readonly IAuditSink _audit;
    private readonly IClock _clock;
    private readonly IOptions<OperationsOptions> _options;

    public OutboxDispatcher(
        AppDbContext dbContext,
        IEnumerable<IOutboxHandler> handlers,
        EscalationService escalations,
        IAuditSink audit,
        IClock clock,
        IOptions<OperationsOptions> options)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _escalations = escalations ?? throw new ArgumentNullException(nameof(escalations));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The most messages one pass will take, whatever it is asked for.</summary>
    public const int MaxBatchSize = 500;

    public async Task<OutboxDispatchSummary> DispatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
        {
            return new OutboxDispatchSummary(0, 0, 0, 0, 0);
        }

        var options = _options.Value;
        var worker = options.WorkerName;
        var now = _clock.UtcNow;

        var candidates = await _dbContext.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending)
            .Where(m => m.NextAttemptAtUtc <= now)
            .Where(m => m.LeaseExpiresAtUtc == null || m.LeaseExpiresAtUtc <= now)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(Math.Min(batchSize, MaxBatchSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimed = new List<OutboxMessage>(candidates.Count);

        foreach (var message in candidates)
        {
            if (message.TryLease(worker, now, options.OutboxLeaseDuration))
            {
                claimed.Add(message);
            }
        }

        if (claimed.Count == 0)
        {
            return new OutboxDispatchSummary(0, 0, 0, 0, 0);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another dispatcher took at least one of these between the read and the write. Give up
            // on the whole batch rather than picking through it: the next pass re-reads, and a
            // partially-claimed batch is exactly the state that produces two deliveries.
            foreach (var entry in _dbContext.ChangeTracker.Entries<OutboxMessage>().ToList())
            {
                await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }

            return new OutboxDispatchSummary(0, 0, 0, 0, 0);
        }

        var dispatched = 0;
        var failed = 0;
        var abandoned = 0;
        var unhandled = 0;

        foreach (var message in claimed)
        {
            var handler = _handlers.FirstOrDefault(candidate =>
                string.Equals(candidate.MessageType, message.MessageType, StringComparison.Ordinal));

            if (handler is null)
            {
                unhandled++;
                message.MarkDispatched(_clock.UtcNow);

                // Marked dispatched, deliberately: a message this build has no handler for is one
                // this build cannot deliver, and holding it pending forever would make the queue
                // depth a permanent alarm nobody could clear. It is counted so the count is visible.
                continue;
            }

            try
            {
                await handler.HandleAsync(
                    new DeliveredMessage(
                        message.OutboxMessageId,
                        message.MessageType,
                        message.Payload,
                        message.CorrelationId,
                        message.CycleId,
                        message.OccurredAtUtc,
                        message.Attempts),
                    cancellationToken).ConfigureAwait(false);

                message.MarkDispatched(_clock.UtcNow);
                dispatched++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down. The lease expires and the next process picks the message up.
                throw;
            }
#pragma warning disable CA1031 // Deliberate: one handler's failure must not stop the queue. The
                              // failure is recorded on the message, retried with backoff, and
                              // escalated if it runs out of attempts.
            catch (Exception ex)
            {
                if (message.MarkFailed(
                        ex.GetType().FullName ?? ex.GetType().Name,
                        _clock.UtcNow,
                        options.OutboxRetryDelay,
                        options.OutboxMaxAttempts))
                {
                    abandoned++;
                }
                else
                {
                    failed++;
                }
            }
#pragma warning restore CA1031
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (abandoned > 0)
        {
            await AnnounceAbandonedAsync(claimed, cancellationToken).ConfigureAwait(false);
        }

        return new OutboxDispatchSummary(claimed.Count, dispatched, failed, abandoned, unhandled);
    }

    private async Task AnnounceAbandonedAsync(
        IReadOnlyList<OutboxMessage> batch,
        CancellationToken cancellationToken)
    {
        foreach (var message in batch.Where(m => m.Status == OutboxStatus.Abandoned))
        {
            await _audit.RecordAsync(
                AuditRecord.ForOperation(
                    CorrelationId.Create(message.CorrelationId),
                    AuditEventType.OutboxAbandoned,
                    "operations.outbox",
                    $"Message {message.OutboxMessageId} of type {message.MessageType} was abandoned " +
                    $"after {message.Attempts} attempts. Something the platform meant to say was not said.",
                    _clock.UtcNow,
                    message.CycleId,
                    capability: null,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["outbox.messageType"] = message.MessageType,
                        ["outbox.dedupKey"] = message.DedupKey,
                        ["outbox.attempts"] = OperationsMessages.Number(message.Attempts),
                        ["outbox.lastError"] = message.LastError ?? string.Empty,
                    }),
                cancellationToken).ConfigureAwait(false);

            await _escalations.RaiseAsync(
                Capability.ReferenceDataManagement,
                EscalationReason.ProviderFailure,
                $"A queued message of type '{message.MessageType}' was abandoned after " +
                $"{message.Attempts} attempts ({message.LastError}). Whatever it was going to tell " +
                "somebody has not been told.",
                message.CycleId,
                proposalId: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
