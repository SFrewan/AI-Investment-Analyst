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
/// A SEC-shaped acquisition harness over a transport that cannot reach the network.
/// </summary>
internal sealed class SecHarness : IAsyncDisposable
{
    /// <summary>The JSON-API host this harness pretends to be. Deliberately unresolvable.</summary>
    internal const string DataHost = "https://sec.invalid/";

    /// <summary>The archive host this harness pretends to be. Also deliberately unresolvable.</summary>
    /// <remarks>
    /// Distinct from <see cref="DataHost"/> on purpose: with two sentinels a test can prove WHICH
    /// base address a category resolved against, which is the property the archive-host correction
    /// turns on. Both sit under the reserved <c>.invalid</c> TLD, which cannot resolve, so a request
    /// that somehow escaped the fake transport would fail rather than reach anybody.
    /// </remarks>
    internal const string ArchiveHost = "https://sec-archive.invalid/";
    /// <summary>
    /// The fixed instant every harness clock reports. Held here rather than in a test class so
    /// that two test classes sharing this harness cannot drift apart on what "now" is.
    /// </summary>
    internal static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _archiveRoot;

    private SecHarness(
        AppDbContext context,
        ScopedWriteAuthorization authorization,
        FakeSecTransport handler,
        FileSystemRawResponseArchive archive,
        IFilingDocumentFetcher fetcher,
        IIngestionGateway gateway,
        string archiveRoot)
    {
        Gateway = gateway;
        Context = context;
        Authorization = authorization;
        Handler = handler;
        Archive = archive;
        Fetcher = fetcher;
        _archiveRoot = archiveRoot;
    }

    internal AppDbContext Context { get; }

    internal ScopedWriteAuthorization Authorization { get; }

    internal FakeSecTransport Handler { get; }

    internal FileSystemRawResponseArchive Archive { get; }

    internal IFilingDocumentFetcher Fetcher { get; }

    /// <summary>
    /// The same gateway the fetcher dispatches through.
    /// </summary>
    /// <remarks>
    /// Exposed so a test can send a request for a category the filing-document fetcher does not
    /// serve. Proving that a rule applies only to filing documents needs a non-filing-document
    /// request through the identical path, and there is no other way to make one.
    /// </remarks>
    internal IIngestionGateway Gateway { get; }

    /// <summary>
    /// Builds the harness. <paramref name="correlation"/> is the acquisition act this harness
    /// performs its fetches under.
    /// </summary>
    /// <remarks>
    /// It defaults to a fixed correlation, which is what most tests want. It is a parameter because
    /// the seam's idempotency key is the request fingerprint scoped to the correlation, so two
    /// fetches of one target under one correlation are the same act and deduplicate, while two
    /// fetches under different correlations are two acts and both happen. A test about re-observing
    /// a document over time has to be able to say which of those it means.
    /// </remarks>
    internal static async Task<SecHarness> CreateAsync(
        PostgresFixture fixture,
        bool registerSource = true,
        bool admitFilingDocuments = false,
        ICorrelationContext? correlation = null,
        IPolicyEngine? policyEngine = null,
        IPolicyContextProvider? policyContext = null)
    {
        var authorization = new ScopedWriteAuthorization();
        var context = fixture.CreateContext(authorization);
        var clock = new FixedUtcClock(Now);

        var archiveRoot = Path.Combine(Path.GetTempPath(), "f4b-" + Guid.NewGuid().ToString("N"));

        var archive = new FileSystemRawResponseArchive(
            Options.Create(new RawArchiveOptions { RootPath = archiveRoot }));

        var handler = new FakeSecTransport();

        var provider = new SecEdgarProvider(
            new HttpClient(handler) { BaseAddress = new Uri(DataHost) },
            Options.Create(new SecEdgarOptions
            {
                ApplicationName = "AI Investment Analyst Tests",
                ContactEmail = "tests@example.invalid",
                BaseAddress = DataHost,
                ArchiveBaseAddress = ArchiveHost,
            }),
            clock);

        var gateway = new IngestionGateway(
            new EfSourceRegistry(context),
            new ProviderCatalogue([provider]),
            new SlidingWindowRateLimiter(),
            archive,
            new EfIngestionRunStore(context),
            new ActionGateway(
                policyEngine ?? new PermissivePolicyEngine(),
                policyContext ?? new PermissiveContext(),
                new EfAuditSink(context),
                new EfIdempotencyStore(context),
                new EfActionExecutionStore(context),
                authorization,
                clock),
            clock);

        if (registerSource)
        {
            await SeedSourceAsync(context, authorization, admitFilingDocuments);
        }

        return new SecHarness(
            context,
            authorization,
            handler,
            archive,
            new SecFilingDocumentFetcher(gateway, correlation ?? new FixedCorrelation(), clock),
            gateway,
            archiveRoot);
    }

