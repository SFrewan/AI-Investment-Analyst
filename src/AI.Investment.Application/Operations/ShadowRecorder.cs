using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Shadow;

namespace AI.Investment.Application.Operations;

/// <summary>
/// Records what one autonomy level up would have decided. Measures; never acts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no execution path in this class and there must never be one.</strong> It takes a
/// proposal, a context and the decision that was actually reached, hands them to
/// <see cref="ShadowEvaluation"/>, and writes down the difference. It holds no gateway, no venue and
/// no write authorisation - not because it currently has no use for them, but so that a future edit
/// adding "and if the shadow says execute, then…" would have to add a dependency somebody would see
/// in the constructor. An architecture test asserts the same thing from the outside.
/// </para>
/// <para>
/// A failure to record a measurement must never stop the real work. The measurement is how autonomy
/// gets earned; the real work is what the platform is for, and losing the second to protect the
/// first would be the wrong trade. Failures are counted and reported rather than thrown.
/// </para>
/// </remarks>
public sealed class ShadowRecorder
{
    private readonly IPolicyEngine _engine;
    private readonly IShadowDecisionStore _store;
    private readonly IOutbox _outbox;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public ShadowRecorder(
        IPolicyEngine engine,
        IShadowDecisionStore store,
        IOutbox outbox,
        IAuditSink audit,
        ICorrelationContext correlation,
        IClock clock)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Measures and records. Returns the measurement, or null when there was nothing to measure.
    /// </summary>
    public async Task<ShadowDecision?> RecordAsync(
        ActionProposal proposal,
        PolicyContext context,
        PolicyDecision actual,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actual);

        var now = _clock.UtcNow;

        var measurement = ShadowEvaluation.Evaluate(_engine, proposal, context, actual, now);

        if (measurement is null)
        {
            return null;
        }

        await _store.AddAsync(measurement, cancellationToken).ConfigureAwait(false);

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shadow.id"] = measurement.ShadowDecisionId.ToString("d", System.Globalization.CultureInfo.InvariantCulture),
            ["shadow.proposalId"] = measurement.ProposalId.ToString("d", System.Globalization.CultureInfo.InvariantCulture),
            ["shadow.actualMode"] = measurement.ActualMode.ToString(),
            ["shadow.actualOutcome"] = measurement.ActualOutcome.ToString(),
            ["shadow.shadowMode"] = measurement.ShadowMode.ToString(),
            ["shadow.shadowOutcome"] = measurement.ShadowOutcome.ToString(),
            ["shadow.wouldHaveExecuted"] = measurement.WouldHaveExecuted ? "true" : "false",
            ["shadow.agreed"] = measurement.Agreed ? "true" : "false",
        };

        await _outbox.EnqueueAsync(
            new OutboxEnvelope(
                OperationsMessages.ShadowDecisionRecorded,
                OperationsMessages.Payload(details),
                $"shadow:{measurement.ShadowDecisionId:d}",
                _correlation.Current.Value,
                proposal.CycleId),
            cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            AuditRecord.ForOperation(
                _correlation.Current,
                AuditEventType.ShadowDecisionRecorded,
                "operations.shadow",
                $"At {measurement.ShadowMode} the gate would have answered " +
                $"{measurement.ShadowOutcome}; at {measurement.ActualMode} it answered " +
                $"{measurement.ActualOutcome}. Nothing was executed on the strength of this.",
                now,
                proposal.CycleId,
                proposal.Capability,
                details),
            cancellationToken).ConfigureAwait(false);

        await _store.SaveAsync(cancellationToken).ConfigureAwait(false);

        return measurement;
    }
}
