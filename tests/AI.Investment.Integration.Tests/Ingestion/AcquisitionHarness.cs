using System.Net;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Options;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The real acquisition stack, on one scoped context, with the network replaced and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Every collaborator below is the production one: the real EODHD connectors building real URLs,
/// the real normalisers, the real EF stores against real PostgreSQL, the real
/// <see cref="ActionGateway"/> with the real idempotency store and audit sink, the real archive on
/// disk. Only two things are substituted, and both are substituted so the test is deterministic
/// rather than to make it pass:
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>The HTTP transport.</strong> A handler that answers from fixtures, so a proof about
/// corporate actions does not depend on what a vendor happens to return today.
/// </description></item>
/// <item><description>
/// <strong>The policy engine.</strong> A stub that permits, because these tests are about what the
/// ledger and the idempotency claim do to each other, not about what policy decides. Policy has
/// its own suite and is exercised there against every rule; wiring the real engine here would make
/// these tests fail whenever safety configuration changed, which would teach nobody anything.
/// </description></item>
/// </list>
/// <para>
/// <strong>One context, deliberately.</strong> Production resolves these from a single scope, so
/// the harness does too - that shared change tracker is the thing that failed in Block 2B, and a
/// harness that gave each store its own context would prove the opposite of what is being asserted.
/// </para>
/// </remarks>
internal sealed class AcquisitionHarness : IDisposable
{
    internal const string ApiKey = "test-key-not-a-real-credential";

    private readonly string _archiveRoot;

    private AcquisitionHarness(
        AppDbContext context,
        ScopedWriteAuthorization authorization,
        string archiveRoot,
        DateTime now)
    {
        Context = context;
        Authorization = authorization;
        _archiveRoot = archiveRoot;
        Now = now;

        var clock = new FixedClock(now);
        var options = Options.Create(EodhdOptionsFor());

        Handler = new RoutingHandler();

        Archive = new FileSystemRawResponseArchive(
            Options.Create(new RawArchiveOptions { RootPath = archiveRoot }));

        Runs = new EfIngestionRunStore(context);
        Observations = new EfObservationStore(context);

        Actions = new ActionGateway(
            new PermittingPolicyEngine(),
            new PermissiveContextProvider(),
            new EfAuditSink(context),
            new EfIdempotencyStore(context),
            new EfActionExecutionStore(context),
            authorization,
            clock);

        var prices = new EodhdProvider(
            new HttpClient(Handler) { BaseAddress = new Uri("https://eodhd.test/") },
            options,
            clock);

        var splits = new EodhdSplitsProvider(
            new HttpClient(Handler) { BaseAddress = new Uri("https://eodhd.test/") },
            options,
            clock);

        Ingestion = new IngestionGateway(
            new EfSourceRegistry(context),
            new ProviderCatalogue([prices, splits]),
            new SlidingWindowRateLimiter(),
            Archive,
            Runs,
            Actions,
            clock);

        Acquisition = new DataAcquisitionService(
            Ingestion,
            new NormalizationPipeline(
                Archive,
                [new EodhdDailyPriceNormalizer(options), new EodhdSplitsNormalizer(options)],
                Observations,
                new EfQuarantineStore(context),
                Actions,
                clock));
    }

    internal AppDbContext Context { get; }

    internal ScopedWriteAuthorization Authorization { get; }

    internal DateTime Now { get; }

    internal RoutingHandler Handler { get; }

    internal FileSystemRawResponseArchive Archive { get; }

    internal EfIngestionRunStore Runs { get; }

    internal EfObservationStore Observations { get; }

    internal ActionGateway Actions { get; }

    internal IngestionGateway Ingestion { get; }

    internal IDataAcquisition Acquisition { get; }

    /// <summary>Builds the stack and registers and activates both EODHD sources.</summary>
    internal static async Task<AcquisitionHarness> StartAsync(PostgresFixture fixture, DateTime now)
    {
        var authorization = new ScopedWriteAuthorization();

        var harness = new AcquisitionHarness(
            fixture.CreateContext(authorization),
            authorization,
            Path.Combine(Path.GetTempPath(), "aiinv-archive-" + Guid.NewGuid().ToString("N")),
            now);

        await harness.RegisterSourcesAsync().ConfigureAwait(false);

        return harness;
    }