    /// <summary>
    /// Registers sec-edgar for this test only, optionally admitting filing documents.
    /// </summary>
    /// <remarks>
    /// A fixture row in the test database, truncated between tests. It grants nothing in
    /// production: the real registry row is untouched, and a test asserts that directly.
    /// </remarks>
    private static async Task SeedSourceAsync(
        AppDbContext context,
        ScopedWriteAuthorization authorization,
        bool admitFilingDocuments)
    {
        List<DataCategory> categories =
        [
            DataCategory.RegulatoryFilings,
            DataCategory.CompanyProfile,
            DataCategory.FinancialStatements,
            DataCategory.EarningsDisclosure,
            DataCategory.MarketWideDisclosure,
        ];

        if (admitFilingDocuments)
        {
            categories.Add(DataCategory.RegulatoryFilingDocuments);
        }

        var source = DataSource.Register(
            SecEdgarProvider.Id,
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
            Now);

        source.Activate(Now);

        context.DataSources.Add(source);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await context.SaveChangesAsync();
        }
    }

    internal async Task<IReadOnlyList<ContentHash>> ArchivedHashesAsync()
    {
        var hashes = new List<ContentHash>();

        await foreach (var hash in Archive.EnumerateAsync())
        {
            hashes.Add(hash);
        }

        return hashes;
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();

        if (Directory.Exists(_archiveRoot))
        {
            Directory.Delete(_archiveRoot, recursive: true);
        }
    }
}

/// <summary>
/// A transport that answers from a fixture and can reach nothing.
/// </summary>
/// <remarks>
/// It never opens a socket. Asked for something no fixture describes, it fails the test rather
/// than returning a plausible answer - a fake that invents a 404 hides a connector asking for
/// the wrong path.
/// </remarks>
internal sealed class FakeSecTransport : HttpMessageHandler
{
    private byte[] _body = [];
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _mediaType = "text/html";
    private bool _configured;

    internal int Calls { get; private set; }

    internal List<Uri> Requested { get; } = [];

    internal void Respond(HttpStatusCode status, byte[] body, string mediaType)
    {
        _status = status;
        _body = body;
        _mediaType = mediaType;
        _configured = true;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;
        Requested.Add(request.RequestUri!);

        Assert.True(
            _configured,
            $"The connector requested {request.RequestUri} with no fixture configured. A fake "
                + "that answered anyway would hide a connector asking for the wrong path.");

        // The SEC's fair-access policy requires identification on every request.
        Assert.NotNull(request.Headers.UserAgent);
        Assert.NotEmpty(request.Headers.GetValues("User-Agent"));

        var content = new ByteArrayContent(_body);

        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);

        return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
    }
}

internal sealed class FixedUtcClock : IClock
{
    internal FixedUtcClock(DateTime now) => UtcNow = now;

    public DateTime UtcNow { get; }
}

internal sealed class FixedCorrelation : ICorrelationContext
{
    public CorrelationId Current { get; } = CorrelationId.Create("f4b-filing-document");
}

/// <summary>
/// A correlation that can be moved on, modelling a second deliberate acquisition act.
/// </summary>
/// <remarks>
/// <strong>Not a way around the idempotency key.</strong> The seam deduplicates on the request
/// fingerprint scoped to the correlation precisely so that repeating one act is suppressed while
/// performing a new act is allowed. Re-observing a filing document weeks later is a new act, and
/// this is how a test says so; calling the same target twice inside one correlation is not, and a
/// test asserts that it is still suppressed.
/// </remarks>
internal sealed class MutableCorrelation : ICorrelationContext
{
    public MutableCorrelation(string initial) => Current = CorrelationId.Create(initial);

    public CorrelationId Current { get; private set; }

    internal void MoveTo(string next) => Current = CorrelationId.Create(next);
}

internal sealed class PermissivePolicyEngine : IPolicyEngine
{
    public PolicyDecision Evaluate(
        ActionProposal proposal,
        PolicyContext context,
        DateTime nowUtc) =>
        PolicyDecision.Execute(proposal, "permitted for the test", ["test.permits@1"], nowUtc);
}

/// <summary>
/// A policy context a test can set the kill switch and capability state on.
/// </summary>
/// <remarks>
/// Used with the REAL <c>PolicyEngine</c> rather than a permissive stub, because the claim being
/// tested is that the engine refuses - and a stub that refused would only prove the stub refused.
/// </remarks>
internal sealed class ConfigurableContext : IPolicyContextProvider
{
    public ConfigurableContext(KillSwitchState killSwitch, bool ingestionEnabled = true)
    {
        KillSwitch = killSwitch;
        IngestionEnabled = ingestionEnabled;
    }

    public KillSwitchState KillSwitch { get; }

    public bool IngestionEnabled { get; }

    public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PolicyContext.Create(
            "Integration",
            KillSwitch,
            [CapabilityPolicy.Create(Capability.DataIngestion, IngestionEnabled, RiskTier.High)]));
}

internal sealed class PermissiveContext : IPolicyContextProvider
{
    public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(PolicyContext.Create(
            "Integration",
            KillSwitchState.Disengaged,
            [CapabilityPolicy.Create(Capability.DataIngestion, enabled: true, RiskTier.High)]));
}
