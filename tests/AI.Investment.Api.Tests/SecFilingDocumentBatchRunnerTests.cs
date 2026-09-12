using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The filing-document runner's gate order, proven against fakes with no network anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here can reach the SEC.</strong> The acquisition service is a fake that records
/// what it was asked for, so every "would this have been dispatched" question is answered by
/// counting its calls rather than by making one. The real batch is exercised elsewhere, once,
/// behind its own door.
/// </para>
/// <para>
/// <strong>Every refusal is asserted by its rule id.</strong> A test that only checked "it refused"
/// would pass when the wrong gate fired, and the gate that fires is the whole contract: which one
/// refused says whether a unit was spent, and whether an operator should look at the approval, the
/// accounting, the scope or the clock.
/// </para>
/// </remarks>
public sealed class SecFilingDocumentBatchRunnerTests
{
    private const string Evidence = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";
    private const string Contact = "someone@example.invalid";
    private const string Cik = "0000892482";

    private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Batch 1's five, verbatim from the sealed declaration.</summary>
    private static IReadOnlyList<string> BatchOneTargets =>
        SecEdgarFilingDocumentPartition.TargetsFor(1);

    // ---- 1. one batch, and only one ---------------------------------------------------------------

    /// <summary>1. Batch 2 is unauthorised, so it refuses at the first gate and spends nothing.</summary>
    [Fact]
    public async Task An_unauthorised_batch_refuses_first_and_dispatches_nothing()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization, SecEdgarFilingDocumentPartition.Batches[1], TargetsFor(2)));

        Assert.Equal(SecFilingDocumentBatchRunner.RefusedStatus, outcome.Status);
        Assert.Equal(SecFilingDocumentBatchRunner.BatchAuthorisedRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>
    /// 1. And even approved, batch 2 refuses on accounting until batch 1 has actually been spent.
    /// </summary>
    /// <remarks>
    /// The second lock on "one batch at a time". Batch 2 was approved against five prior dispatches;
    /// with none made, the accounting has not reached it, and <c>runner.prior-consumption@1</c>
    /// refuses before the consumption boundary. Flipping a flag alone cannot run it out of order.
    /// </remarks>
    [Fact]
    public async Task An_approved_batch_two_still_refuses_until_batch_one_has_been_spent()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(
                authorization,
                SecEdgarFilingDocumentPartition.Batches[1] with { Authorised = true },
                TargetsFor(2)));

        Assert.Equal(SecFilingDocumentBatchRunner.PriorConsumptionRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>Batch 1, approved, dispatches exactly its five and consumes exactly five.</summary>
    [Fact]
    public async Task The_approved_first_batch_dispatches_exactly_its_five()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(Context(authorization));

        Assert.Equal(5, acquisition.Requests.Count);
        Assert.Equal(5, outcome.Dispatched);
        Assert.Equal(5, outcome.ConsumedThisRun);
        Assert.Equal(5, authorization.Consumed);
        Assert.Equal(25, authorization.Remaining);

        // Exactly the declaration's first five, and nothing else.
        Assert.Equal(
            BatchOneTargets,
            acquisition.Requests.Select(r => r.Subject.Identifier!).ToList());

        // Every request is the filing-document category, the right source, and carries no window.
        Assert.All(acquisition.Requests, r =>
        {
            Assert.Equal(DataCategory.RegulatoryFilingDocuments, r.Category);
            Assert.Equal(SecFilingDocumentBatchRunner.Source, r.SourceId.Value);
            Assert.Equal(FilingDocumentSubject.SubjectKind, r.Subject.Kind);
            Assert.Null(r.Window);
        });
    }

    // ---- 2-6. targets outside the batch -----------------------------------------------------------

    /// <summary>2, 4. A document of another member is refused before any request is built.</summary>
    [Fact]
    public async Task A_target_from_another_member_is_refused()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        // A genuine batch-2 target, smuggled into batch 1.
        var foreign = TargetsFor(2)[0];

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization, targets: [.. BatchOneTargets.Take(4), foreign]));

        Assert.Equal(SecFilingDocumentBatchRunner.MemberIdentityRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>3, 5, 6. A wildcard, a changed accession or a changed document is refused.</summary>
    /// <remarks>
    /// <para>
    /// The three ways a target could be nudged into naming something else, and each refuses before a
    /// request exists. A changed accession or document still starts with the authorised CIK, so the
    /// member check passes and the authorisation's own set membership is what catches it - which is
    /// the property that makes the scope thirty documents rather than six companies.
    /// </para>
    /// <para>
    /// The wildcard is refused earlier still, by the production parser, because <c>*</c> is not a
    /// character a document name may contain.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("0000892482|0001493152-23-003970|*", "wildcard document")]
    [InlineData("0000892482|0001493152-23-999999|form8-k.htm", "changed accession")]
    [InlineData("0000892482|0001493152-23-003970|form10-k.htm", "changed primary document")]
    [InlineData("0000892482|0001493152-23-003970|../../../etc/passwd", "traversal")]
    public async Task A_tampered_target_is_refused_and_never_requested(string tampered, string why)
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization, targets: [.. BatchOneTargets.Take(4), tampered]));

        // Either the member check or the authorisation's set membership refuses it. Both are
        // batch-level gates, so nothing in the batch is dispatched.
        Assert.Equal(SecFilingDocumentBatchRunner.MemberIdentityRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
        Assert.Contains("0000892482", outcome.Reason, StringComparison.Ordinal);

        _ = why;
    }

    /// <summary>14. A viewer-rendered path is refused with F4b's semantics, not rewritten.</summary>
    /// <remarks>
    /// The target is inside the authorised set for this test's purposes only in shape; what matters
    /// is that a path carrying a separator never becomes a request. F4 established that no 8-K in
    /// the pilot carries one, so this proves the refusal exists rather than that it is needed.
    /// </remarks>
    [Fact]
    public async Task A_viewer_rendered_path_is_refused_and_never_requested()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var viewer = "0000892482|0001493152-23-003970|xsl8-K/form8-k.htm";

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization, targets: [.. BatchOneTargets.Take(4), viewer]));

        Assert.Equal(SecFilingDocumentBatchRunner.MemberIdentityRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);

        // And the production parser is what would refuse it at the target gate too.
        Assert.False(FilingDocumentSubject.Parse(viewer).IsAccepted);
    }

    // ---- 7. expiry ---------------------------------------------------------------------------------

    /// <summary>7. An expired authorisation refuses before the consumption boundary.</summary>
    [Fact]
    public async Task An_expired_authorisation_refuses_and_spends_nothing()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        // A day after the sealed expiry.
        var runner = new SecFilingDocumentBatchRunner(
            acquisition, new FakeRunStore(), new FakeArchive(),
            new FixedClock(new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc)));

        var outcome = await runner.RunAsync(Context(authorization));

        Assert.Equal(SecFilingDocumentBatchRunner.AuthorisationExpiredRule, outcome.RuleId);
        Assert.Contains(AcquisitionAuthorization.ExpiredRule, outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    // ---- the declaration binding -------------------------------------------------------------------

    /// <summary>An authorisation bound to another declaration cannot run this batch.</summary>
    [Fact]
    public async Task An_authorisation_bound_to_another_declaration_is_refused()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization) with { PilotDeclarationPath = "declarations/some-other-pilot.json" });

        Assert.Equal(SecFilingDocumentBatchRunner.DeclarationBindingRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>A batch charged against another authorisation file is refused.</summary>
    [Fact]
    public async Task A_batch_charged_against_another_authorisation_is_refused()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization) with { AuthorizationFileName = "acquisition-eodhd-sample400.json" });

        Assert.Equal(SecFilingDocumentBatchRunner.SourceCategoryRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
    }

    // ---- admission ----------------------------------------------------------------------------------

    /// <summary>15. A source that does not admit the category refuses, with no request made.</summary>
    /// <remarks>
    /// This is the gate that kept dispatch closed for four stages, applied here in the runner's own
    /// preflight so the refusal costs nothing. The gateway would refuse it again; discovering it
    /// there would cost a unit, because that gate sits after consumption.
    /// </remarks>
    [Fact]
    public async Task A_source_that_does_not_admit_filing_documents_refuses_before_any_request()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization) with { Source = SourceWithout(DataCategory.RegulatoryFilingDocuments) });

        Assert.Equal(SecFilingDocumentBatchRunner.RefusedStatus, outcome.Targets[0].Status);
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>No connector registered means no dispatch, and no unit spent.</summary>
    [Fact]
    public async Task An_absent_connector_refuses_before_any_request()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization) with { Provider = null });

        Assert.All(outcome.Targets, t => Assert.Equal(SecFilingDocumentBatchRunner.RefusedStatus, t.Status));
        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>An absent or malformed fair-access contact refuses the whole batch.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public async Task An_unusable_deployment_identity_refuses_the_batch(string? contact)
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(
            Context(authorization) with { ContactAddress = contact });

        Assert.Equal(SecFilingDocumentBatchRunner.DeploymentIdentityRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);

        // The value is never recorded. Only meaningful for a non-blank one: every string
        // contains the empty string, so asserting it for an empty contact would pass without
        // testing anything at all.
        if (!string.IsNullOrWhiteSpace(contact))
        {
            Assert.DoesNotContain(contact, outcome.Reason ?? string.Empty, StringComparison.Ordinal);
        }

        // And the refusal names the variable rather than its contents, in every case.
        Assert.Contains(SecFilingDocumentBatchRunner.ContactVariable, outcome.Reason, StringComparison.Ordinal);
    }

    // ---- 10, 11, 12. idempotency and exact consumption ----------------------------------------------

    /// <summary>10. A target the ledger has already satisfied is suppressed and costs nothing.</summary>
    [Fact]
    public async Task An_already_satisfied_target_is_suppressed_and_consumes_nothing()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var runStore = new FakeRunStore { CompleteEverything = true };

        var runner = new SecFilingDocumentBatchRunner(
            acquisition, runStore, new FakeArchive(), new FixedClock(Now));

        var outcome = await runner.RunAsync(Context(authorization));

        Assert.Empty(acquisition.Requests);
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(5, authorization.Suppressed);
        Assert.All(outcome.Targets, t =>
            Assert.Equal(SecFilingDocumentBatchRunner.SuppressedStatus, t.Status));
    }

    /// <summary>11, 12. A failed fetch is still consumed; a refused one never is.</summary>
    /// <remarks>
    /// <strong>The distinction is what the ceiling means.</strong> A dispatched request consumes one
    /// whether it then succeeds or fails, because the call was made. A request refused before the
    /// boundary was never made, so charging it would exhaust an authorisation without fetching
    /// anything - the failure mode where a retried run runs out of budget it never spent.
    /// </remarks>
    [Fact]
    public async Task A_failed_fetch_is_consumed_and_a_refused_one_is_not()
    {
        var acquisition = new FakeAcquisition { FailEverything = true };
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(Context(authorization));

        // Five calls were made, so five units are spent, and none of them archived anything.
        Assert.Equal(5, acquisition.Requests.Count);
        Assert.Equal(5, authorization.Consumed);
        Assert.Equal(0, outcome.Archived);
        Assert.All(outcome.Targets, t => Assert.Null(t.ContentHash));
    }

    /// <summary>12. A throwing provider is recorded, not swallowed, and does not stop the batch.</summary>
    [Fact]
    public async Task A_throwing_provider_is_recorded_and_the_batch_continues()
    {
        var acquisition = new FakeAcquisition { ThrowOn = 2 };
        var authorization = Load();

        var outcome = await Runner(acquisition).RunAsync(Context(authorization));

        Assert.Equal(5, outcome.Targets.Count);
        Assert.Contains(outcome.Targets, t => t.Status == SecFilingDocumentBatchRunner.ThrewStatus);
        Assert.Equal(5, authorization.Consumed);
    }

    /// <summary>The ceiling is the ceiling, even for an approved batch.</summary>
    [Fact]
    public async Task A_batch_larger_than_the_remaining_ceiling_refuses()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        authorization.RecordPriorConsumption(27);

        var outcome = await Runner(acquisition).RunAsync(
            Context(
                authorization,
                SecEdgarFilingDocumentPartition.Batches[0] with
                {
                    Authorised = true,
                    ExpectedPriorConsumption = 27,
                }));

        Assert.Equal(SecFilingDocumentBatchRunner.DispatchCeilingRule, outcome.RuleId);
        Assert.Empty(acquisition.Requests);
    }

    // ---- 15. no discovery ---------------------------------------------------------------------------

    /// <summary>15. The runner asks for what it was given and never discovers a target.</summary>
    /// <remarks>
    /// Structural rather than behavioural: the runner has no way to list, search or follow anything.
    /// Its targets arrive in the context, its request builder takes one identifier, and the only
    /// thing it calls is the acquisition service with a request it built.
    /// </remarks>
    [Fact]
    public async Task The_runner_requests_only_what_it_was_handed()
    {
        var acquisition = new FakeAcquisition();
        var authorization = Load();

        var two = BatchOneTargets.Take(2).ToList();

        var outcome = await Runner(acquisition).RunAsync(
            Context(
                authorization,
                SecEdgarFilingDocumentPartition.Batches[0] with
                {
                    Authorised = true,
                    Count = 2,

                    // Nothing spent in this scenario, stated rather than inherited.
                    ExpectedPriorConsumption = 0,
                },
                two));

        Assert.Equal(2, acquisition.Requests.Count);
        Assert.Equal(two, acquisition.Requests.Select(r => r.Subject.Identifier!).ToList());
        Assert.Equal(2, outcome.Dispatched);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static SecFilingDocumentBatchRunner Runner(FakeAcquisition acquisition) =>
        new(acquisition, new FakeRunStore(), new FakeArchive(), new FixedClock(Now));

    private static AcquisitionAuthorization Load() =>
        AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarFilingDocumentPartition.Declaration),
            Evidence,
            Universe.RepositoryPath());

    private static IReadOnlyList<string> TargetsFor(int batch) =>
        SecEdgarFilingDocumentPartition.TargetsFor(batch);

    private static FilingDocumentRunContext Context(
        AcquisitionAuthorization authorization,
        SecEdgarFilingDocumentPartition.FilingDocumentBatchDefinition? batch = null,
        IReadOnlyList<string>? targets = null) =>
        new(
            // The premise of these tests is an authorisation with nothing spent, so the batch is
            // approved against nothing spent. Stated here rather than inherited from the partition,
            // whose batch 1 records what its last real approval was given against - five, after the
            // attempt-2 retry - and would otherwise refuse on runner.prior-consumption@1.
            batch ?? SecEdgarFilingDocumentPartition.Batches[0] with
            {
                Authorised = true,
                ExpectedPriorConsumption = 0,
            },
            targets ?? BatchOneTargets,
            authorization,
            SecEdgarFilingDocumentPartition.Declaration,
            SecEdgarFilingDocumentPartition.PilotDeclarationPath,
            ActiveSource(),
            new StubProvider(),
            Contact,
            AttemptNumber: 1,
            new HashSet<string>(StringComparer.Ordinal));

    private static DataSource ActiveSource() => SourceWith(
        DataCategory.RegulatoryFilings, DataCategory.RegulatoryFilingDocuments);

    private static DataSource SourceWithout(DataCategory category)
    {
        var categories = new[]
        {
            DataCategory.RegulatoryFilings, DataCategory.RegulatoryFilingDocuments,
        }.Where(c => c != category).ToArray();

        return SourceWith(categories);
    }

    private static DataSource SourceWith(params DataCategory[] categories)
    {
        var source = DataSource.Register(
            SourceId.Create(SecFilingDocumentBatchRunner.Source),
            "U.S. Securities and Exchange Commission - EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            categories,
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: true,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: SecEdgarSource.LicensingNotes,
                retention: RetentionLimit.Unlimited),
            VerificationPolicy.Authoritative,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "Built in memory. Registration records assessment, not permission.");

        source.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        return source;
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class StubProvider : IDataProvider
    {
        public SourceId SourceId => SecEdgarProvider.Id;

        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Create(
            [DataCategory.RegulatoryFilings, DataCategory.RegulatoryFilingDocuments],
            [Region.UnitedStates],
            ["Company", FilingDocumentSubject.SubjectKind],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(5));

        public Task<ProviderResponse> FetchAsync(
            IngestionRequest request,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The stub provider exists to satisfy the capability check. Nothing in these tests " +
                "reaches a connector, and a call arriving here would mean one did.");
    }

    /// <summary>Records every request rather than making one.</summary>
    private sealed class FakeAcquisition : IDataAcquisition
    {
        public List<IngestionRequest> Requests { get; } = [];

        public bool FailEverything { get; init; }

        public int? ThrowOn { get; init; }

        public Task<AcquisitionResult> AcquireAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (ThrowOn == Requests.Count)
            {
                throw new HttpRequestException("the fake transport was told to fail this one.");
            }

            var run = IngestionRun.Start(request, Now);

            if (FailEverything)
            {
                run.MarkFailed("FakeFailure during ingestion.", Now);

                return Task.FromResult(new AcquisitionResult(run, Normalization: null));
            }

            run.RecordArtifact(ContentHash.Compute(
                Encoding.UTF8.GetBytes("<html>" + request.Subject.Identifier + "</html>")));

            run.MarkSucceeded(Now);

            return Task.FromResult(new AcquisitionResult(run, Normalization: null));
        }
    }

    private sealed class FakeRunStore : IIngestionRunStore
    {
        public bool CompleteEverything { get; init; }

        public Task RecordAsync(IngestionRun run, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> HasCompletedAsync(
            string requestFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CompleteEverything);

        // The runner uses none of these. They throw rather than return a benign default, so a
        // future change that started reading the ledger here fails loudly instead of silently
        // seeing an empty one.
        public Task<IngestionRun?> GetLatestForSourceAsync(
            SourceId sourceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the runner does not read the ledger this way.");

        public Task<IngestionRun?> GetLatestSuccessfulForSourceAsync(
            SourceId sourceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the runner does not read the ledger this way.");

        public Task<IReadOnlyList<IngestionRun>> GetRecentAsync(
            DateTime sinceUtc, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the runner does not read the ledger this way.");
    }

    private sealed class FakeArchive : IRawResponseArchive
    {
        public Task<ContentHash> StoreAsync(
            SourceId sourceId,
            ReadOnlyMemory<byte> payload,
            string mediaType,
            DateTime retrievedAtUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ContentHash.Compute(payload.Span));

        public Task<byte[]?> RetrieveAsync(
            ContentHash hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<ArchivedPayload?> DescribeAsync(
            ContentHash hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArchivedPayload?>(new ArchivedPayload(
                SecEdgarProvider.Id,
                "text/html",
                Now,
                ByteLength: 42));

        public async IAsyncEnumerable<ContentHash> EnumerateAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            yield break;
        }

        public Task<bool> ExistsAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task DeleteAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("nothing in these tests deletes archived evidence.");
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
