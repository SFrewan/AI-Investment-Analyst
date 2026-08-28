using AI.Investment.Domain.Exceptions;
using AI.Investment.Infrastructure.Persistence;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The queue's own rules: leased once, retried with backoff, abandoned loudly, never lost.
/// </summary>
/// <remarks>
/// These are safety tests rather than plumbing tests. The outbox is what stops a database commit and
/// an external effect disagreeing about whether something happened, and every property below is one
/// of the ways that guarantee is normally lost.
/// </remarks>
public sealed class OutboxMessageTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static OutboxMessage Message() =>
        OutboxMessage.Create(
            "operations.escalation-raised@1",
            "{\"escalation.id\":\"x\"}",
            "escalation:" + Guid.NewGuid().ToString("n"),
            Guid.NewGuid().ToString("n"),
            Now);

    [Fact]
    public void A_new_message_is_pending_and_due_immediately()
    {
        var message = Message();

        Assert.True(message.IsPending);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(Now, message.NextAttemptAtUtc);
        Assert.Null(message.DispatchedAtUtc);
    }

    [Fact]
    public void A_message_must_be_typed_keyed_and_correlated()
    {
        Assert.Throws<DomainValidationException>(() =>
            OutboxMessage.Create("  ", "{}", "key", "corr", Now));

        var error = Assert.Throws<DomainValidationException>(() =>
            OutboxMessage.Create("type", "{}", "  ", "corr", Now));

        Assert.Contains("same thing twice", error.Message, StringComparison.Ordinal);

        Assert.Throws<DomainValidationException>(() =>
            OutboxMessage.Create("type", "{}", "key", "  ", Now));
    }

    [Fact]
    public void A_lease_keeps_a_second_dispatcher_out_until_it_expires()
    {
        var message = Message();

        Assert.True(message.TryLease("dispatcher-a", Now, TimeSpan.FromMinutes(2)));
        Assert.False(message.TryLease("dispatcher-b", Now.AddMinutes(1), TimeSpan.FromMinutes(2)));
        Assert.True(message.TryLease("dispatcher-b", Now.AddMinutes(3), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void A_message_that_is_not_yet_due_cannot_be_leased()
    {
        var message = Message();

        message.MarkFailed("System.TimeoutException", Now, TimeSpan.FromSeconds(30), maxAttempts: 5);

        Assert.False(message.TryLease("dispatcher", Now, TimeSpan.FromMinutes(2)));
        Assert.True(message.TryLease("dispatcher", message.NextAttemptAtUtc, TimeSpan.FromMinutes(2)));
    }

    /// <summary>
    /// A dispatcher that delivered and then crashed before committing will deliver again. Treating
    /// the second as an error would turn an at-least-once queue into a dead one.
    /// </summary>
    [Fact]
    public void Dispatching_twice_is_not_an_error()
    {
        var message = Message();

        message.MarkDispatched(Now);
        var first = message.DispatchedAtUtc;

        message.MarkDispatched(Now.AddMinutes(1));

        Assert.Equal(first, message.DispatchedAtUtc);
        Assert.Equal(OutboxStatus.Dispatched, message.Status);
    }

    /// <summary>
    /// The backoff is computed and stored rather than slept, so a dispatcher that restarts does not
    /// forget it - and so a failing handler does not hold a thread.
    /// </summary>
    [Fact]
    public void Retries_back_off_exponentially()
    {
        var message = Message();
        var baseDelay = TimeSpan.FromSeconds(30);

        Assert.False(message.MarkFailed("System.TimeoutException", Now, baseDelay, maxAttempts: 5));
        Assert.Equal(Now.AddSeconds(30), message.NextAttemptAtUtc);

        Assert.False(message.MarkFailed("System.TimeoutException", Now, baseDelay, maxAttempts: 5));
        Assert.Equal(Now.AddSeconds(60), message.NextAttemptAtUtc);

        Assert.False(message.MarkFailed("System.TimeoutException", Now, baseDelay, maxAttempts: 5));
        Assert.Equal(Now.AddSeconds(120), message.NextAttemptAtUtc);

        Assert.Equal(3, message.Attempts);
        Assert.True(message.IsPending);
    }

    /// <summary>
    /// Abandoning is the one outcome that breaks the queue's promise, so it reports itself and the
    /// dispatcher raises an escalation on the strength of that return value.
    /// </summary>
    [Fact]
    public void A_message_out_of_attempts_is_abandoned_and_says_so()
    {
        var message = Message();

        Assert.False(message.MarkFailed("System.TimeoutException", Now, TimeSpan.FromSeconds(1), 2));
        Assert.True(message.MarkFailed("System.TimeoutException", Now, TimeSpan.FromSeconds(1), 2));

        Assert.Equal(OutboxStatus.Abandoned, message.Status);
        Assert.False(message.IsPending);
        Assert.False(message.TryLease("dispatcher", Now.AddHours(1), TimeSpan.FromMinutes(2)));
    }

    /// <summary>
    /// The failure is stored as a type name. These rows are permanent and cannot be redacted, and an
    /// exception message is exactly the kind of string that ends up containing a connection string.
    /// </summary>
    [Fact]
    public void A_failure_is_recorded_and_a_message_is_required()
    {
        var message = Message();

        message.MarkFailed("System.Net.Http.HttpRequestException", Now, TimeSpan.FromSeconds(1), 5);

        Assert.Equal("System.Net.Http.HttpRequestException", message.LastError);

        Assert.Throws<DomainValidationException>(() =>
            message.MarkFailed("  ", Now, TimeSpan.FromSeconds(1), 5));
    }

    [Fact]
    public void Every_transition_requires_a_utc_instant()
    {
        var message = Message();
        var local = DateTime.SpecifyKind(Now, DateTimeKind.Local);

        Assert.Throws<DomainValidationException>(() => message.MarkDispatched(local));
        Assert.Throws<DomainValidationException>(() => message.TryLease("d", local, TimeSpan.FromMinutes(1)));
        Assert.Throws<DomainValidationException>(() => message.MarkFailed("e", local, TimeSpan.FromSeconds(1), 3));
    }

    [Fact]
    public void A_lease_must_expire_and_a_message_must_be_attempted_at_least_once()
    {
        var message = Message();

        Assert.Throws<DomainValidationException>(() => message.TryLease("d", Now, TimeSpan.Zero));
        Assert.Throws<DomainValidationException>(() =>
            message.MarkFailed("e", Now, TimeSpan.FromSeconds(1), maxAttempts: 0));
    }
}
