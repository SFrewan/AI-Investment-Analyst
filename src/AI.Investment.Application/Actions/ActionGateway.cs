using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Actions;

/// <summary>
/// Orchestrates the Action/Policy seam: propose, decide, record, and only then act.
/// </summary>
/// <remarks>
/// <para>
/// This class is short on purpose. It contains no business rules of its own - every judgement
/// belongs to <see cref="IPolicyEngine"/>, which is pure and exhaustively testable. What lives
/// here is sequencing and I/O, so that the part that decides and the part that performs are
/// separately verifiable.
/// </para>
/// <para><strong>The invariants this class must never lose:</strong></para>
/// <list type="number">
/// <item>The effect delegate is invoked on exactly one code path - inside the
/// <see cref="PolicyOutcome.Execute"/> branch, after an authorisation window has been opened.
/// Nowhere else.</item>
/// <item>An audit record is written for every outcome, including denials, and the decision is
/// recorded BEFORE the effect runs. If the process dies mid-effect, the trail still shows what
/// was authorised.</item>
/// <item>A failing effect is recorded and then rethrown. It is never swallowed: a caller must
/// not be able to mistake a failed write for a policy denial.</item>
/// </list>
/// </remarks>
public sealed class ActionGateway : IActionGateway
{
    private readonly IPolicyEngine _policyEngine;
    private readonly IPolicyContextProvider _policyContextProvider;
    private readonly IAuditSink _auditSink;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IActionExecutionStore _executionStore;
    private readonly IWriteAuthorization _writeAuthorization;
    private readonly IClock _clock;

    public ActionGateway(
        IPolicyEngine policyEngine,
        IPolicyContextProvider policyContextProvider,
        IAuditSink auditSink,
        IIdempotencyStore idempotencyStore,
        IActionExecutionStore executionStore,
        IWriteAuthorization writeAuthorization,
        IClock clock)
    {
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        _policyContextProvider = policyContextProvider ?? throw new ArgumentNullException(nameof(policyContextProvider));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
        _writeAuthorization = writeAuthorization ?? throw new ArgumentNullException(nameof(writeAuthorization));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ActionOutcome<TResult>> DispatchAsync<TResult>(
        ActionProposal proposal,
        Func<CancellationToken, Task<TResult>> effect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(effect);

        // 1. Gather everything policy is allowed to consider. The provider is responsible for
        //    failing closed if it cannot determine the context.
        var context = await _policyContextProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        // 2. Decide. Pure, deterministic, no I/O.
        var decision = _policyEngine.Evaluate(proposal, context, _clock.UtcNow);

        // 3. Record the decision BEFORE anything happens. If the process dies during the
        //    effect, the trail still shows what was authorised and why.
        await _auditSink
            .RecordAsync(AuditRecord.ForPolicyDecision(proposal, decision, _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        switch (decision.Outcome)
        {
            case PolicyOutcome.Deny:
                return ActionOutcome<TResult>.Denied(decision);

            case PolicyOutcome.RequireApproval:
                // Phase 1 stops here. The approval workflow - request, token, human decision,
                // single-use consumption - is Phase 5 work. What matters now, and is tested, is
                // that the effect is not invoked.
                return ActionOutcome<TResult>.ApprovalRequired(decision);

            case PolicyOutcome.Execute:
                return await ExecuteAsync(proposal, decision, effect, cancellationToken).ConfigureAwait(false);

            default:
                // Unreachable: PolicyOutcome has exactly three members and the engine is total.
                // Present so that adding a fourth outcome without updating this switch denies
                // rather than falling through to execution.
                return ActionOutcome<TResult>.Denied(decision);
        }
    }

    private async Task<ActionOutcome<TResult>> ExecuteAsync<TResult>(
        ActionProposal proposal,
        PolicyDecision decision,
        Func<CancellationToken, Task<TResult>> effect,
        CancellationToken cancellationToken)
    {
        // 4. Claim the idempotency key. A retry of an already-performed action stops here.
        var claimed = await _idempotencyStore
            .TryClaimAsync(proposal.IdempotencyKey, proposal.ProposalId, _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            await _auditSink
                .RecordAsync(
                    AuditRecord.ForDuplicateSuppressed(proposal, decision, _clock.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);

            return ActionOutcome<TResult>.DuplicateSuppressed(decision);
        }

        // 5. ActionExecution.Start re-checks that this decision authorises this proposal.
        //    Belt and braces: the switch above already established it, and this establishes it
        //    again at the point of use.
        var execution = ActionExecution.Start(proposal, decision, _clock.UtcNow);

        // 6. Open the authorisation window. The persistence layer refuses to commit anything
        //    while this is closed, which is what stops a write path that forgot the gateway.
        using (_writeAuthorization.Authorize(decision))
        {
            try
            {
                var result = await effect(cancellationToken).ConfigureAwait(false);

                execution.MarkSucceeded(_clock.UtcNow);

                await _executionStore.RecordAsync(execution, cancellationToken).ConfigureAwait(false);

                await _auditSink
                    .RecordAsync(
                        AuditRecord.ForExecution(proposal, decision, execution, _clock.UtcNow),
                        cancellationToken)
                    .ConfigureAwait(false);

                return ActionOutcome<TResult>.Executed(decision, result, execution);
            }
#pragma warning disable CA1031 // Deliberate: any failure of the effect must be recorded in the
                              // audit trail before it propagates. The exception is rethrown
                              // immediately - it is observed, never swallowed.
            catch (Exception ex)
            {
                execution.MarkFailed(DescribeFailure(ex), _clock.UtcNow);

                await _executionStore.RecordAsync(execution, CancellationToken.None).ConfigureAwait(false);

                await _auditSink
                    .RecordAsync(
                        AuditRecord.ForExecution(proposal, decision, execution, _clock.UtcNow),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                throw;
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Produces a failure description safe to store permanently.
    /// </summary>
    /// <remarks>
    /// Type name only, never the message and never the stack trace. Audit rows are append-only
    /// and cannot be redacted afterwards, and an exception message is exactly the kind of string
    /// that ends up containing a connection string or a token.
    /// </remarks>
    private static string DescribeFailure(Exception exception) =>
        $"The effect threw {exception.GetType().FullName}. See the correlated log entry for detail.";
}
