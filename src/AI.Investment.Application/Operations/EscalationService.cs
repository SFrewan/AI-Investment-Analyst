using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Operations;

namespace AI.Investment.Application.Operations;

/// <summary>Raises escalations, records them, and queues the message that tells somebody.</summary>
/// <remarks>
/// <para>
/// Three writes, in one transaction, on purpose. The escalation row is the durable question, the
/// audit entry is the permanent record that it was asked, and the queued message is the delivery.
/// Splitting them would let the platform decide to escalate and then fail to tell anyone, which is
/// indistinguishable from not escalating.
/// </para>
/// <para>
/// Note what this class does not do: it does not decide <em>whether</em> to escalate. That is
/// <see cref="EscalationPolicy"/>, which is pure and exhaustively tested. Deciding and notifying are
/// separated so the decision can be enumerated without a database.
/// </para>
/// </remarks>
public sealed class EscalationService
{
    private readonly IEscalationStore _escalations;
    private readonly IOutbox _outbox;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public EscalationService(
        IEscalationStore escalations,
        IOutbox outbox,
        IAuditSink audit,
        ICorrelationContext correlation,
        IClock clock)
    {
        _escalations = escalations ?? throw new ArgumentNullException(nameof(escalations));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>How long an unanswered escalation stays a live question.</summary>
    /// <remarks>
    /// An answer given long after the question was asked is an answer to a different question: the
    /// market context the human was judging has moved. Past this, the escalation counts as unhandled
    /// and the unattended-operation criterion fails - which is the intended outcome, because nobody
    /// answering is a real result rather than a quiet one.
    /// </remarks>
    public static TimeSpan DefaultValidity { get; } = TimeSpan.FromHours(24);

    public async Task<Escalation> RaiseAsync(
        Capability capability,
        EscalationReason reason,
        string explanation,
        Guid? cycleId = null,
        Guid? proposalId = null,
        TimeSpan? validFor = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var escalation = Escalation.Raise(
            capability,
            reason,
            explanation,
            now,
            validFor ?? DefaultValidity,
            cycleId,
            proposalId);

        await _escalations.AddAsync(escalation, cancellationToken).ConfigureAwait(false);

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["escalation.id"] = escalation.EscalationId.ToString("d", System.Globalization.CultureInfo.InvariantCulture),
            ["escalation.reason"] = reason.ToString(),
            ["escalation.capability"] = capability.ToString(),
            ["escalation.expiresAtUtc"] = OperationsMessages.Instant(escalation.ExpiresAtUtc),
            ["escalation.explanation"] = escalation.Explanation,
        };

        await _outbox.EnqueueAsync(
            new OutboxEnvelope(
                OperationsMessages.EscalationRaised,
                OperationsMessages.Payload(details),
                // One message per escalation. The escalation identifier is already unique, so a
                // retry of this method after a partial failure re-queues nothing.
                $"escalation:{escalation.EscalationId:d}",
                _correlation.Current.Value,
                cycleId),
            cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                AuditEventType.EscalationRaised,
                "operations.escalation",
                $"Escalated {reason} for {capability}: {escalation.Explanation}",
                now,
                cycleId,
                capability,
                details),
            cancellationToken).ConfigureAwait(false);

        await _escalations.SaveAsync(cancellationToken).ConfigureAwait(false);

        return escalation;
    }
}
