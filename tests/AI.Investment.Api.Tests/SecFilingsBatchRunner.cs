using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Acquires one SEC filing history for one authorised member, or refuses before anything is spent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Where this lives, and why it is not under <c>src/</c>.</strong> Everything the runner has
/// to reason about - <see cref="AcquisitionAuthorization"/>, the partition,
/// <see cref="SubmissionsCompleteness"/>, <see cref="AcquisitionCorrelation"/> - is test-side, by the
/// same convention that puts <c>AcquisitionSplitBatchTests</c> and <c>AcquisitionBatchTests</c>
/// there. Moving this class under <c>src/</c> would mean moving authorisation accounting into
/// production with it: a new production concept and a relocation of authorisation semantics, both of
/// which Phase B was told to stop and report on rather than perform. So the runner is test-side and
/// <strong>no production file changes in this phase</strong>. What it orchestrates is entirely
/// production: the preflight, the gateway, the archive, the normaliser, the ledger and the seam.
/// </para>
/// <para>
/// <strong>Every dependency is injected and nothing is ambient.</strong> No environment variable is
/// read here, no file is opened, no clock is consulted except through <see cref="IClock"/>. That is
/// what lets the whole gate order be proven against fakes without a network, a database or a
/// provider - which is the entire test suite for this class.
/// </para>
/// <para>
/// <strong>The one gate that is deliberately downstream.</strong> The provider rate limiter reserves
/// a slot when it passes, so it stays where it is - inside <c>IngestionGateway</c>, after
/// consumption. Phase A recorded that as a deferred limitation and Phase B does not redesign it: a
/// run refused on rate limit still costs one unit. Nothing here peeks at it, reserves against it, or
/// duplicates it.
/// </para>
/// </remarks>
internal sealed class SecFilingsBatchRunner
{
    /// <summary>The only source this runner names. There is no other, and no price source.</summary>
    public const string Source = "sec-edgar";

    /// <summary>The subject kind EDGAR's connector understands.</summary>
    public const string SubjectKind = "Company";

    /// <summary>The name this installation identifies itself by under the SEC's fair-access policy.</summary>
    public const string ApplicationName = "AI-Investment-Analyst";

    /// <summary>The environment variable carrying the contact address. Its value is never recorded.</summary>
    public const string ContactVariable = "AIINV_SEC_CONTACT";

    // ---- outcome statuses ------------------------------------------------------------------------

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
    public const string ArchiveUnreadableRule = "runner.archive-unreadable@1";

    /// <summary>The authorisation's scope window. Used for <c>Covers</c> and for nothing else.</summary>
    public static readonly DateOnly WindowFrom = new(2021, 9, 1);

    /// <inheritdoc cref="WindowFrom"/>
    public static readonly DateOnly WindowTo = new(2026, 8, 31);

    private readonly IDataAcquisition _acquisition;
    private readonly IIngestionRunStore _runStore;
    private readonly IRawResponseArchive _archive;
    private readonly IClock _clock;

