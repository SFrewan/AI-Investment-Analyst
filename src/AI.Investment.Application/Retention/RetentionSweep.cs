using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Retention;

/// <summary>
/// Walks the archive, asking each payload's licence whether it may still be kept.
/// </summary>
/// <remarks>
/// <para>
/// The recurring half of retention. <see cref="IRetentionEnforcer"/> decides about one payload and
/// knows nothing of scheduling; this decides when to go looking and knows nothing of licensing.
/// Neither can do the other's job, which is what lets the rule that destroys evidence be tested
/// exhaustively without a clock.
/// </para>
/// <para>
/// <strong>Bounded, and honest about it.</strong> A sweep takes a limit and reports whether it
/// reached the end of the archive. Silently stopping at a cap would make "nothing left to delete"
/// indistinguishable from "we stopped looking", and only the first is a compliance statement.
/// </para>
/// <para>
/// <strong>One payload's failure does not end the sweep, and is not disguised as a decision.</strong>
/// An archive is exactly where a single unreadable entry is likely, and a poisoned one that killed
/// every sweep would block the obligation permanently. But counting a thrown exception as a policy
/// refusal would be worse: a database outage would report as five thousand payloads that policy
/// declined to delete, which is a sentence about compliance that nothing observed. Failures are
/// counted separately, and <see cref="RetentionSweepSummary.Failed"/> climbing is an infrastructure
/// alarm rather than a policy one.
/// </para>
/// </remarks>
public sealed class RetentionSweep : IRetentionSweep
{
    /// <summary>The most payloads one sweep will consider, whatever it is asked for.</summary>
    /// <remarks>
    /// A ceiling on a caller's optimism. Each payload costs an archive read, a registry lookup, a
    /// reference check and possibly a seam dispatch, so an unbounded sweep would hold a scheduler
    /// slot indefinitely on a large archive. Reaching it is reported as "not complete", so the next
    /// sweep continues rather than the work being lost.
    /// </remarks>
    public const int MaxLimit = 5000;

    private readonly IRawResponseArchive _archive;
    private readonly IRetentionEnforcer _enforcer;

    public RetentionSweep(IRawResponseArchive archive, IRetentionEnforcer enforcer)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _enforcer = enforcer ?? throw new ArgumentNullException(nameof(enforcer));
    }

    public async Task<RetentionSweepSummary> SweepAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            // Nothing was examined, so the archive was not reached. Reporting completion here would
            // let a misconfigured limit of zero read as "the archive is clean".
            return new RetentionSweepSummary(0, 0, 0, 0, 0, Reached: false);
        }

        var effectiveLimit = Math.Min(limit, MaxLimit);

        var examined = 0;
        var retained = 0;
        var deleted = 0;
        var refused = 0;
        var failed = 0;
        var reachedEnd = true;

        await foreach (var hash in _archive.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (examined == effectiveLimit)
            {
                // Stopped by the limit, not by the end of the archive. The caller needs to know
                // which, so it sweeps again rather than concluding there is nothing left.
                reachedEnd = false;

                break;
            }

            examined++;

            var action = await EnforceAsync(hash, cancellationToken).ConfigureAwait(false);

            switch (action)
            {
                case RetentionAction.Deleted:
                    deleted++;

                    break;

                case RetentionAction.DeletionRefused:
                    refused++;

                    break;

                case RetentionAction.NothingRequired:
                    retained++;

                    break;

                // Unknown, which the enforcer never returns, and anything a later build adds.
                // Counted as outstanding rather than retained: an action this build cannot
                // interpret has not been shown to have discharged an obligation, and the reading
                // that overstates what a sweep accomplished is the dangerous one.
                default:
                    failed++;

                    break;
            }
        }

        return new RetentionSweepSummary(examined, retained, deleted, refused, failed, reachedEnd);
    }

    private async Task<RetentionAction> EnforceAsync(
        ContentHash hash,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _enforcer.EnforceAsync(hash, cancellationToken).ConfigureAwait(false);

            return result.Action;
        }
        catch (OperationCanceledException)
        {
            // The caller stopping the sweep, not a payload that failed. Swallowing it would turn a
            // shutdown into a report claiming every remaining payload had failed.
            throw;
        }
        catch (Exception)
        {
            // Deliberately broad - see the remarks on this class. A single poisoned payload must
            // not block the obligation permanently, and the failure is counted rather than lost.
            return RetentionAction.Unknown;
        }
    }
}
