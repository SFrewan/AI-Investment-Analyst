using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Acquires the five filing documents of one authorised batch, or refuses before anything is spent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same shape as <see cref="SecFilingsBatchRunner"/>, for a different category.</strong>
/// Same gate order, same consumption boundary, same rule ids, same refusal-is-an-outcome discipline,
/// same test-side placement. It is not a second dispatch mechanism: it orchestrates the identical
/// production path - preflight, <see cref="IDataAcquisition"/>, the gateway, the seam, the archive
/// and the ledger - and adds only the two gates <c>@3</c> made possible, the declaration binding and
/// the expiry.
/// </para>
/// <para>
/// <strong>One batch is five requests, not one.</strong> A submissions batch is one company and one
/// document; a filing-document batch is five named documents. So the batch-level gates run once and
/// the per-target gates run five times, each consuming its own unit at its own consumption boundary.
/// A target that refuses costs nothing and does not stop the other four - and a target that fails
/// after dispatch has still been paid for, because the call was made.
/// </para>
/// <para>
/// <strong>Every dependency is injected and nothing is ambient.</strong> No environment variable is
/// read here, no file is opened, no clock is consulted except through <see cref="IClock"/>. That is
/// what lets the whole gate order be proven against fakes without a network, and it is why the
/// deployment identity arrives as a string rather than being fetched.
/// </para>
/// </remarks>
internal sealed class SecFilingDocumentBatchRunner
{
    /// <summary>The only source this runner names. There is no other, and no price source.</summary>
    public const string Source = "sec-edgar";

    /// <summary>The environment variable carrying the contact address. Its value is never recorded.</summary>
    public const string ContactVariable = "AIINV_SEC_CONTACT";

    // ---- outcome statuses, as the submissions runner names them ----------------------------------

    public const string RefusedStatus = "refused";
    public const string SuppressedStatus = "suppressed";
    public const string DispatchedStatus = "dispatched";
    public const string ThrewStatus = "threw";

    // ---- the runner's own gates, ahead of the gateway's -------------------------------------------

    public const string BatchAuthorisedRule = "runner.batch-authorised@1";
    public const string MemberIdentityRule = "runner.member-identity@1";
    public const string SourceCategoryRule = "runner.source-category@1";
    public const string AuthorisationCoversRule = "runner.authorisation-covers@1";
    public const string RequestWindowAbsentRule = "runner.request-window-absent@1";
    public const string DeploymentIdentityRule = "runner.deployment-identity@1";
    public const string PriorConsumptionRule = "runner.prior-consumption@1";
    public const string DispatchCeilingRule = "runner.dispatch-ceiling@1";
    public const string AttemptAlreadyClaimedRule = "runner.attempt-already-claimed@1";

    /// <summary>New in F6d: the authorisation must be bound to the declaration the batch names.</summary>
    public const string DeclarationBindingRule = "runner.declaration-binding@1";

    /// <summary>New in F6d: an expired authorisation refuses before the consumption boundary.</summary>
    public const string AuthorisationExpiredRule = "runner.authorisation-expired@1";

    /// <summary>The authorisation's scope window. Used for <c>Covers</c> and for nothing else.</summary>
    public static readonly DateOnly WindowFrom = new(2021, 9, 1);

    /// <inheritdoc cref="WindowFrom"/>
    public static readonly DateOnly WindowTo = new(2026, 8, 31);

    private readonly IDataAcquisition _acquisition;
    private readonly IIngestionRunStore _runStore;
    private readonly IRawResponseArchive _archive;
    private readonly IClock _clock;