    public SecFilingsBatchRunner(
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
    /// The correlation one attempt at one member carries. Deterministic, and distinct per attempt.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="AcquisitionCorrelation"/> so it obeys the rule that class was written
    /// for: a symbol-only correlation makes a transport failure permanent, because the seam claims
    /// the key and refuses the retry as a duplicate. Carrying the attempt number makes each approved
    /// attempt a distinct act while leaving the seam's duplicate rule exactly as strict.
    /// </remarks>
    public static string CorrelationFor(int batchIndex, int attempt, string cik) =>
        AcquisitionCorrelation.Text(batchIndex, attempt, cik, DataCategory.RegulatoryFilings);

    /// <summary>
    /// The request this runner would dispatch. Built in one place, and never with a window.
    /// </summary>
    /// <remarks>
    /// <strong>Two windows, and they must not meet.</strong> The authorisation's scope window
    /// (2021-09-01..2026-08-31) is passed to <c>Covers</c> as literal arguments and nowhere else.
    /// The provider request carries no period at all, because <c>SecEdgarProvider</c> declares
    /// <c>supportsWindow: false</c>: the submissions endpoint returns one whole document and takes
    /// no period, so a windowed request is refused by the capability gate. There is deliberately no
    /// parameter here by which a caller could supply one.
    /// </remarks>
    public IngestionRequest BuildRequest(string cik, string correlation) =>
        IngestionRequest.Create(
            SourceId.Create(Source),
            DataCategory.RegulatoryFilings,
            Region.UnitedStates,
            IngestionSubject.Create(SubjectKind, cik),
            CorrelationId.Create(correlation),
            _clock.UtcNow);

    /// <summary>
    /// Runs one batch: one member, one request, one unit, or a refusal that costs nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order below is the contract, and each step is a separate named refusal so that "no data
    /// appeared" never has to be guessed at. Everything before the consumption boundary is
    /// deterministic and free; everything after it has been paid for.
    /// </para>
    /// <para>
    /// Nothing here throws for an unsuccessful acquisition. A caller running six members must not
    /// lose five to one transport failure, and - more importantly - an exception escaping after
    /// consumption would end the run before the record of what was spent could be written, which is
    /// the one outcome worse than a failed request.
    /// </para>
    /// </remarks>
    public async Task<SecRunOutcome> RunAsync(SecRunContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var batch = context.Batch;
        var authorization = context.Authorization;
        var before = authorization.Consumed;
        var correlation = CorrelationFor(batch.Index, context.AttemptNumber, batch.Cik);
        var forms = FormsInScopeFor(context, batch.Cik);

        SecRunOutcome Refuse(string rule, string reason) =>
            SecRunOutcome.Refused(batch, context.AttemptNumber, correlation, rule, reason, before, authorization, forms);

        // 3. The batch is approved. First, and ahead of everything that could reach a provider.
        if (!batch.Authorised)
        {
            return Refuse(
                BatchAuthorisedRule,
                Universe.Inv($"batch {batch.Index} (`{batch.Cik}`) is defined but NOT authorised to run. Each batch is approved separately. Nothing was dispatched."));
        }

        // 4. The member is who the sealed universe says, and the authorisation names it.
        if (!context.SealedCiks.Contains(batch.Cik))
        {
            return Refuse(
                MemberIdentityRule,
                Universe.Inv($"`{batch.Cik}` is not a member of the sealed universe this authorisation is for."));
        }

        if (!authorization.Symbols.Contains(batch.Cik))
        {
            return Refuse(
                MemberIdentityRule,
                Universe.Inv($"`{batch.Cik}` is not one of the {authorization.Symbols.Count} subjects the authorisation names."));
        }

        // 5. The source and category are the installed ones, and the batch names the right declaration.
        if (!string.Equals(batch.Declaration, SecEdgarSixMemberPartition.Declaration, StringComparison.Ordinal))
        {
            return Refuse(
                SourceCategoryRule,
                Universe.Inv($"batch {batch.Index} names declaration `{batch.Declaration}`, not the installed SEC authorisation. A filing batch charged against another declaration would spend a budget approved for something else."));
        }

        if (!authorization.Sources.Contains(Source))
        {
            return Refuse(
                SourceCategoryRule,
                Universe.Inv($"the authorisation does not name source `{Source}`."));
        }

        // 6. Coverage, using the authorisation's own scope window and no other dates.
        var covered = authorization.Covers(Source, batch.Cik, WindowFrom, WindowTo);

        if (!covered.Allowed)
        {
            return Refuse(AuthorisationCoversRule, covered.Reason!);
        }

        // 7. The request, built without a window.
        var request = BuildRequest(batch.Cik, correlation);

        // Belt and braces: the builder takes no window parameter, so this can only fail if someone
        // later gives it one. Refusing here costs nothing; discovering it at the capability gate
        // would cost a unit, because that gate sits after consumption.
        if (request.Window is not null)
        {
            return Refuse(
                RequestWindowAbsentRule,
                "the request carries a period and the connector declares supportsWindow=false. The authorisation's scope window is for Covers() and must never be sent to the provider.");
        }

        // 8. Every deterministic gate the gateway would apply, applied now, for free.
        var preflight = IngestionPreflight.Evaluate(context.Source, context.Provider, request);

        if (!preflight.IsAdmitted)
        {
            return Refuse(preflight.RuleId!, preflight.Reason!);
        }

        // 9. Fair-access identity. The value is checked and never recorded.
        if (!DeploymentIdentityIsUsable(context.ContactAddress))
        {
            return Refuse(
                DeploymentIdentityRule,
                Universe.Inv($"{ContactVariable} is absent or not a well-formed address (value not recorded). EDGAR fair access requires every request to name a contact, and this runner will not invent one."));
        }

        // The accounting the approval was given against, checked before it is charged.
        if (before != batch.ExpectedPriorConsumption)
        {
            return Refuse(
                PriorConsumptionRule,
                Universe.Inv($"{before} dispatch(es) have been consumed against this authorisation and batch {batch.Index} was approved against {batch.ExpectedPriorConsumption}. The accounting has moved since the approval."));
        }

        if (authorization.Remaining < batch.Count)
        {
            return Refuse(
                DispatchCeilingRule,
                Universe.Inv($"{authorization.Remaining} dispatch(es) remain against a batch of {batch.Count}."));
        }

        // The ledger has already answered this exact request. Suppressed, and it costs nothing.
        var fingerprint = request.Fingerprint();

        if (await _runStore.HasCompletedAsync(fingerprint, cancellationToken).ConfigureAwait(false))
        {
            authorization.RecordSuppressed();

            return SecRunOutcome.Suppressed(batch, context.AttemptNumber, correlation, fingerprint, before, authorization, forms);
        }

        // An earlier attempt already claimed this correlation, so the seam would refuse it as a
        // duplicate after the unit was spent. Refused here instead, unspent.
        if (context.BurnedCorrelations.Contains(correlation))
        {
            return Refuse(
                AttemptAlreadyClaimedRule,
                Universe.Inv($"`{correlation}` was claimed by an earlier recorded attempt. A new attempt needs a new attempt number and its own approval."));
        }

        // ---- THE CONSUMPTION BOUNDARY -------------------------------------------------------------
        // Everything above is deterministic and free. Everything below has been paid for.

        var permitted = authorization.TryConsume(Source, batch.Cik, WindowFrom, WindowTo);

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
            return SecRunOutcome.Threw(
                batch, context.AttemptNumber, correlation, fingerprint, exception.GetType().Name,
                before, authorization, forms);
        }
#pragma warning restore CA1031

        // ---- INTERPRETATION -----------------------------------------------------------------------
        // Coverage first, always. A count of zero taken from a document that does not reach the
        // window is not a count of zero; it is no count at all.

        var reading = await ReadAsync(result, context, forms, cancellationToken).ConfigureAwait(false);

        return SecRunOutcome.ForDispatch(
            batch, context.AttemptNumber, correlation, fingerprint, before, authorization, forms, result, reading);
    }

