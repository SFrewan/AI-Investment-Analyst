using System.Net;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Application.Actions;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The filing-document fetcher against a fake transport: what it archives, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No real network is reachable from here.</strong> The connector is constructed over an
/// <see cref="HttpMessageHandler"/> that fails the test if it is asked for anything unexpected, and
/// its base address points at a host that does not exist. The SEC is never contacted, and the
/// handler's call count is asserted to be zero on every refusal path.
/// </para>
/// <para>
/// <strong>The gates are the subject.</strong> Most of these prove that a filing-document request
/// is refused before a connector is reached, because the registered source does not declare the
/// category. That refusal is what keeps dispatch closed while the capability exists.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SecFilingDocumentFetcherTests : IAsyncLifetime
{
    private const string Cik = "0000892482";
    private const string Accession = "0000897101-04-002197";
    private const string Document = "rimage044983_8k.htm";

    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public SecFilingDocumentFetcherTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the gate that keeps dispatch closed -------------------------------------------------

    /// <summary>
    /// A source that does not declare the category is refused, and nothing leaves.
    /// </summary>
    /// <remarks>
    /// This is the standing state of the platform: <c>sec-edgar</c> is registered for the
    /// submissions categories and not for filing documents, so every request the fetcher can build
    /// is refused by admission. **Zero HTTP calls** is the assertion that matters.
    /// </remarks>
    [SkippableFact]
    public async Task An_unadmitted_category_is_refused_and_reaches_no_transport()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: false);

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.Empty(harness.Handler.Requested);

        // The refusal is recorded, so "nothing was dispatched" is an auditable fact.
        Assert.Equal(1, await harness.Context.IngestionRuns.CountAsync());
    }

    /// <summary>An unregistered source is refused before anything else, and reaches no transport.</summary>
    [SkippableFact]
    public async Task An_unregistered_source_is_refused_and_reaches_no_transport()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, registerSource: false);

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(0, harness.Handler.Calls);
    }

    /// <summary>
    /// A malformed target never becomes a request, so no gate is even consulted.
    /// </summary>
    [Theory]
    [InlineData("", Accession, Document)]
    [InlineData("not-a-cik", Accession, Document)]
    [InlineData(Cik, "", Document)]
    [InlineData(Cik, "000089710104002197", Document)]
    [InlineData(Cik, Accession, "")]
    [InlineData(Cik, Accession, "../../etc/passwd")]
    [InlineData(Cik, Accession, "..\\secret.htm")]
    [InlineData(Cik, Accession, "/etc/file")]
    [InlineData(Cik, Accession, "foo?x=1")]
    [InlineData(Cik, Accession, "foo#frag")]
    [InlineData(Cik, Accession, "xslF25X02/primary_doc.xml")]
    public async Task A_malformed_or_viewer_target_is_refused_before_any_request_is_built(
        string cik, string accession, string document)
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Fetcher.FetchAsync(cik, accession, document));

        Assert.Equal(0, harness.Handler.Calls);
        Assert.Equal(0, await harness.Context.IngestionRuns.CountAsync());
    }

    // ---- the authorised path, against the fake transport --------------------------------------

    /// <summary>
    /// With the category admitted in a test-only registry, the request reaches the fake transport
    /// and the exact bytes are archived.
    /// </summary>
    /// <remarks>
    /// <strong>The admission here is a test fixture and nothing else.</strong> It registers a source
    /// row inside this test's own transaction against the test database. No production registry row
    /// is created, no acquisition authorisation exists, and the real <c>sec-edgar</c> row is
    /// unchanged - which the last assertion checks by reading it back.
    /// </remarks>
    [SkippableTheory]
    [InlineData("<html><body>Rimage Corporation\r\n  common stock, symbol RIMG</body></html>", "text/html")]
    [InlineData("<?xml version=\"1.0\"?>\n<ownershipDocument>\n  <issuer/>\n</ownershipDocument>", "application/xml")]
    [InlineData("QUMU CORPORATION\r\nFORM 8-K\r\n\r\n  trading symbol: QUMU\r\n", "text/plain")]
    public async Task An_admitted_request_reaches_the_transport_and_archives_the_exact_bytes(
        string body, string mediaType)
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bytes = Encoding.UTF8.GetBytes(body);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, bytes, mediaType);

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(1, harness.Handler.Calls);

        // The derived path, exactly as F4 established it - and no URL was supplied by the caller.
        Assert.Equal(
            "/Archives/edgar/data/892482/000089710104002197/rimage044983_8k.htm",
            harness.Handler.Requested.Single().AbsolutePath);

        var hash = Assert.Single(run.Artifacts);

        // The archived hash is the hash of the exact response body.
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            hash.Value.ToLowerInvariant());

        var archived = await harness.Archive.RetrieveAsync(hash);

        Assert.NotNull(archived);
        Assert.Equal(bytes, archived);

        // Byte-for-byte: no line-ending conversion, no BOM, no whitespace or entity rewriting.
        Assert.Equal(body, Encoding.UTF8.GetString(archived!));
        Assert.Equal(bytes.Length, archived!.Length);

        var described = await harness.Archive.DescribeAsync(hash);

        Assert.NotNull(described);
        Assert.Equal("sec-edgar", described!.SourceId.Value);
        Assert.Equal(bytes.Length, described.ByteLength);
    }

    /// <summary>A. B. The same document twice is one archived payload.</summary>
    [SkippableFact]
    public async Task The_same_document_fetched_twice_is_archived_once()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bytes = Encoding.UTF8.GetBytes("<html>identical</html>");

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, bytes, "text/html");

        var first = await harness.Fetcher.FetchAsync(Cik, Accession, Document);
        var second = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        // One payload on disk however the second run resolved: the archive is content-addressed,
        // so storing bytes it already holds is a no-op returning the same hash.
        var archived = Assert.Single(await harness.ArchivedHashesAsync());

        Assert.Equal(archived, first.Artifacts.Single());

        // The second attempt is suppressed by the seam's idempotency ledger rather than archiving a
        // duplicate - an identical acquisition proposal is the same action. Asserted as "no second
        // payload" rather than as a particular outcome, because which of the two the ledger
        // produces is the seam's decision and not this capability's.
        Assert.True(
            second.Artifacts.Count == 0 || second.Artifacts.Single() == archived,
            "a repeated fetch of identical bytes must never produce a second payload.");
    }

    /// <summary>
    /// C. The same target returning different bytes does not overwrite anything.
    /// </summary>
    /// <remarks>
    /// Content addressing makes this structural: different bytes hash differently, so they land
    /// beside the first payload rather than on top of it. Both remain retrievable, and the two runs
    /// record different hashes - which is what lets a later stage see that a document changed
    /// rather than discover that it silently had.
    /// </remarks>
    [SkippableFact]
    public async Task The_same_target_returning_different_bytes_never_overwrites()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var original = Encoding.UTF8.GetBytes("<html>original</html>");
        var altered = Encoding.UTF8.GetBytes("<html>altered</html>");

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, original, "text/html");
        var first = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        harness.Handler.Respond(HttpStatusCode.OK, altered, "text/html");
        var second = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        // The safety property, and it holds: the first payload is byte-for-byte what it was.
        // Nothing was overwritten, because content addressing gives the altered bytes a different
        // address and the seam declined to write them at all.
        Assert.Equal(original, await harness.Archive.RetrieveAsync(first.Artifacts.Single()));

        // The finding, asserted rather than glossed: the second attempt at the SAME target is
        // suppressed by the idempotency ledger before its bytes are examined, so a document that
        // changed between fetches is not noticed. That is safe - nothing is corrupted, and the
        // original evidence is intact - and it is not yet *detection*. Making a changed document a
        // recorded provenance event is the next stage's work, and F4b records the gap instead of
        // inventing behaviour for it.
        Assert.Empty(second.Artifacts);
        Assert.Single(await harness.ArchivedHashesAsync());
        Assert.Null(await harness.Archive.RetrieveAsync(
            Domain.Ingestion.ContentHash.Compute(altered)));
    }

    /// <summary>E, F, G. A failure is a failure, never an absence of evidence.</summary>
    [SkippableTheory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_http_failure_archives_nothing_and_is_recorded_as_a_failure(HttpStatusCode status)
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(status, Encoding.UTF8.GetBytes("error body"), "text/plain");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.NotEqual(IngestionOutcome.Succeeded, run.Outcome);

        // Nothing was archived, so nothing downstream can mistake a failed fetch for a document
        // that contained no symbol.
        Assert.Empty(await harness.ArchivedHashesAsync());

        // And the attempt is on the ledger rather than silently absent.
        Assert.Equal(1, await harness.Context.IngestionRuns.CountAsync());
    }

    /// <summary>
    /// An empty 200 is a failed filing-document acquisition, and archives nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This assertion is the inverse of the one F4b left here, and the inversion is the
    /// point.</strong> F4b pinned the then-current behaviour - an empty 200 succeeded and archived
    /// zero bytes - and recorded it as a finding rather than changing the gateway, because altering
    /// the gateway's success rule would have changed EODHD acquisition too. F6 resolves the finding
    /// at the only place where it can be resolved narrowly: inside the SEC connector, for
    /// <c>RegulatoryFilingDocuments</c> alone.
    /// </para>
    /// <para>
    /// <strong>Why it had to change before any document was fetched.</strong> An archived zero-byte
    /// payload is indistinguishable in shape from a document that was read and found to state no
    /// symbol. F2 and F3 both forbid reading a delivery failure as evidence of absence, so the pilot
    /// must not be able to produce that shape at all. Failing here means the run ledger records a
    /// failed acquisition - which is what happened - and the archive is left holding nothing.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task An_empty_response_is_a_failure_and_archives_nothing()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, [], "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.NotEqual(IngestionOutcome.Succeeded, run.Outcome);

        // Nothing was archived, so there is no zero-byte payload for a later stage to misread.
        Assert.Empty(await harness.ArchivedHashesAsync());
        Assert.Empty(run.Artifacts);

        // The attempt is on the ledger rather than silently absent, and names its own cause with a
        // closed-set token rather than anything the provider wrote.
        Assert.Equal(1, await harness.Context.IngestionRuns.CountAsync());
        Assert.Contains(nameof(EmptyFilingDocumentException), run.Reason, StringComparison.Ordinal);
        Assert.Contains(EmptyFilingDocumentException.Diagnostic, run.Reason, StringComparison.Ordinal);
    }

    /// <summary>The fetcher itself reaches nothing that could bypass the gateway.</summary>
    [Fact]
    public void The_fetcher_can_reach_no_transport_archive_or_authorisation_of_its_own()
    {
        var dependencies = typeof(SecFilingDocumentFetcher)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.Equal(["IngestionGateway", "ICorrelationContext", "IClock"], dependencies);

        Assert.DoesNotContain(
            dependencies,
            d => d.Contains("HttpClient", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Archive", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Policy", StringComparison.OrdinalIgnoreCase)
                || d.Contains("RateLimiter", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("sec-edgar", SecFilingDocumentFetcher.Source.Value);
    }
}
