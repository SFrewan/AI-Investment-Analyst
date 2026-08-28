using System.Globalization;
using AI.Investment.Infrastructure.Persistence;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// Runs the queue for a simulated fortnight, through an outage, a redelivery and a crash.
/// </summary>
/// <remarks>
/// <para>
/// The unattended-operation criterion says nothing the platform meant to say goes unsaid. That
/// promise is made by five behaviours acting together - deduplication on enqueue, a lease that
/// expires, a backoff that grows, a bounded number of attempts, and a handler that tolerates being
/// called twice - and each of them is individually correct in <see cref="OutboxMessageTests"/>.
/// What is measured here is the thing none of those tests can see: that the five compose over a
/// long period, across the events that actually happen to a queue.
/// </para>
/// <para>
/// Virtual minutes rather than half-hours, because the backoff this exercises is measured in
/// minutes and a coarser tick would step over it. Nothing sleeps and nothing is timed: the clock is
/// a variable, so the run is the same every time.
/// </para>
/// <para>
/// As everywhere else in Phase 6, this is a deterministic exercise of the controls rather than two
/// weeks of real operation, and it is recorded as such.
/// </para>
/// </remarks>
public sealed class OutboxFortnightTests
{
    private static readonly DateTime Start = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Fortnight = TimeSpan.FromDays(14);

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private const int MaxAttempts = 12;

    /// <summary>A provider outage on day three. Three hours, which the backoff has to ride out.</summary>
    private static readonly DateTime OutageFrom = Start.AddDays(3);

    private static readonly DateTime OutageUntil = OutageFrom.AddHours(3);

    [Fact]
    public void A_simulated_fortnight_delivers_every_message_exactly_once_and_abandons_none()
    {
        var run = Run(handlerRecoversAfterOutage: true);

        // Nothing was lost.
        Assert.Equal(run.Enqueued, run.Dispatched);
        Assert.Equal(0, run.Abandoned);
        Assert.All(run.Messages, message => Assert.Equal(OutboxStatus.Dispatched, message.Status));

        // Nothing was applied twice, even though several messages were delivered more than once.
        // That gap is the at-least-once guarantee doing its job and the handler's idempotency
        // absorbing the cost of it.
        Assert.Equal(run.Enqueued, run.Applied);
        Assert.True(
            run.DuplicateApplicationsSuppressed > 0,
            "no message was ever redelivered to a handler that had already applied it, so the " +
            "idempotency this queue depends on was never actually exercised");
        Assert.True(run.Deliveries > run.Applied);

        // The outage was ridden out rather than survived by luck: some message needed several
        // attempts, and none came close to exhausting them.
        Assert.True(run.MaxAttempts > 3, $"the busiest message took only {run.MaxAttempts} attempts");
        Assert.True(run.MaxAttempts < MaxAttempts);

        // A dispatcher died holding messages, and they were picked up again once the lease expired
        // rather than being stranded.
        Assert.True(run.LeasesAbandonedByCrash > 0, "no dispatcher ever crashed holding a message");
    }

