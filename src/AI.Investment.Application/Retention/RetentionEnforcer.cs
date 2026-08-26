using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Retention;

/// <summary>
/// Applies a source's licensed retention obligation to one archived payload.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deletion goes through the safety seam.</strong> Destroying evidence is a side effect,
/// and the most consequential one this platform performs routinely, so it is proposed under
/// <see cref="Capability.DataRetention"/> and evaluated by the same policy engine as everything
/// else. Three things follow for free: the kill switch stops retention deletion; an installation
/// that has not deliberately enabled the capability deletes nothing, because a capability with no
/// configured policy is denied; and every deletion is audited with the rule and reason that
/// required it.
/// </para>
/// <para>
/// <strong>Marking precedes deletion.</strong> When referenced evidence must go, the unreplayable
/// marker is written first. A crash between the two steps then leaves a marker for a payload that
/// still exists - conservative, visible, and self-correcting on the next pass. The other order
/// would leave a deleted payload with nothing recording why, which is the silent gap the whole
/// mechanism exists to prevent.
/// </para>
/// <para>
/// <strong>Retain decisions are not written to the audit trail.</strong> A retain is the absence of
/// an action, and a sweep over a large archive would otherwise produce one audit row per payload
/// per pass, burying the deletions that matter under millions that do not. The decision remains
/// auditable - it is pure, deterministic and reproducible from the source's terms and the
/// payload's age - which is what makes re-deriving it cheaper and more trustworthy than storing it.
/// </para>
/// </remarks>
public sealed class RetentionEnforcer : IRetentionEnforcer
{
    /// <summary>Reported when the archive holds nothing under the hash.</summary>
    public const string NothingArchivedRule = "retention.nothing-archived@1";

    /// <summary>Reported when the payload's source is not in the registry.</summary>
    public const string UnknownSourceRule = "retention.source-unknown@1";

    private static readonly ActionType DeleteActionType = ActionType.Create("retention.delete-payload");
    private static readonly ProposedBy Proposer = ProposedBy.Service("retention-enforcer", "1.0");

    private readonly ISourceRegistry _sources;
    private readonly IRawResponseArchive _archive;
    private readonly IPayloadReferenceIndex _references;
    private readonly IUnreplayableEvidenceStore _markers;
    private readonly IActionGateway _actionGateway;
    private readonly IClock _clock;

    public RetentionEnforcer(
        ISourceRegistry sources,
        IRawResponseArchive archive,
        IPayloadReferenceIndex references,
        IUnreplayableEvidenceStore markers,
        IActionGateway actionGateway,
        IClock clock)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        _actionGateway = actionGateway ?? throw new ArgumentNullException(nameof(actionGateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<RetentionEnforcementResult> EnforceAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        var payload = await _archive.DescribeAsync(hash, cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            // Nothing held, nothing to decide. Not an error: a sweep racing a previous pass, or a
            // payload already removed, should converge rather than throw.
            return RetentionEnforcementResult.Retained(new RetentionDecision(
                RetentionOutcome.Retain,
                NothingArchivedRule,
                $"No payload is archived under {hash.Abbreviated}."));
        }

        var source = await _sources.GetByIdAsync(payload.SourceId, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            // The obligation lives in the source's terms. With no source there are no terms to
            // read, and an unknown obligation is not a licence to delete - it is a reason not to.
            return RetentionEnforcementResult.Retained(new RetentionDecision(
                RetentionOutcome.Retain,
                UnknownSourceRule,
                $"Source '{payload.SourceId}' is not registered, so its retention terms cannot be " +
                "read. An obligation that cannot be established never compels deletion."));
        }

        var isReferenced = await _references.IsReferencedAsync(hash, cancellationToken).ConfigureAwait(false);

        var decision = RetentionPolicy.Evaluate(
            source.Licensing,
            payload.RetrievedAtUtc,
            _clock.UtcNow,
            isReferenced);

        if (!decision.RequiresDeletion)
        {
            return RetentionEnforcementResult.Retained(decision);
        }

        var deleted = await DeleteAsync(hash, payload, decision, cancellationToken).ConfigureAwait(false);

        // Reported rather than assumed. The deletion declares itself irreversible, so an
        // installation that has not granted automatic execution for Capability.DataRetention gets
        // an approval requirement here every time - and a caller told only that deletion was
        // *required* would record a compliance obligation as discharged when the payload is still
        // on disk.
        return new RetentionEnforcementResult(
            decision,
            deleted ? RetentionAction.Deleted : RetentionAction.DeletionRefused);
    }

    /// <summary>Proposes the deletion and reports whether the seam let it happen.</summary>
    private async Task<bool> DeleteAsync(
        ContentHash hash,
        ArchivedPayload payload,
        RetentionDecision decision,
        CancellationToken cancellationToken)
    {
        var proposal = ActionProposal.Create(
            CorrelationId.New(),
            Capability.DataRetention,
            DeleteActionType,
            ActionTarget.Create("ArchivedPayload", hash.Value),
            new RetentionParameters(hash, payload.SourceId, decision),

            // No money changes hands, but the action destroys evidence and cannot be undone.
            // Declaring that truthfully means policy.irreversible-requires-approval@1 applies, so
            // an installation gets human approval on every retention deletion unless it has
            // deliberately granted AllowIrreversibleAutoExecute for this capability. That is the
            // right default for the one operation here that cannot be taken back.
            ActionEconomics.Create(Money.ZeroUsd, Money.ZeroUsd, ReversibilityClass.Irreversible),
            Proposer,

            // One deletion per payload, however many times a sweep revisits it.
            $"retention.delete:{hash.Value}",
            _clock.UtcNow);

        var outcome = await _actionGateway.DispatchAsync(
            proposal,
            async token =>
            {
                if (decision.RequiresEvidenceMarking)
                {
                    var marker = UnreplayableEvidence.Mark(
                        hash,
                        payload.SourceId,
                        decision,
                        _clock.UtcNow);

                    await _markers.RecordAsync(marker, token).ConfigureAwait(false);
                }

                await _archive.DeleteAsync(hash, token).ConfigureAwait(false);

                return decision.RuleId;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.WasExecuted;
    }
}