    /// <summary>
    /// Reads the archived document and applies the completeness rule before interpreting absence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes come from the archive rather than from the response, because the archive is what a
    /// replay would read and what a later re-interpretation would use. A run that archived nothing -
    /// refused, failed - yields no reading at all rather than an empty one.
    /// </para>
    /// <para>
    /// <strong>The verdict is <see cref="SubmissionsCompleteness"/>'s, not this class's.</strong>
    /// Nothing here composes a label; it counts filings and hands the count to <c>Classify</c>, which
    /// checks coverage first. <c>filings.files</c> is read and reported and never followed, and no
    /// numeric cap on inline filings is assumed anywhere.
    /// </para>
    /// </remarks>
    private async Task<SecReading?> ReadAsync(
        AcquisitionResult result,
        SecRunContext context,
        IReadOnlyList<string> forms,
        CancellationToken cancellationToken)
    {
        if (result.Run.Artifacts.Count == 0)
        {
            return null;
        }

        var hash = result.Run.Artifacts[0];
        var payload = await _archive.RetrieveAsync(hash, cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            return new SecReading(
                hash.Value, ArchiveUnreadableRule, null, null, 0, false, 0, [], false);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            // The payload stays archived and the normaliser has already quarantined it under its own
            // rule. Nothing is interpreted from bytes that could not be read.
            return new SecReading(
                hash.Value, ArchiveUnreadableRule, null, null, 0, false, 0, [], false);
        }

        using (document)
        {
            var root = document.RootElement;
            var dates = SubmissionsCompleteness.ReadRecentFilingDates(root);
            var holdsOlder = SubmissionsCompleteness.HoldsOlderFilings(root);
            var coverage = SubmissionsCompleteness.Evaluate(context.EarliestWindowStart, dates, holdsOlder);
            var relevant = RelevantFilings(root, context, forms);
            var verdict = SubmissionsCompleteness.Classify(coverage, relevant.Count);

            return new SecReading(
                hash.Value,
                null,
                coverage.Coverage,
                verdict,
                coverage.InlineFilings,
                coverage.HoldsOlderFilings,
                relevant.Count,
                relevant,
                coverage.CoversWindow);
        }
    }

