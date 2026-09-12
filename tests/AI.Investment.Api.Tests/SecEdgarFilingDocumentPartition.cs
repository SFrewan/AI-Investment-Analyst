using System.Text.Json;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The six one-member batches the sealed 30-document filing-document pilot is cut into.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every batch is unauthorised, and each needs its own approval.</strong> That is the
/// convention <see cref="SecEdgarSixMemberPartition"/> established and spent: a flag is raised for
/// one batch, that batch runs, and the flag goes down again before the next goes up. Defining a
/// partition is not approving one, and approving one is not dispatching it.
/// </para>
/// <para>
/// <strong>The partition is derived from the declaration, not chosen here.</strong>
/// <see cref="TargetsFor"/> reads the sealed pilot and takes targets in file order, five at a time.
/// The declaration already orders its thirty targets by CIK and then by the selection order within
/// each member, so consecutive groups of five are exactly the six members - checked rather than
/// assumed, by <see cref="TargetsFor"/> itself. Nothing here reselects a filing, queries a provider
/// or re-runs the "latest five" rule: F5 decided which documents these are, and this decides only
/// how many operator approvals stand between them and the wire.
/// </para>
/// <para>
/// <strong>Six of five rather than thirty of one.</strong> The declaration is already partitioned by
/// member with a per-member ceiling of five, and the existing partition's unit is the member, so a
/// batch number means the same member in both places. Thirty single-document approvals would be
/// thirty operator acts for one pilot without making any individual request safer: the ceiling, the
/// scope set and the expiry bound all thirty either way.
/// </para>
/// </remarks>
internal static class SecEdgarFilingDocumentPartition
{
    /// <summary>The installed authorisation this partition is cut against, named per batch.</summary>
    public const string Declaration =
        "acquisition-sec-edgar-filing-document-pilot-2026-09.json";

    /// <summary>The identity the authorisation must load as, checked rather than assumed.</summary>
    /// <remarks>
    /// Deliberately the same string as the sealed declaration's <c>DeclarationId</c>. The
    /// authorisation is <em>for</em> that declaration and names it in
    /// <c>DeclarationPath</c>; the two files are told apart by the <c>acquisition-</c> prefix the
    /// repository already uses for authorisations and by their schemas, which is the existing
    /// convention rather than a new one. Inventing a suffix would break the rule that an
    /// authorisation file is <c>acquisition-{AuthorizationId}.json</c>.
    /// </remarks>
    public const string AuthorizationId = "sec-edgar-filing-document-pilot-2026-09";

    /// <summary>The sealed F5 declaration, repository-relative, as the authorisation binds it.</summary>
    public const string PilotDeclarationPath =
        "declarations/sec-edgar-filing-document-pilot-2026-09.json";

    /// <summary>The only source this partition names. There is no second, and no price source.</summary>
    public const string Source = "sec-edgar";

    /// <summary>The category F4 added for documents, distinct from the submissions index.</summary>
    public static readonly string DataCategory =
        AI.Investment.Domain.Sources.DataCategory.RegulatoryFilingDocuments.ToString();

    /// <summary>The subject kind, taken from the production parser rather than retyped.</summary>
    public static readonly string SubjectKind = FilingDocumentSubject.SubjectKind;

    /// <summary>Thirty documents: six members, five each. The declaration's own arithmetic.</summary>
    public const int PlannedRequests = 30;

    /// <summary>How many documents one batch holds.</summary>
    public const int PerBatch = 5;

    /// <summary>
    /// The six, in the ordinal CIK order the declaration lists them. Every batch unauthorised.
    /// </summary>
    /// <remarks>
    /// The order is the declaration's own, so that a batch number means the same member here and
    /// there. <c>Authorised</c> is a separate approval per batch and every one of them is false;
    /// <see cref="SecEdgarFilingDocumentPartitionTests"/> fails the build if one is flipped without
    /// the deliberate act that is meant to accompany it.
    /// </remarks>
    internal static readonly FilingDocumentBatchDefinition[] Batches =
    [
        new(1, "0000892482", "QUMU.US", PerBatch, 5, Declaration, Authorised: false),
        new(2, "0001335112", "LGIQ.US", PerBatch, 5, Declaration, Authorised: false),
        new(3, "0001404123", "ONEM.US", PerBatch, 10, Declaration, Authorised: false),
        new(4, "0001426332", "NGM.US", PerBatch, 15, Declaration, Authorised: false),
        new(5, "0001775625", "SDC.US", PerBatch, 20, Declaration, Authorised: false),
        new(6, "0001784851", "SHPW.US", PerBatch, 25, Declaration, Authorised: false),
    ];