    /// <summary>
    /// Puts both connectors in the registry, active, through the ordinary domain path.
    /// </summary>
    /// <remarks>
    /// A source registry row is domain state rather than seam bookkeeping, so this needs an open
    /// authorisation window - which is itself worth exercising here, because a test that could seed
    /// the registry without one would not be testing the same guard production runs under.
    /// </remarks>
    private async Task RegisterSourcesAsync()
    {
        var options = Options.Create(EodhdOptionsFor());
        var registry = new EfSourceRegistry(Context);

        foreach (ISourceDefinition definition in
                 new ISourceDefinition[] { new EodhdSource(options), new EodhdSplitsSource(options) })
        {
            var source = definition.Definition(Now);

            source.Activate(Now);

            registry.Add(source);
        }

        using (Authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await Context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The connector configuration, stated rather than defaulted.
    /// </summary>
    /// <remarks>
    /// <see cref="ExchangeSessionOptions.SessionCloseUtc"/> in particular: it is what a split's
    /// instant is built from, and leaving it at zero would place every split at midnight rather
    /// than at the close - restating one session too many, every time.
    /// </remarks>
    internal static EodhdOptions EodhdOptionsFor() => new()
    {
        Enabled = true,
        ApiKey = ApiKey,
        BaseAddress = "https://eodhd.test/",
        MaxRequestsPerMinute = 60,
        LicensingNotes =
            "Test subscription. Storage and automated processing are permitted for internal " +
            "analysis; redistribution is not.",
        RedistributionAllowed = false,
        Exchanges =
        [
            new ExchangeSessionOptions
            {
                Code = "US",
                SessionCloseUtc = TimeSpan.FromHours(20),
                PublicationDelay = TimeSpan.FromHours(4),
            },
        ],
    };

    public void Dispose()
    {
        Context.Dispose();

        if (Directory.Exists(_archiveRoot))
        {
            Directory.Delete(_archiveRoot, recursive: true);
        }
    }

    /// <summary>Answers the price and splits endpoints from fixtures, and counts the calls.</summary>
    internal sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _prices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _splits = new(StringComparer.Ordinal);

        internal int PriceCalls { get; private set; }

        internal int SplitCalls { get; private set; }

        internal List<Uri> Requested { get; } = [];

        internal RoutingHandler WithPrices(string symbol, string document)
        {
            _prices[symbol] = document;

            return this;
        }

        internal RoutingHandler WithSplits(string symbol, string document)
        {
            _splits[symbol] = document;

            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var uri = request.RequestUri!;

            Requested.Add(uri);

            var path = uri.AbsolutePath;
            var symbol = path[(path.LastIndexOf('/') + 1)..];

            string? document;

            if (path.Contains("/api/splits/", StringComparison.Ordinal))
            {
                SplitCalls++;
                document = _splits.TryGetValue(symbol, out var found) ? found : "[]";
            }
            else
            {
                PriceCalls++;
                document = _prices.TryGetValue(symbol, out var found) ? found : null;
            }

            if (document is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("no fixture for " + symbol),
                });
            }

            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(document));

            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(EodhdProvider.MediaType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FixedClock : IClock
    {
        internal FixedClock(DateTime now) => UtcNow = now;

        public DateTime UtcNow { get; }
    }

    /// <summary>Permits, so the assertions are about the ledger rather than about policy.</summary>
    private sealed class PermittingPolicyEngine : IPolicyEngine
    {
        public PolicyDecision Evaluate(ActionProposal proposal, PolicyContext context, DateTime nowUtc) =>
            PolicyDecision.Execute(proposal, "permitted for the test", ["test.permits@1"], nowUtc);
    }

    private sealed class PermissiveContextProvider : IPolicyContextProvider
    {
        public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyContext.Create(
                "Integration",
                KillSwitchState.Disengaged,
                [CapabilityPolicy.Create(Capability.DataIngestion, enabled: true, RiskTier.High)]));
    }
}