    /// <summary>
    /// Enqueuing the same fact twice queues it once, for the whole fortnight rather than for a
    /// moment.
    /// </summary>
    [Fact]
    public void A_fact_offered_repeatedly_across_the_fortnight_is_queued_once()
    {
        var run = Run(handlerRecoversAfterOutage: true);

        Assert.True(run.Offered > run.Enqueued);
        Assert.Equal(run.Enqueued, run.Messages.Select(m => m.DedupKey).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The negative twin. A handler that never recovers must abandon loudly, not deliver quietly.
    /// </summary>
    [Fact]
    public void A_handler_that_never_recovers_abandons_its_messages_and_says_so()
    {
        var run = Run(handlerRecoversAfterOutage: false);

        Assert.True(run.Abandoned > 0);
        Assert.Equal(run.Abandoned, run.AbandonmentsReported);

        // Abandoned rather than pretended: an abandoned message is never marked dispatched, and it
        // never reached its handler.
        Assert.All(
            run.Messages.Where(m => m.Status == OutboxStatus.Abandoned),
            message =>
            {
                Assert.Null(message.DispatchedAtUtc);
                Assert.Equal(MaxAttempts, message.Attempts);
                Assert.NotNull(message.LastError);
            });
    }

    private static QueueRun Run(bool handlerRecoversAfterOutage)
    {
        var now = Start;
        var queue = new List<OutboxMessage>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var applied = new HashSet<string>(StringComparer.Ordinal);

        var offered = 0;
        var deliveries = 0;
        var dispatched = 0;
        var duplicateApplications = 0;
        var abandonmentsReported = 0;
        var leasesAbandonedByCrash = 0;

        var ticks = (int)(Fortnight.Ticks / Tick.Ticks);

        for (var tick = 0; tick < ticks; tick++)
        {
            // ---- producers ------------------------------------------------------------------
            // Three facts a day, each offered twice because the step that produces a message is
            // itself retryable and a retried step offers the same fact again.
            if (tick % 480 == 0)
            {
                var key = string.Create(CultureInfo.InvariantCulture, $"cycle-finished:{tick}");

                foreach (var _ in Enumerable.Range(0, 2))
                {
                    offered++;

                    if (keys.Add(key))
                    {
                        queue.Add(OutboxMessage.Create(
                            "operations.cycle-finished@1",
                            "{\"tick\":" + tick.ToString(CultureInfo.InvariantCulture) + "}",
                            key,
                            "corr-" + tick.ToString(CultureInfo.InvariantCulture),
                            now));
                    }
                }
            }

            // ---- one dispatcher pass --------------------------------------------------------
            var worker = tick % 2 == 0 ? "dispatcher-a" : "dispatcher-b";

            foreach (var message in queue.Where(m => m.IsPending).ToList())
            {
                if (!message.TryLease(worker, now, Lease))
                {
                    continue;
                }

                var handlerWorks = handlerRecoversAfterOutage
                    ? now < OutageFrom || now >= OutageUntil
                    : now < OutageFrom;

                if (!handlerWorks)
                {
                    deliveries++;

                    if (message.MarkFailed("ProviderUnavailableException", now, BaseDelay, MaxAttempts))
                    {
                        // The dispatcher's contract: abandonment raises an escalation. Counted at
                        // the moment it is signalled, so the test can insist that every message
                        // that ended up abandoned was announced when it happened.
                        abandonmentsReported++;
                    }

                    continue;
                }

                deliveries++;

                // An idempotent handler. Applying the same fact twice is a no-op, which is what the
                // at-least-once guarantee requires of every handler in the platform.
                if (!applied.Add(message.DedupKey))
                {
                    duplicateApplications++;
                }

                // Every ninety-seventh pass the dispatcher dies in the worst possible place: after
                // the handler has applied the message and before the delivery is recorded. Nothing
                // is written - a process that dies does not get to write - so the message stays
                // pending, the lease runs out, and somebody delivers it again. This is the case the
                // handler's idempotency exists for, and the only one that proves it.
                if (tick % 97 == 0)
                {
                    leasesAbandonedByCrash++;

                    continue;
                }

                message.MarkDispatched(now);
                dispatched++;
            }

            now = now.Add(Tick);
        }

        return new QueueRun(
            queue,
            offered,
            queue.Count,
            deliveries,
            dispatched,
            applied.Count,
            duplicateApplications,
            queue.Count(m => m.Status == OutboxStatus.Abandoned),
            abandonmentsReported,
            leasesAbandonedByCrash,
            queue.Count == 0 ? 0 : queue.Max(m => m.Attempts));
    }

    private sealed record QueueRun(
        IReadOnlyList<OutboxMessage> Messages,
        int Offered,
        int Enqueued,
        int Deliveries,
        int Dispatched,
        int Applied,
        int DuplicateApplicationsSuppressed,
        int Abandoned,
        int AbandonmentsReported,
        int LeasesAbandonedByCrash,
        int MaxAttempts);
}