    /// <summary>What the partition would dispatch if every batch in it were approved and ran.</summary>
    /// <remarks>
    /// Summed from the batches rather than restated, so a batch that quietly grew changes this and
    /// stops agreeing with the authorisation's ceiling.
    /// </remarks>
    public static int Planned => Batches.Sum(b => b.Count);

    /// <summary>
    /// The five subject identifiers one batch covers, read from the sealed declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read, never chosen.</strong> The targets are taken in the declaration's file order,
    /// five at a time, so the partition is reproducible from the declaration and cannot drift from
    /// it. A hard-coded list here would be a second copy of the scope, and the two could disagree
    /// without anything noticing.
    /// </para>
    /// <para>
    /// The member check is not decoration. It is what makes "consecutive fives are the six members"
    /// a verified property of the file rather than an assumption about how it was written, and it
    /// throws rather than returning a batch of somebody else's documents.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> TargetsFor(int batchIndex)
    {
        var batch = Batches.SingleOrDefault(b => b.Index == batchIndex)
            ?? throw new ArgumentOutOfRangeException(
                nameof(batchIndex),
                batchIndex,
                "The partition has six one-member batches. There is no default batch: a run that " +
                "does not say which batch it is must not pick one.");

        using var declaration = JsonDocument.Parse(
            File.ReadAllText(Universe.RepositoryPath(PilotDeclarationPath.Split('/'))));

        var targets = declaration.RootElement
            .GetProperty("Targets")
            .EnumerateArray()
            .ToList();

        if (targets.Count != PlannedRequests)
        {
            throw new InvalidOperationException(
                $"The declaration holds {targets.Count} targets and this partition is cut for " +
                $"{PlannedRequests}. The partition is derived from the declaration, so a " +
                "disagreement between them is the declaration having changed under a sealed " +
                "authorisation, not a partition to be adjusted.");
        }

        var slice = targets
            .Skip((batchIndex - 1) * PerBatch)
            .Take(PerBatch)
            .ToList();

        foreach (var target in slice)
        {
            var cik = target.GetProperty("Cik").GetString();

            if (!string.Equals(cik, batch.Cik, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Batch {batchIndex} is for CIK `{batch.Cik}` and position " +
                    $"{targets.IndexOf(target)} of the declaration holds `{cik}`. Consecutive " +
                    "groups of five are the six members only while the declaration keeps its own " +
                    "ordering, and this refuses rather than dispatching another member's documents.");
            }
        }

        return slice
            .Select(t => t.GetProperty("SubjectIdentifier").GetString()!)
            .ToList();
    }

    /// <param name="Index">The batch number an operator would name.</param>
    /// <param name="Cik">
    /// The member this batch is for. Not a request parameter on its own: a filing-document request
    /// carries the whole <c>{cik}|{accessionNumber}|{primaryDocument}</c> identity, and the CIK
    /// alone would name a company rather than a document.
    /// </param>
    /// <param name="Symbol">
    /// The EODHD symbol the same company trades under, carried so a document read under this batch
    /// can be joined to the price evidence that raised the question. Never a request parameter, and
    /// never an identity: F5's targets are CIK, accession and primary document.
    /// </param>
    /// <param name="Count">How many documents the batch was cut to hold. Five, for all six.</param>
    /// <param name="ExpectedPriorConsumption">What must already have been spent when it runs.</param>
    /// <param name="Declaration">
    /// The authorisation file in <c>declarations/</c> that covers it. Named per batch so a
    /// filing-document batch cannot be run against the submissions, price or corporate-actions
    /// authorisation by accident.
    /// </param>
    /// <param name="Authorised">Whether this batch has been approved to run. None has.</param>
    internal sealed record FilingDocumentBatchDefinition(
        int Index,
        string Cik,
        string Symbol,
        int Count,
        int ExpectedPriorConsumption,
        string Declaration,
        bool Authorised);
}
