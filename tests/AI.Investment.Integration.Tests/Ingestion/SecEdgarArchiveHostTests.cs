using System.Net;
using System.Text;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// Which EDGAR host each category is addressed against, and what happens when one answers badly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>EDGAR serves its JSON API and its document archive from different hosts.</strong>
/// <c>data.sec.gov</c> answers <c>submissions/…</c> and <c>api/xbrl/…</c>; the
/// <c>Archives/edgar/data/…</c> tree that holds the documents is on <c>www.sec.gov</c>. The first
/// filing-document batch built a correct path and pointed it at the API host, and five
/// authorisation units bought that discovery. These tests are what stop it recurring.
/// </para>
/// <para>
/// <strong>No request leaves this file.</strong> The harness pins two sentinel hosts, both under the
/// reserved <c>.invalid</c> TLD, which cannot resolve. Every URI below is read off the fake
/// transport's record of what it was asked for; the assertions about the real hosts are made against
/// <see cref="SecEdgarOptions"/>'s shipped defaults and composed in memory.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class SecEdgarArchiveHostTests : IAsyncLifetime
{
    private const string Cik = "0000892482";
    private const string Accession = "0001493152-23-003970";
    private const string Document = "form8-k.htm";

    private readonly PostgresFixture _fixture;

    public SecEdgarArchiveHostTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- 1, 2. routing: which base address each category resolves against ----------------------

    /// <summary>2. A filing document is addressed against the ARCHIVE host.</summary>
    /// <remarks>
    /// Asserted on the URI the transport was actually handed, not on a string the connector built,
    /// because the question is which base address won - and only the request knows that.
    /// </remarks>
    [SkippableFact]
    public async Task A_filing_document_is_requested_from_the_archive_host()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html");

        await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        var requested = Assert.Single(harness.Handler.Requested);

        Assert.Equal(
            SecHarness.ArchiveHost + "Archives/edgar/data/892482/000149315223003970/form8-k.htm",
            requested.ToString());

        // And emphatically NOT the JSON-API host.
        Assert.Equal(new Uri(SecHarness.ArchiveHost).Host, requested.Host);
        Assert.NotEqual(new Uri(SecHarness.DataHost).Host, requested.Host);
    }

    /// <summary>1. Every JSON-API category is still addressed against the DATA host.</summary>
    /// <remarks>
    /// The other half of the routing claim, and the one that would catch an over-broad fix. Three
    /// categories with three different path shapes, all resolving relatively against the client's
    /// base address exactly as they did before.
    /// </remarks>
    [SkippableTheory]
    [InlineData(nameof(DataCategory.CompanyProfile), "Company", Cik, "/submissions/CIK0000892482.json")]
    [InlineData(nameof(DataCategory.RegulatoryFilings), "Company", Cik, "/submissions/CIK0000892482.json")]
    [InlineData(nameof(DataCategory.FinancialStatements), "Company", Cik, "/api/xbrl/companyfacts/CIK0000892482.json")]
    [InlineData(nameof(DataCategory.MarketWideDisclosure), "Period", "us-gaap/Assets/USD/CY2021Q3I", "/api/xbrl/frames/us-gaap/Assets/USD/CY2021Q3I.json")]
    public async Task A_json_api_category_is_requested_from_the_data_host(
        string category,
        string subjectKind,
        string subject,
        string expectedPath)
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{}"), "application/json");

        var request = IngestionRequest.Create(
            SecEdgarProvider.Id,
            Enum.Parse<DataCategory>(category),
            Region.UnitedStates,
            IngestionSubject.Create(subjectKind, subject),
            CorrelationId.Create("f6i-" + category.ToLowerInvariant()),
            SecHarness.Now);

        await harness.Gateway.IngestAsync(request);

        var requested = Assert.Single(harness.Handler.Requested);

        Assert.Equal(new Uri(SecHarness.DataHost).Host, requested.Host);
        Assert.NotEqual(new Uri(SecHarness.ArchiveHost).Host, requested.Host);
        Assert.Equal(expectedPath, requested.AbsolutePath);
    }

    // ---- 2 (literal), 3, 4, 5. the composed URI against the shipped defaults --------------------

    /// <summary>
    /// 2, 3, 4, 5. With the shipped defaults, the sealed target composes to the required URI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The literal check the correction exists to satisfy, made in memory. <strong>Nothing is
    /// sent</strong>: the URI is composed exactly as the connector composes it - the archive base
    /// address, then <see cref="SecEdgarEndpoints.FilingDocument"/>'s path - and compared to the
    /// expected string.
    /// </para>
    /// <para>
    /// It also pins the two normalisations in one place: the CIK loses its leading zeros and the
    /// accession loses its dashes, while the primary document is carried verbatim.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_shipped_defaults_compose_the_required_archive_uri()
    {
        var options = new SecEdgarOptions();

        Assert.Equal("https://data.sec.gov/", options.BaseAddress);
        Assert.Equal("https://www.sec.gov/", options.ArchiveBaseAddress);

        var subject = FilingDocumentSubject.Parse($"{Cik}|{Accession}|{Document}").Subject!;

        // 3, 4, 5. The path segments, each asserted on its own.
        Assert.Equal("892482", subject.CikPathSegment);
        Assert.Equal("000149315223003970", subject.AccessionPathSegment);
        Assert.Equal(Document, subject.PrimaryDocument);

        var composed = new Uri(
            new Uri(options.ArchiveBaseAddress, UriKind.Absolute),
            SecEdgarEndpoints.FilingDocument(subject));

        Assert.Equal(
            "https://www.sec.gov/Archives/edgar/data/892482/000149315223003970/form8-k.htm",
            composed.ToString());
    }

    /// <summary>The archive base address is validated exactly as the data one is.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("www.sec.gov")]
    [InlineData("/Archives/")]
    [InlineData("http://www.sec.gov/")]
    public void A_malformed_archive_base_address_is_refused(string configured)
    {
        var options = new SecEdgarOptions
        {
            Enabled = true,
            ApplicationName = "AI-Investment-Analyst",
            ContactEmail = "someone@example.invalid",
            ArchiveBaseAddress = configured,
        };

        var results = options
            .Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options))
            .ToList();

        Assert.Contains(
            results,
            r => r.MemberNames.Contains(nameof(SecEdgarOptions.ArchiveBaseAddress)));
    }

    // ---- 9, B. a non-success response keeps its status -------------------------------------------

    /// <summary>
    /// 9. A non-success status reaches the ledger instead of being reduced to a type name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the observability half of the F6h finding.</strong> The first batch recorded
    /// five failures as <c>"HttpRequestException during ingestion."</c> and nothing more, because
    /// <c>EnsureSuccessStatusCode</c> throws a bare exception and the ledger records type names only.
    /// The status was on the exception and was discarded.
    /// </para>
    /// <para>
    /// <strong>The status is read from the response, never invented.</strong>
    /// <c>ProviderTransportException.Classify</c> builds a closed-set token from framework
    /// enumerations - no URL, no body, nothing the service wrote - which is what makes it safe in an
    /// append-only ledger.
    /// </para>
    /// </remarks>
    [SkippableTheory]
    [InlineData(HttpStatusCode.NotFound, "status:404")]
    [InlineData(HttpStatusCode.Forbidden, "status:403")]
    [InlineData(HttpStatusCode.TooManyRequests, "status:429")]
    [InlineData(HttpStatusCode.InternalServerError, "status:500")]
    public async Task A_non_success_status_is_preserved_in_the_ledger(HttpStatusCode status, string token)
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(status, Encoding.UTF8.GetBytes("error body"), "text/plain");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Failed, run.Outcome);
        Assert.Empty(await harness.ArchivedHashesAsync());

        // The closed-set token, and the type that carries it.
        Assert.Contains(token, run.Reason, StringComparison.Ordinal);
        Assert.Contains(nameof(ProviderTransportException), run.Reason, StringComparison.Ordinal);

        // And nothing the service wrote reached the ledger.
        Assert.DoesNotContain("error body", run.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("sec-archive.invalid", run.Reason, StringComparison.Ordinal);
    }

    // ---- A, C. the two outcomes either side of a success -----------------------------------------

    /// <summary>A. A successful document is passed through byte for byte.</summary>
    [SkippableFact]
    public async Task A_successful_archive_response_is_archived_unchanged()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bytes = Encoding.UTF8.GetBytes("<html>\r\n  <body>QUMU 8-K</body>\r\n</html>");

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, bytes, "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(bytes, await harness.Archive.RetrieveAsync(run.Artifacts.Single()));

        // From the archive host, and nowhere else.
        Assert.Equal(
            new Uri(SecHarness.ArchiveHost).Host,
            Assert.Single(harness.Handler.Requested).Host);
    }

    /// <summary>C, 8. An empty 200 from the archive host is still not a document.</summary>
    [SkippableFact]
    public async Task An_empty_archive_response_is_still_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        harness.Handler.Respond(HttpStatusCode.OK, [], "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Failed, run.Outcome);
        Assert.Empty(await harness.ArchivedHashesAsync());
        Assert.Contains(EmptyFilingDocumentException.Diagnostic, run.Reason, StringComparison.Ordinal);
    }

    // ---- 6, 7. refusals still happen before a host is ever chosen --------------------------------

    /// <summary>
    /// 6, 7. A malformed, viewer or traversing target is refused before any host is involved.
    /// </summary>
    /// <remarks>
    /// The refusals are the parser's and they run before a URI exists, so changing the host cannot
    /// have moved them. Re-proved here anyway, because "the fix did not weaken the refusals" is
    /// exactly the claim worth re-proving after touching the code that builds the request.
    /// </remarks>
    [SkippableTheory]
    [InlineData("xsl8-K/form8-k.htm")]
    [InlineData("../../../etc/passwd")]
    [InlineData("form8-k.htm?x=1")]
    [InlineData("form8-k.htm#top")]
    [InlineData("sub/dir/form8-k.htm")]
    public async Task A_refused_target_never_reaches_either_host(string document)
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(_fixture, admitFilingDocuments: true);

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Fetcher.FetchAsync(Cik, Accession, document));

        Assert.Empty(harness.Handler.Requested);
        Assert.Equal(0, harness.Handler.Calls);
    }
}
