namespace AI.Investment.Api.Tests;

/// <summary>
/// The cut of the installed SEC EDGAR authorisation into batches. Data only; nothing here runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What a partition is in this repository, and what it is not.</strong> A partition writes
/// down which subjects a batch means, so that naming a batch means the same set every time it is
/// named, and so that an approval approves something legible rather than "whatever is outstanding".
/// It is a table of facts. It holds no provider, opens no scope, resolves no service and charges no
/// authorisation - <see cref="SecEdgarSixMemberPartitionTests"/> reads this file's own source text
/// and fails if any of those appear in it. Cutting the partition is therefore not a step towards
/// dispatching; it is the thing that has to exist before an approval could refer to anything.
/// </para>
/// <para>
/// <strong>Why six batches of one rather than one batch of six.</strong> The split partition learned
/// this twice, at <c>GPC.US</c> and again at <c>MYO.US</c>: authorisation is charged at intent, so a
/// batch that dispatches six and fails on the third has still spent six units, and the ceiling is
/// gone while a member remains unretrieved. Recovering it then needs a successor authorisation - a
/// whole approval cycle - for one request. Cut one member to a batch and the same failure spends one
/// unit and leaves five, each still payable under the authorisation that is already approved.
/// </para>
/// <para>
/// Grouping would buy nothing to set against that. <c>SecEdgarProvider</c> issues one GET per
/// request with no continuation token and declares <c>supportsWindow: false</c>, so six members are
/// six requests however they are arranged; there is no per-batch overhead to amortise, and the six
/// do not share a reading scope - each has its own breach windows, and one of them has its own forms.
/// </para>
/// <para>
/// <strong>What is deliberately absent.</strong> There is no <c>ExpectedOutstandingBefore</c>. The
/// split partition carries one because a live ledger records what the store still owes in corporate
/// actions, and a batch that runs against a different number is refused. Nothing has been acquired
/// under this authorisation and no SEC ledger exists to owe anything, so a projection of it would be
/// a number with no source. <see cref="SecFilingBatchDefinition.ExpectedPriorConsumption"/> is kept,
/// because it has a source - the authorisation's own counter - and it pins the ordering: only the
/// batch whose declared prior spend equals what has actually been spent is admissible.
/// </para>
/// </remarks>
internal static class SecEdgarSixMemberPartition
{
    /// <summary>The installed authorisation this partition is cut against, named per batch.</summary>
    public const string Declaration =
        "acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json";

    /// <summary>The identity the declaration must load as, checked rather than assumed.</summary>
    public const string AuthorizationId = "sec-edgar-gate6-six-members-2021-09-to-2026-08";

    /// <summary>The only source this partition names. There is no second, and no price source.</summary>
    public const string Source = "sec-edgar";

    /// <summary>
    /// One request per member, because the connector has no continuation token and no window.
    /// </summary>
    public const int PlannedRequests = 6;

    /// <summary>
    /// The six, in the ordinal CIK order the declaration lists them. Every batch unauthorised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the declaration's own, so that a batch number means the same member here and
    /// there. <c>Authorised</c> is a separate approval per batch and every one of them is false;
    /// <see cref="SecEdgarSixMemberPartitionTests"/> fails the build if one is flipped, and
    /// <see cref="SecEdgarSixMemberInstallationTests"/> asserts it a second time from the other side.
    /// </para>
    /// <para>
    /// <strong>Batches 1, 2 and 3 have run, one at a time.</strong> Batch 1 was approved for one
    /// canary request - QUMU, CIK 0000892482 - then batch 2 for one request after it - LGIQ, CIK
    /// 0001335112 - then batch 3 - ONEM, CIK 0001404123. Each was dispatched, each consumed one unit
    /// of six, and each flag was returned to false as soon as its approval was spent. That is the
    /// split partition's own convention, whose runner records it in the same words: every batch
    /// there was returned to unauthorised once its approval had been used. Each approval window was
    /// one filtered run long, and no two overlapped - each flag was already down before the next
    /// went up.
    /// </para>
    /// <para>
    /// <strong>None of the three can run again, and not only because their flags are down.</strong>
    /// The attempt artefacts they wrote are now the authorisation's spending record, so their
    /// <c>ExpectedPriorConsumption</c> of 0, 1 and 2 no longer match the 3 that has been spent;
    /// every one of their correlations is claimed; and the ledger holds a successful run for each of
    /// their request fingerprints. Any of the three checks refuses them, and none of them costs a
    /// unit.
    /// </para>
    /// <para>
    /// Batches 4 to 6 have never been approved. Batch 4 now declares the prior consumption that
    /// actually stands, which makes it the only batch the accounting would admit - and it is
    /// admitted by nothing until somebody approves it deliberately. That ordering gate is not
    /// decoration: it is what stops a batch being run out of turn on a mistyped identifier.
    /// </para>
    /// </remarks>
    internal static readonly SecFilingBatchDefinition[] Batches =
    [
        new(1, "0000892482", "QUMU.US", 1, 0, Declaration, Authorised: false),
        new(2, "0001335112", "LGIQ.US", 1, 1, Declaration, Authorised: false),
        new(3, "0001404123", "ONEM.US", 1, 2, Declaration, Authorised: false),
        new(4, "0001426332", "NGM.US", 1, 3, Declaration, Authorised: false),
        new(5, "0001775625", "SDC.US", 1, 4, Declaration, Authorised: false),
        new(6, "0001784851", "SHPW.US", 1, 5, Declaration, Authorised: false),
    ];

    /// <summary>What the partition would dispatch if every batch in it were approved and ran.</summary>
    /// <remarks>
    /// Summed from the batches rather than restated, so a batch that quietly grew changes this and
    /// stops agreeing with the authorisation's ceiling.
    /// </remarks>
    public static int Planned => Batches.Sum(b => b.Count);

    /// <param name="Index">The batch number an operator would name.</param>
    /// <param name="Cik">
    /// The subject, as EDGAR identifies it and as the authorisation lists it. This is the only
    /// identifier a request would carry.
    /// </param>
    /// <param name="Symbol">
    /// The EODHD symbol the same company trades under, carried so that a filing read under this
    /// batch can be joined to the price evidence that raised the question. Never a request parameter:
    /// <c>SecEdgarEndpoints.ForCategory(RegulatoryFilings, cik)</c> takes the CIK and nothing else.
    /// </param>
    /// <param name="Count">How many requests the batch was cut to hold. One, for all six.</param>
    /// <param name="ExpectedPriorConsumption">What must already have been spent when it runs.</param>
    /// <param name="Declaration">
    /// The authorisation file in <c>declarations/</c> that covers it. Named per batch so a filing
    /// batch cannot be run against a price or corporate-actions authorisation by accident.
    /// </param>
    /// <param name="Authorised">Whether this batch has been approved to run. None has.</param>
    internal sealed record SecFilingBatchDefinition(
        int Index,
        string Cik,
        string Symbol,
        int Count,
        int ExpectedPriorConsumption,
        string Declaration,
        bool Authorised);
}