    public SecFilingDocumentBatchRunner(
        IDataAcquisition acquisition,
        IIngestionRunStore runStore,
        IRawResponseArchive archive,
        IClock clock)
    {
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// The correlation one attempt at one document carries. Deterministic, and distinct per target.
    /// </summary>
    /// <remarks>
    /// It carries the batch, the attempt number and the accession, so five documents in one batch
    /// are five distinct acts rather than one repeated - and a second approved attempt at the same
    /// document is a new act, which is the mechanism F6a settled the changed-document question on.
    /// The document name is deliberately not in it: the accession already identifies the filing
    /// within the batch, and correlations are length-bounded.
    /// </remarks>
    public static string CorrelationFor(int batchIndex, int attempt, string accessionNumber) =>
        AcquisitionCorrelation.Text(
            batchIndex, attempt, accessionNumber, DataCategory.RegulatoryFilingDocuments);

    /// <summary>
    /// The request for one document. Built in one place, and never with a window.
    /// </summary>
    /// <remarks>
    /// <strong>Two windows, and they must not meet.</strong> The authorisation's scope window is
    /// passed to <c>Covers</c> as literal arguments and nowhere else. The provider request carries
    /// no period at all, because <c>SecEdgarProvider</c> declares <c>supportsWindow: false</c>: a
    /// filing document is one document and takes no period, so a windowed request is refused by the
    /// capability gate. There is deliberately no parameter here by which a caller could supply one.
    /// </remarks>
    public IngestionRequest BuildRequest(string subjectIdentifier, string correlation) =>
        IngestionRequest.Create(
            SourceId.Create(Source),
            DataCategory.RegulatoryFilingDocuments,
            Region.UnitedStates,
            IngestionSubject.Create(FilingDocumentSubject.SubjectKind, subjectIdentifier),
            CorrelationId.Create(correlation),
            _clock.UtcNow);

    /// <summary>
    /// Runs one batch: five documents, five units, or refusals that cost nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order below is the contract, and each step is a separate named refusal so that "no data
    /// appeared" never has to be guessed at. Everything before a target's consumption boundary is
    /// deterministic and free; everything after it has been paid for.
    /// </para>
    /// <para>
    /// Nothing here throws for an unsuccessful acquisition. A caller running five documents must not
    /// lose four to one transport failure, and - more importantly - an exception escaping after
    /// consumption would end the run before the record of what was spent could be written, which is
    /// the one outcome worse than a failed request.
    /// </para>
    /// </remarks>
    public async Task<FilingDocumentRunOutcome> RunAsync(
        FilingDocumentRunContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var batch = context.Batch;
        var authorization = context.Authorization;
        var before = authorization.Consumed;

        FilingDocumentRunOutcome RefuseBatch(string rule, string reason) =>
            FilingDocumentRunOutcome.RefusedBatch(
                batch, context.AttemptNumber, rule, reason, before, authorization);

        // 1. The batch is approved. First, and ahead of everything that could reach a provider.
        if (!batch.Authorised)
        {
            return RefuseBatch(
                BatchAuthorisedRule,
                Universe.Inv($"batch {batch.Index} (`{batch.Cik}` / {batch.Symbol}) is defined but NOT authorised to run. Each batch is approved separately. Nothing was dispatched."));
        }

        // 2. The authorisation is the document-scoped kind and is bound to the declaration this
        //    batch names. A @1 or @2 authorisation has no binding at all, so it could not be the
        //    one that approved a named set of documents however well its symbols happened to match.
        if (authorization.Scope is not { } scope)
        {
            return RefuseBatch(
                DeclarationBindingRule,
                Universe.Inv($"authorisation `{authorization.AuthorizationId}` is `{authorization.SchemaVersion}` and carries no declaration binding. A filing-document batch is approved against a sealed declaration, not against a list of subjects."));
        }

        if (!string.Equals(batch.Declaration, context.AuthorizationFileName, StringComparison.Ordinal))
        {
            return RefuseBatch(
                SourceCategoryRule,
                Universe.Inv($"batch {batch.Index} names declaration `{batch.Declaration}`, and the authorisation loaded is `{context.AuthorizationFileName}`. A batch charged against another declaration would spend a budget approved for something else."));
        }

        if (!string.Equals(scope.DeclarationPath, context.PilotDeclarationPath, StringComparison.Ordinal))
        {
            return RefuseBatch(
                DeclarationBindingRule,
                Universe.Inv($"the authorisation is bound to `{scope.DeclarationPath}` and this batch is cut from `{context.PilotDeclarationPath}`. The targets could coincide and the approval would still be for a different document."));
        }

        // 3. It has not expired. Before the consumption boundary, so a dead authorisation is free
        //    to discover rather than costing a unit to learn about.
        var validity = authorization.IsValidAt(_clock.UtcNow);

        if (!validity.Allowed)
        {
            return RefuseBatch(AuthorisationExpiredRule, validity.Reason!);
        }

        // 4. The source and category are the ones this authorisation is for.
        if (!authorization.Sources.Contains(Source))
        {
            return RefuseBatch(
                SourceCategoryRule,
                Universe.Inv($"the authorisation does not name source `{Source}`."));
        }

        if (!string.Equals(scope.DataCategory, nameof(DataCategory.RegulatoryFilingDocuments), StringComparison.Ordinal))
        {
            return RefuseBatch(
                SourceCategoryRule,
                Universe.Inv($"the authorisation is for category `{scope.DataCategory}` and this runner acquires `{nameof(DataCategory.RegulatoryFilingDocuments)}`."));
        }

        if (!string.Equals(scope.SubjectKind, FilingDocumentSubject.SubjectKind, StringComparison.Ordinal))
        {
            return RefuseBatch(
                SourceCategoryRule,
                Universe.Inv($"the authorisation is for subject kind `{scope.SubjectKind}` and this runner names `{FilingDocumentSubject.SubjectKind}`."));
        }

        // 5. The batch holds the number of targets it was cut for, and every one of them is a
        //    document this member filed.
        if (context.Targets.Count != batch.Count)
        {
            return RefuseBatch(
                MemberIdentityRule,
                Universe.Inv($"batch {batch.Index} was cut for {batch.Count} documents and holds {context.Targets.Count}."));
        }

        foreach (var target in context.Targets)
        {
            if (!target.StartsWith(batch.Cik + "|", StringComparison.Ordinal))
            {
                return RefuseBatch(
                    MemberIdentityRule,
                    Universe.Inv($"batch {batch.Index} is for `{batch.Cik}` and holds `{target}`. A batch does not dispatch another member's documents."));
            }

            if (!authorization.Symbols.Contains(target))
            {
                return RefuseBatch(
                    MemberIdentityRule,
                    Universe.Inv($"`{target}` is not one of the {authorization.Symbols.Count} documents the authorisation names."));
            }
        }

        // 6. The accounting the approval was given against, checked before it is charged.
        if (before != batch.ExpectedPriorConsumption)
        {
            return RefuseBatch(
                PriorConsumptionRule,
                Universe.Inv($"{before} dispatch(es) have been consumed against this authorisation and batch {batch.Index} was approved against {batch.ExpectedPriorConsumption}. The accounting has moved since the approval."));
        }

        if (authorization.Remaining < batch.Count)
        {
            return RefuseBatch(
                DispatchCeilingRule,
                Universe.Inv($"{authorization.Remaining} dispatch(es) remain against a batch of {batch.Count}."));
        }

        // 7. Fair-access identity. The value is checked and never recorded.
        if (!DeploymentIdentityIsUsable(context.ContactAddress))
        {
            return RefuseBatch(
                DeploymentIdentityRule,
                Universe.Inv($"{ContactVariable} is absent or not a well-formed address (value not recorded). EDGAR fair access requires every request to name a contact, and this runner will not invent one."));
        }

        // ---- per target ---------------------------------------------------------------------------

        var results = new List<FilingDocumentTargetOutcome>();

        foreach (var target in context.Targets)
        {
            results.Add(await RunTargetAsync(context, target, cancellationToken).ConfigureAwait(false));
        }

        return FilingDocumentRunOutcome.ForBatch(
            batch, context.AttemptNumber, before, authorization, results);
    }

    private async Task<FilingDocumentTargetOutcome> RunTargetAsync(
        FilingDocumentRunContext context,
        string target,
        CancellationToken cancellationToken)
    {
        var batch = context.Batch;
        var authorization = context.Authorization;
        var accession = target.Split('|')[1];
        var correlation = CorrelationFor(batch.Index, context.AttemptNumber, accession);

        FilingDocumentTargetOutcome Refuse(string rule, string reason) =>
            FilingDocumentTargetOutcome.Refused(target, correlation, rule, reason);

        // The identifier is one the production parser accepts. A malformed or viewer-rendered
        // target never becomes a request: F4b's refusal semantics, applied before the wire.
        var parsed = FilingDocumentSubject.Parse(target);

        if (!parsed.IsAccepted)
        {
            return Refuse(
                MemberIdentityRule,
                Universe.Inv($"`{target}` was refused by the production parser as {parsed.Refusal}. It is not rewritten, guessed at, or substituted."));
        }

        // Coverage, using the authorisation's own scope window, category and subject kind.
        var covered = authorization.Covers(
            Source, target, WindowFrom, WindowTo,
            nameof(DataCategory.RegulatoryFilingDocuments), FilingDocumentSubject.SubjectKind);

        if (!covered.Allowed)
        {
            return Refuse(AuthorisationCoversRule, covered.Reason!);
        }

        var request = BuildRequest(target, correlation);

        // Belt and braces: the builder takes no window parameter, so this can only fail if someone
        // later gives it one. Refusing here costs nothing; discovering it at the capability gate
        // would cost a unit, because that gate sits after consumption.
        if (request.Window is not null)
        {
            return Refuse(
                RequestWindowAbsentRule,
                "the request carries a period and the connector declares supportsWindow=false.");
        }

        // Every deterministic gate the gateway would apply, applied now, for free.
        var preflight = IngestionPreflight.Evaluate(context.Source, context.Provider, request);

        if (!preflight.IsAdmitted)
        {
            return Refuse(preflight.RuleId!, preflight.Reason!);
        }

        // The ledger has already answered this exact request. Suppressed, and it costs nothing.
        var fingerprint = request.Fingerprint();

        if (await _runStore.HasCompletedAsync(fingerprint, cancellationToken).ConfigureAwait(false))
        {
            authorization.RecordSuppressed();

            return FilingDocumentTargetOutcome.Suppressed(target, correlation, fingerprint);
        }

        if (context.BurnedCorrelations.Contains(correlation))
        {
            return Refuse(
                AttemptAlreadyClaimedRule,
                Universe.Inv($"`{correlation}` was claimed by an earlier recorded attempt. A new attempt needs a new attempt number and its own approval."));
        }

        // ---- THE CONSUMPTION BOUNDARY -------------------------------------------------------------
        // Everything above is deterministic and free. Everything below has been paid for.

        var permitted = authorization.TryConsume(
            Source, target, WindowFrom, WindowTo, _clock.UtcNow,
            nameof(DataCategory.RegulatoryFilingDocuments), FilingDocumentSubject.SubjectKind);

        if (!permitted.Allowed)
        {
            return Refuse(DispatchCeilingRule, permitted.Reason!);
        }

        // ---- DISPATCH -----------------------------------------------------------------------------

        AcquisitionResult result;

#pragma warning disable CA1031 // Deliberate: an unhandled exception here would end the run before
                              // the record of what has already been spent could be written, which is
                              // the one outcome worse than a failed request.
        try
        {
            result = await _acquisition.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Type name only. This text is long-lived and a provider's message is one of the likelier
            // places for a URL with a credential in it to surface.
            return FilingDocumentTargetOutcome.Threw(
                target, correlation, fingerprint, exception.GetType().Name);
        }
#pragma warning restore CA1031

        // ---- WHAT WAS ARCHIVED --------------------------------------------------------------------

        var hash = result.Run.Artifacts.Count > 0 ? result.Run.Artifacts[0].Value : null;
        int? bytes = null;

        if (result.Run.Artifacts.Count > 0)
        {
            var described = await _archive
                .DescribeAsync(result.Run.Artifacts[0], cancellationToken)
                .ConfigureAwait(false);

            bytes = described?.ByteLength;
        }

        return FilingDocumentTargetOutcome.Dispatched(
            target, correlation, fingerprint, result.Run.Outcome.ToString(), hash, bytes,
            result.Run.Reason, result.ObservationsRecorded);
    }

    /// <summary>
    /// Whether the contact address is present and shaped like one. Its value is never recorded.
    /// </summary>
    /// <remarks>
    /// Shape only, because this cannot establish that an address is monitored - that is the
    /// operator's assertion, and the runner's job is to refuse when there is plainly nothing to
    /// assert. The value is never interpolated into a refusal, an artefact or a log.
    /// </remarks>
    private static bool DeploymentIdentityIsUsable(string? contact) =>
        !string.IsNullOrWhiteSpace(contact)
        && System.Text.RegularExpressions.Regex.IsMatch(
            contact,
            "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));
}

/// <summary>Everything one batch run needs, injected rather than fetched.</summary>
/// <param name="Batch">The batch definition, including whether it is approved.</param>
/// <param name="Targets">Its five subject identifiers, read from the sealed declaration.</param>
/// <param name="Authorization">The loaded, digest-verified authorisation.</param>
/// <param name="AuthorizationFileName">The file it was loaded from, checked against the batch.</param>
/// <param name="PilotDeclarationPath">The declaration the batch was cut from.</param>
/// <param name="Source">The registered source, as the registry holds it.</param>
/// <param name="Provider">The connector, or null when none is registered.</param>
/// <param name="ContactAddress">The fair-access contact. Checked for shape, never recorded.</param>
/// <param name="AttemptNumber">Which approved attempt at this batch this is.</param>
/// <param name="BurnedCorrelations">Correlations earlier recorded attempts already claimed.</param>
internal sealed record FilingDocumentRunContext(
    SecEdgarFilingDocumentPartition.FilingDocumentBatchDefinition Batch,
    IReadOnlyList<string> Targets,
    AcquisitionAuthorization Authorization,
    string AuthorizationFileName,
    string PilotDeclarationPath,
    DataSource? Source,
    IDataProvider? Provider,
    string? ContactAddress,
    int AttemptNumber,
    IReadOnlySet<string> BurnedCorrelations);

/// <summary>What became of one document.</summary>
internal sealed record FilingDocumentTargetOutcome(
    string Target,
    string Correlation,
    string Status,
    string? RuleId,
    string? Reason,
    string? Fingerprint,
    string? ContentHash,
    int? ByteLength,
    string? RunOutcome,
    int ObservationsRecorded)
{
    public bool WasDispatched =>
        string.Equals(Status, SecFilingDocumentBatchRunner.DispatchedStatus, StringComparison.Ordinal)
        || string.Equals(Status, SecFilingDocumentBatchRunner.ThrewStatus, StringComparison.Ordinal);

    public bool Archived => ContentHash is not null;

    public static FilingDocumentTargetOutcome Refused(
        string target, string correlation, string rule, string reason) =>
        new(target, correlation, SecFilingDocumentBatchRunner.RefusedStatus, rule, reason,
            null, null, null, null, 0);

    public static FilingDocumentTargetOutcome Suppressed(
        string target, string correlation, string fingerprint) =>
        new(target, correlation, SecFilingDocumentBatchRunner.SuppressedStatus, null,
            "the ledger already holds a completed run for this exact request.",
            fingerprint, null, null, null, 0);

    public static FilingDocumentTargetOutcome Threw(
        string target, string correlation, string fingerprint, string exceptionType) =>
        new(target, correlation, SecFilingDocumentBatchRunner.ThrewStatus, null, exceptionType,
            fingerprint, null, null, null, 0);

    public static FilingDocumentTargetOutcome Dispatched(
        string target, string correlation, string fingerprint, string runOutcome,
        string? contentHash, int? byteLength, string? reason, int observations) =>
        new(target, correlation, SecFilingDocumentBatchRunner.DispatchedStatus, null, reason,
            fingerprint, contentHash, byteLength, runOutcome, observations);
}

/// <summary>What became of one batch, and what it cost.</summary>
internal sealed record FilingDocumentRunOutcome(
    int BatchIndex,
    string Cik,
    string Symbol,
    int AttemptNumber,
    string Status,
    string? RuleId,
    string? Reason,
    int ConsumedBefore,
    int ConsumedThisRun,
    int ConsumedTotal,
    int Remaining,
    int Suppressed,
    IReadOnlyList<FilingDocumentTargetOutcome> Targets)
{
    public int Dispatched => Targets.Count(t => t.WasDispatched);

    public int Archived => Targets.Count(t => t.Archived);

    public static FilingDocumentRunOutcome RefusedBatch(
        SecEdgarFilingDocumentPartition.FilingDocumentBatchDefinition batch,
        int attempt,
        string rule,
        string reason,
        int before,
        AcquisitionAuthorization authorization) =>
        new(batch.Index, batch.Cik, batch.Symbol, attempt,
            SecFilingDocumentBatchRunner.RefusedStatus, rule, reason,
            before, 0, authorization.Consumed, authorization.Remaining, authorization.Suppressed,
            []);

    public static FilingDocumentRunOutcome ForBatch(
        SecEdgarFilingDocumentPartition.FilingDocumentBatchDefinition batch,
        int attempt,
        int before,
        AcquisitionAuthorization authorization,
        IReadOnlyList<FilingDocumentTargetOutcome> targets) =>
        new(batch.Index, batch.Cik, batch.Symbol, attempt,
            SecFilingDocumentBatchRunner.DispatchedStatus, null, null,
            before, authorization.Consumed - before, authorization.Consumed,
            authorization.Remaining, authorization.Suppressed, targets);
}
