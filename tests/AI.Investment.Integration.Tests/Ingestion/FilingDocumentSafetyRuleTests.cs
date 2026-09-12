using System.Net;
using System.Text;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The two safety rules F4b found missing, resolved and bounded.
/// </summary>
/// <remarks>
/// <para>
/// F4b exercised the filing-document path end to end against a fake transport and recorded two
/// findings rather than inventing behaviour for them: a successful response carrying no bytes was
/// archived as a document, and a re-fetch of one target was suppressed before its bytes could be
/// compared, so a changed document was never noticed. Both are resolved here, and both resolutions
/// are bounded by tests that fail if the fix reaches further than it should.
/// </para>
/// <para>
/// <strong>No real network is reachable from here.</strong> The transport fails the test if asked
/// for anything unconfigured and its base address is a host that does not exist. The SEC is never
/// contacted.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class FilingDocumentSafetyRuleTests : IAsyncLifetime
{
    private const string Cik = "0000892482";
    private const string Accession = "0000897101-04-002197";
    private const string Document = "rimage044983_8k.htm";

    private readonly PostgresFixture _fixture;

    public FilingDocumentSafetyRuleTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- finding 2: an empty 200 is not a document -------------------------------------------

    /// <summary>
    /// A filing document of zero bytes fails, is recorded, and leaves the archive empty.
    /// </summary>
    /// <remarks>
    /// The failure is what makes the archive safe to read later. An archived zero-byte payload has
    /// exactly the shape of a document that was retrieved and found to state no symbol, and F2 and
    /// F3 both forbid reading a delivery failure that way. Refusing at the connector means the
    /// ledger records what actually happened - a failed acquisition - and there is no payload for a
    /// later stage to misread.
    /// </remarks>
    [SkippableFact]
    public async Task A_zero_byte_filing_document_fails_and_archives_nothing()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, [], "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Failed, run.Outcome);
        Assert.Empty(run.Artifacts);
        Assert.Empty(await harness.ArchivedHashesAsync());

        // Recorded, not silent: a fetch that vanished without a ledger row is indistinguishable
        // from one that was never attempted.
        Assert.Equal(1, await harness.Context.IngestionRuns.CountAsync());

        // And the reason names a closed-set token rather than anything the provider wrote.
        Assert.Contains(EmptyFilingDocumentException.Diagnostic, run.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is about zero bytes, not about a document being small.
    /// </summary>
    /// <remarks>
    /// A length threshold would be a judgement about content, and this rule deliberately makes
    /// none: one byte is a document the archive must keep exactly as received, whatever a reader
    /// later concludes about it. Asserting the boundary keeps a future "surely this is too short to
    /// be real" from being added without a decision.
    /// </remarks>
    [SkippableFact]
    public async Task A_single_byte_filing_document_is_accepted_unchanged()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        byte[] body = [0x41];

        harness.Handler.Respond(HttpStatusCode.OK, body, "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(body, await harness.Archive.RetrieveAsync(run.Artifacts.Single()));
    }

    /// <summary>
    /// The rule reaches filing documents and stops there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the test that bounds the fix.</strong> The brief required the empty-payload
    /// behaviour to change for SEC filing documents only, and the danger in a rule like this is
    /// that it quietly becomes the platform's general definition of success. An empty response from
    /// a price or splits feed legitimately means "nothing in this window", and turning that into a
    /// failure would break EODHD acquisition while looking like a safety improvement.
    /// </para>
    /// <para>
    /// The same connector, the same gateway and the same empty body are used here, with only the
    /// category changed, so nothing but the category can account for the difference in outcome.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_empty_response_for_another_category_still_succeeds()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, [], "application/json");

        var request = IngestionRequest.Create(
            SecEdgarProvider.Id,
            DataCategory.CompanyProfile,
            Region.UnitedStates,
            IngestionSubject.Create("Company", Cik),
            CorrelationId.Create("f6-other-category"),
            SecHarness.Now);

        var run = await harness.Gateway.IngestAsync(request);

        // Unchanged behaviour, and deliberately so.
        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Single(run.Artifacts);
        Assert.Empty(await harness.Archive.RetrieveAsync(run.Artifacts.Single()) ?? [1]);
    }

    // ---- finding 1: a changed document is observable -----------------------------------------

    /// <summary>
    /// Re-observing a target as a new acquisition act archives the changed bytes beside the
    /// original, and the two runs name different artifacts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>F4b's finding was a consequence of the idempotency contract, not a defect in
    /// it.</strong> The seam's key is the request fingerprint scoped to the correlation. Two
    /// fetches of one target inside one correlation are one act performed twice, and suppressing
    /// the second is correct. F4b's harness fetched twice under a single fixed correlation, so the
    /// suppression it observed was the seam doing its job - which is why nothing in the gateway is
    /// changed here.
    /// </para>
    /// <para>
    /// <strong>Re-observation is a different act and is allowed to happen.</strong> Under a new
    /// correlation the request reaches the connector, the altered bytes are archived at their own
    /// content address, and the original remains byte-for-byte retrievable. Detection then needs no
    /// new mechanism: the two runs carry the same request fingerprint and different artifact
    /// hashes, which is a comparison over records the ledger already keeps.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task A_changed_document_re_observed_is_archived_beside_the_original()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var original = Encoding.UTF8.GetBytes("<html>original</html>");
        var altered = Encoding.UTF8.GetBytes("<html>altered</html>");

        var correlation = new MutableCorrelation("f6-observation-1");

        await using var harness = await SecHarness.CreateAsync(
            _fixture,
            admitFilingDocuments: true,
            correlation: correlation);

        harness.Handler.Respond(HttpStatusCode.OK, original, "text/html");
        var first = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        correlation.MoveTo("f6-observation-2");

        harness.Handler.Respond(HttpStatusCode.OK, altered, "text/html");
        var second = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Succeeded, first.Outcome);
        Assert.Equal(IngestionOutcome.Succeeded, second.Outcome);

        // Both payloads exist. Content addressing put the altered bytes at their own address
        // rather than on top of the original, so nothing was overwritten.
        Assert.Equal(2, (await harness.ArchivedHashesAsync()).Count);
        Assert.Equal(original, await harness.Archive.RetrieveAsync(first.Artifacts.Single()));
        Assert.Equal(altered, await harness.Archive.RetrieveAsync(second.Artifacts.Single()));

        // The change is observable: same request, different artifact. This is the detection F4b
        // recorded as missing, and it needs no new table and no new comparison step.
        Assert.Equal(first.Request.Fingerprint(), second.Request.Fingerprint());
        Assert.NotEqual(first.Artifacts.Single(), second.Artifacts.Single());
    }

    /// <summary>
    /// Re-observing an unchanged document produces no change signal.
    /// </summary>
    /// <remarks>
    /// The other half of detection, and the half that makes it usable. A comparison that reported a
    /// difference every time would be no more informative than one that never did: identical bytes
    /// must reach the identical address, so a differing artifact hash means the document actually
    /// differs.
    /// </remarks>
    [SkippableFact]
    public async Task An_unchanged_document_re_observed_reports_the_same_artifact()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bytes = Encoding.UTF8.GetBytes("<html>identical</html>");

        var correlation = new MutableCorrelation("f6-stable-1");

        await using var harness = await SecHarness.CreateAsync(
            _fixture,
            admitFilingDocuments: true,
            correlation: correlation);

        harness.Handler.Respond(HttpStatusCode.OK, bytes, "text/html");

        var first = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        correlation.MoveTo("f6-stable-2");

        var second = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(first.Artifacts.Single(), second.Artifacts.Single());
        Assert.Single(await harness.ArchivedHashesAsync());
    }

    /// <summary>
    /// Repeating one acquisition act is still suppressed, and that contract is unchanged.
    /// </summary>
    /// <remarks>
    /// Pinned deliberately. The resolution of the changed-document finding is that re-observation
    /// is a new act, and that reasoning only holds while repeating the same act remains
    /// deduplicated. If this stopped being true, a retried batch would re-dispatch every request it
    /// had already made - which is the failure the idempotency key exists to prevent, and which
    /// would matter a great deal on a metered vendor.
    /// </remarks>
    [SkippableFact]
    public async Task A_repeat_within_one_acquisition_act_is_still_suppressed()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bytes = Encoding.UTF8.GetBytes("<html>identical</html>");

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, bytes, "text/html");

        await harness.Fetcher.FetchAsync(Cik, Accession, Document);
        var callsAfterFirst = harness.Handler.Calls;

        var second = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Empty(second.Artifacts);
        Assert.Equal(callsAfterFirst, harness.Handler.Calls);
        Assert.Single(await harness.ArchivedHashesAsync());
    }

    // ---- the gate that still keeps dispatch closed -------------------------------------------

    /// <summary>
    /// The safety fixes opened nothing. Admission was opened later, by F6g, as its own act.
    /// </summary>
    /// <remarks>
    /// F6 asserted that its two safety fixes did not move the gate, and they did not - the gate moved
    /// several stages later, once a declaration had been sealed, an authorisation bound to it, and a
    /// partition cut with every batch unapproved. What this file still owns is that the empty-payload
    /// and changed-document rules are unaffected by admission being open, which the tests above
    /// assert directly. Exactly one category was added.
    /// </remarks>
    [Fact]
    public void The_registered_sec_source_admits_filing_documents_and_nothing_else_new()
    {
        var definition = new SecEdgarSource().Definition(SecHarness.Now);

        Assert.Equal(
            [
                DataCategory.RegulatoryFilings,
                DataCategory.CompanyProfile,
                DataCategory.FinancialStatements,
                DataCategory.EarningsDisclosure,
                DataCategory.MarketWideDisclosure,
                DataCategory.RegulatoryFilingDocuments,
            ],
            definition.Categories);
    }
}