    /// <summary>
    /// Filings inside one of this member's selection windows whose form is in scope for it.
    /// </summary>
    private static List<string> RelevantFilings(
        JsonElement root,
        SecRunContext context,
        IReadOnlyList<string> forms)
    {
        var found = new List<string>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("filings", out var filings)
            || !filings.TryGetProperty("recent", out var recent)
            || recent.ValueKind != JsonValueKind.Object)
        {
            return found;
        }

        var formColumn = Column(recent, "form");
        var dateColumn = Column(recent, "filingDate");
        var accessionColumn = Column(recent, "accessionNumber");

        if (dateColumn is not { } dates)
        {
            return found;
        }

        for (var row = 0; row < dates.GetArrayLength(); row++)
        {
            var filed = DateAt(dates, row);
            var form = TextAt(formColumn, row);

            if (filed is not { } date || form is null)
            {
                continue;
            }

            if (!FormMatches(form, forms))
            {
                continue;
            }

            if (!context.SelectionWindows.Any(w => date >= w.From && date <= w.To))
            {
                continue;
            }

            found.Add(TextAt(accessionColumn, row) ?? Universe.Inv($"{form}@{date:yyyy-MM-dd}"));
        }

        return found;
    }

    /// <summary>
    /// The forms this member may be read for: the shared list, plus its own scope when it has one.
    /// </summary>
    /// <remarks>
    /// <strong>The secondary scope belongs to exactly one member.</strong> The installed declaration
    /// grants <c>424B*</c>, <c>S-1</c> and <c>S-3</c> to LGIQ alone, with
    /// <c>AppliesToOtherMembers: false</c>. This is where that is honoured: a member whose CIK is not
    /// the scoped one gets the shared list and nothing else, whatever the context carries.
    /// </remarks>
    public static IReadOnlyList<string> FormsInScopeFor(SecRunContext context, string cik)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SecondaryFormsCik is null
            || !string.Equals(context.SecondaryFormsCik, cik, StringComparison.Ordinal))
        {
            return context.SharedForms;
        }

        return [.. context.SharedForms, .. context.SecondaryForms];
    }

    /// <summary>Whether a stated form is in a scope list, honouring a trailing <c>*</c>.</summary>
    /// <remarks>
    /// EDGAR writes a family of offering documents as <c>424B1</c> through <c>424B8</c>, and the
    /// declaration names the family as <c>424B*</c>. The wildcard is a prefix and nothing more: it
    /// matches at the end of a pattern only, so no pattern can be made to match everything.
    /// </remarks>
    public static bool FormMatches(string form, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        if (string.IsNullOrWhiteSpace(form))
        {
            return false;
        }

        var stated = form.Trim();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];

                if (prefix.Length > 0 && stated.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(stated, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the configured fair-access identity would be accepted, without recording the value.
    /// </summary>
    /// <remarks>
    /// Both halves of the existing validation are run. <c>Validate</c> catches a blank address; the
    /// <c>[EmailAddress]</c> annotation catches a malformed one, and annotations run only when the
    /// validator is asked for them. A caller checking <c>Validate</c> alone would accept
    /// <c>not-an-email</c>. Nothing about the value leaves this method.
    /// </remarks>
    public static bool DeploymentIdentityIsUsable(string? contact)
    {
        var options = new SecEdgarOptions
        {
            Enabled = true,
            ApplicationName = ApplicationName,
            ContactEmail = contact ?? string.Empty,
        };

        return Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            new List<ValidationResult>(),
            validateAllProperties: true);
    }

    private static JsonElement? Column(JsonElement recent, string property) =>
        recent.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array
            : null;

    private static string? TextAt(JsonElement? column, int index)
    {
        if (column is not { } array || index >= array.GetArrayLength())
        {
            return null;
        }

        var element = array[index];

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = element.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static DateOnly? DateAt(JsonElement column, int index)
    {
        var text = TextAt(column, index);

        return text is not null
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}

/// <summary>
/// Everything one run needs, supplied by its caller. Nothing is read from the environment here.
/// </summary>
/// <param name="Batch">The single-member batch being run.</param>
/// <param name="Authorization">The loaded declaration, with its counter where the caller left it.</param>
/// <param name="SealedCiks">The sealed universe's members, for the identity check.</param>
/// <param name="Source">The registry entry for <c>sec-edgar</c>, or null when it is unregistered.</param>
/// <param name="Provider">The connector, or null - which is also what a disabled connector looks like.</param>
/// <param name="ContactAddress">The fair-access contact. Checked, never recorded.</param>
/// <param name="AttemptNumber">Which approved attempt at this batch this is, from one.</param>
/// <param name="EarliestWindowStart">The earliest date this member's selection windows begin at.</param>
/// <param name="SelectionWindows">This member's breach reading windows.</param>
/// <param name="SharedForms">The forms every member may be read for.</param>
/// <param name="SecondaryForms">The extra forms one named member may be read for.</param>
/// <param name="SecondaryFormsCik">That member's CIK, or null when no member has extra scope.</param>
/// <param name="BurnedCorrelations">Correlations earlier recorded attempts already claimed.</param>
internal sealed record SecRunContext(
    SecEdgarSixMemberPartition.SecFilingBatchDefinition Batch,
    AcquisitionAuthorization Authorization,
    IReadOnlySet<string> SealedCiks,
    DataSource? Source,
    IDataProvider? Provider,
    string? ContactAddress,
    int AttemptNumber,
    DateOnly EarliestWindowStart,
    IReadOnlyList<(DateOnly From, DateOnly To)> SelectionWindows,
    IReadOnlyList<string> SharedForms,
    IReadOnlyList<string> SecondaryForms,
    string? SecondaryFormsCik,
    IReadOnlySet<string> BurnedCorrelations);

/// <summary>What reading the archived document established, once it had been fetched.</summary>
/// <param name="ArchivedHash">The content hash of the bytes read.</param>
/// <param name="RuleId">Why nothing could be read, or null when it was.</param>
/// <param name="Coverage">"covered" or "not proven", as the completeness rule states it.</param>
/// <param name="Verdict">The label the completeness rule returned. Never composed here.</param>
/// <param name="InlineFilings">How many dated filings the document carries inline.</param>
/// <param name="HoldsOlderFilings">Whether it refers to further files. Reported, never followed.</param>
/// <param name="RelevantFilings">Filings in scope and inside a selection window.</param>
/// <param name="RelevantAccessions">Which ones, by accession number.</param>
/// <param name="CoversWindow">Whether the window can be searched in this document at all.</param>
internal sealed record SecReading(
    string ArchivedHash,
    string? RuleId,
    string? Coverage,
    string? Verdict,
    int InlineFilings,
    bool HoldsOlderFilings,
    int RelevantFilings,
    IReadOnlyList<string> RelevantAccessions,
    bool CoversWindow);

/// <summary>
/// What one run did, in the terms the attempt artefact records and an operator reads.
/// </summary>
/// <remarks>
/// Immutable and complete: every field the artefact needs is here, so the record and the file cannot
/// disagree. Serialising it is a caller's job - this class opens no file, which is also what makes
/// "the runner cannot write a successor authorisation" a property a test can read off its source.
/// </remarks>
internal sealed record SecRunOutcome(
    string Status,
    string? RuleId,
    string? Reason,
    int BatchIndex,
    string Cik,
    string Symbol,
    int AttemptNumber,
    string Correlation,
    string? Fingerprint,
    bool RequestCarriedWindow,
    int ConsumedBefore,
    int ConsumedThisRun,
    int ConsumedTotal,
    int Remaining,
    bool Dispatched,
    string? RunOutcome,
    string? Diagnostic,
    int ObservationsRecorded,
    int PayloadsQuarantined,
    IReadOnlyList<string> FormsInScope,
    SecReading? Reading)
{
    public static SecRunOutcome Refused(
        SecEdgarSixMemberPartition.SecFilingBatchDefinition batch,
        int attempt,
        string correlation,
        string ruleId,
        string reason,
        int before,
        AcquisitionAuthorization authorization,
        IReadOnlyList<string> forms) =>
        new(SecFilingsBatchRunner.RefusedStatus, ruleId, reason, batch.Index, batch.Cik, batch.Symbol,
            attempt, correlation, null, false, before, authorization.Consumed - before,
            authorization.Consumed, authorization.Remaining, false, null, null, 0, 0, forms, null);

    public static SecRunOutcome Suppressed(
        SecEdgarSixMemberPartition.SecFilingBatchDefinition batch,
        int attempt,
        string correlation,
        string fingerprint,
        int before,
        AcquisitionAuthorization authorization,
        IReadOnlyList<string> forms) =>
        new(SecFilingsBatchRunner.SuppressedStatus, null,
            "the ledger already holds a successful run for this exact request; nothing was dispatched and nothing was consumed",
            batch.Index, batch.Cik, batch.Symbol, attempt, correlation, fingerprint, false, before,
            authorization.Consumed - before, authorization.Consumed, authorization.Remaining,
            false, null, null, 0, 0, forms, null);

    public static SecRunOutcome Threw(
        SecEdgarSixMemberPartition.SecFilingBatchDefinition batch,
        int attempt,
        string correlation,
        string fingerprint,
        string exceptionType,
        int before,
        AcquisitionAuthorization authorization,
        IReadOnlyList<string> forms) =>
        new(SecFilingsBatchRunner.ThrewStatus, null,
            "the acquisition threw after the unit was consumed; the unit is spent and is not credited back",
            batch.Index, batch.Cik, batch.Symbol, attempt, correlation, fingerprint, false, before,
            authorization.Consumed - before, authorization.Consumed, authorization.Remaining,
            true, null, exceptionType, 0, 0, forms, null);

    /// <remarks>
    /// Named <c>ForDispatch</c> rather than <c>Dispatched</c> because the record already carries a
    /// <see cref="Dispatched"/> property, and a factory may not share a name with a member of the
    /// type it builds.
    /// </remarks>
    public static SecRunOutcome ForDispatch(
        SecEdgarSixMemberPartition.SecFilingBatchDefinition batch,
        int attempt,
        string correlation,
        string fingerprint,
        int before,
        AcquisitionAuthorization authorization,
        IReadOnlyList<string> forms,
        AcquisitionResult result,
        SecReading? reading) =>
        new(SecFilingsBatchRunner.DispatchedStatus, result.Run.RefusalRuleId, result.Run.Reason,
            batch.Index, batch.Cik, batch.Symbol, attempt, correlation, fingerprint, false, before,
            authorization.Consumed - before, authorization.Consumed, authorization.Remaining,
            true, result.Run.Outcome.ToString(), null, result.ObservationsRecorded,
            result.Normalization?.PayloadsQuarantined ?? 0, forms, reading);

    /// <summary>The attempt artefact's content. A string, so this type still opens no file.</summary>
    public string ToArtefact(string evidenceBase, AcquisitionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        return JsonSerializer.Serialize(
            new
            {
                EvidenceBaseFingerprint = evidenceBase,
                Authorization = authorization.AuthorizationId,
                AuthorizationDigest = authorization.Digest,
                Source = SecFilingsBatchRunner.Source,
                Category = nameof(DataCategory.RegulatoryFilings),
                WindowFromUtc = "2021-09-01",
                WindowToUtc = "2026-08-31",
                RequestWindow = "(none - the connector declares supportsWindow=false)",
                BatchIndex,
                Cik,
                Symbol,
                AttemptNumber,
                Correlation,
                Fingerprint,
                Status,
                RuleId,
                Reason,
                RunOutcome,
                Diagnostic,
                Dispatched,
                AuthorizationConsumedBefore = ConsumedBefore,
                AuthorizationConsumedThisRun = ConsumedThisRun,
                AuthorizationConsumedTotal = ConsumedTotal,
                AuthorizationRemaining = Remaining,
                ObservationsRecorded,
                PayloadsQuarantined,
                FormsInScope,
                Reading,
            },
            Universe.Json) + "\n";
    }
}
