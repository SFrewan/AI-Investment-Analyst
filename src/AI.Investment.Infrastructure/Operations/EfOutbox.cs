using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Operations;

/// <summary>Queues messages in the same context - and therefore the same transaction - as the change.</summary>
/// <remarks>
/// <para>
/// Staged rather than saved. That is the entire point of the pattern: the message becomes real when
/// the change that caused it becomes real, and neither can exist without the other. A store that
/// committed the message on its own would be a store that could announce something that never
/// happened.
/// </para>
/// <para>
/// A duplicate deduplication key is answered false rather than thrown, because the caller's correct
/// response is to carry on. Queuing the same fact twice is what a retry does, and a retry is the
/// normal case in an unattended system.
/// </para>
/// </remarks>
public sealed class EfOutbox : IOutbox
{
    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;

    public EfOutbox(AppDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<bool> EnqueueAsync(
        OutboxEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (_dbContext.OutboxMessages.Local.Any(tracked =>
                string.Equals(tracked.DedupKey, envelope.DedupKey, StringComparison.Ordinal)))
        {
            return false;
        }

        var existing = await _dbContext.OutboxMessages
            .AsNoTracking()
            .AnyAsync(m => m.DedupKey == envelope.DedupKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing)
        {
            return false;
        }

        var message = OutboxMessage.Create(
            envelope.MessageType,
            envelope.Payload,
            envelope.DedupKey,
            envelope.CorrelationId,
            _clock.UtcNow,
            envelope.CycleId);

        await _dbContext.OutboxMessages.AddAsync(message, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
