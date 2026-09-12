using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// Per-run provenance, written through the real seam into a real PostgreSQL database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the test that was missing, and its absence cost a live provider request.</strong>
/// Eighteen focused tests exercised the gateway against an in-memory
/// <c>IProviderExchangeStore</c>, and a composition test proved the container injects the EF one.
/// Neither could see the defect, because the defect was in <c>AppDbContext.GuardWrites</c>:
/// <c>ProviderExchange</c> was not classified as seam bookkeeping, so its insert was refused with
/// "Pending changes without an authorised execution: ProviderExchange:Added" the moment it was
/// attempted outside an authorisation window - which is exactly where
/// <c>IngestionGateway.RecordExchangesAsync</c> attempts it. The live pilot fetched real data,
/// archived it, recorded the run, and stored no provenance at all.
/// </para>
/// <para>
/// So the seam here is real: a real <see cref="ActionGateway"/> opening and closing a real
/// authorisation window around the effect, a real <c>AppDbContext</c> with its guard armed, real EF
/// stores, a real content-addressed archive on disk and the real EODHD price normaliser. The only
/// fake is the transport - <see cref="StubPriceProvider"/> returns canned bytes and touches no
/// network, which is the one thing a test must never do for real.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class ProviderExchangePersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowFrom = new(2023, 2, 13, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2023, 2, 14, 0, 0, 0, DateTimeKind.Utc);

    private const string SourceIdValue = "eodhd-eod";
    private const string Symbol = "VLDR.US";
    private const string SubjectKind = "Security";
    private const string EndpointTemplate = "api/eod/{symbol}";

    /// <summary>
    /// A credential shaped like nothing a vendor issues, so a match cannot be coincidence.
    /// </summary>
    private const string Secret = "not a key - the value that must never be persisted";

    /// <summary>Two sessions in EODHD's own shape. Small on purpose; the count is asserted.</summary>
    private const string Payload =
        """
        [{"date":"2023-02-13","open":1.26,"high":1.27,"low":1.25,"close":1.26,"adjusted_close":1.26,"volume":1000},
         {"date":"2023-02-14","open":1.26,"high":1.28,"low":1.24,"close":1.27,"adjusted_close":1.27,"volume":2000}]
        """;

    private readonly PostgresFixture _fixture;

    public ProviderExchangePersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ================================================================= the happy path

    [SkippableFact]
    public async Task One_acquisition_persists_exactly_one_provenance_row_with_every_field()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var archiveRoot = TemporaryRoot();

        try
        {
            var authorization = new ScopedWriteAuthorization();
            await using var context = _fixture.CreateContext(authorization);

            var harness = Build(context, authorization, archiveRoot);

            var result = await harness.Acquisition.AcquireAsync(Request());

            // ---- the run -------------------------------------------------------------------
            Assert.Equal(IngestionOutcome.Succeeded, result.Run.Outcome);

            await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

            var run = Assert.Single(await verification.IngestionRuns.ToListAsync());
            Assert.Equal(result.Run.Id, run.Id);

            var artifact = Assert.Single(run.Artifacts);

            // ---- the provenance row --------------------------------------------------------
            var row = Assert.Single(await verification.ProviderExchanges.ToListAsync());

            Assert.Equal(run.Id, row.IngestionRunId);
            Assert.Equal(0, row.ExchangeOrdinal);
            Assert.Equal(SourceIdValue, row.SourceId.Value);
            Assert.Equal(run.Request.Fingerprint(), row.RequestFingerprint);
            Assert.Equal(EndpointTemplate, row.EndpointTemplate);
            Assert.Equal(WindowFrom, row.RequestedFromUtc!.Value);
            Assert.Equal(WindowTo, row.RequestedToUtc!.Value);
            Assert.Equal(200, row.HttpStatusCode!.Value);
            Assert.Equal(Now, row.RetrievedAtUtc);
            Assert.Equal(artifact.Value, row.ResponseContentHash);
            Assert.Equal(Encoding.UTF8.GetByteCount(Payload), row.ResponseByteLength!.Value);

            // The parameters really are the request's, not a placeholder that happened to persist.
            using var parameters = JsonDocument.Parse(row.RedactedRequestParameters);

            Assert.Equal(Symbol, parameters.RootElement.GetProperty("symbol").GetString());
            Assert.Equal("2023-02-13", parameters.RootElement.GetProperty("from").GetString());
            Assert.Equal("2023-02-14", parameters.RootElement.GetProperty("to").GetString());

            using var headers = JsonDocument.Parse(row.SelectedResponseHeaders);

            Assert.Equal(
                "application/json",
                headers.RootElement.GetProperty("content-type").GetString());

            // ---- the archive ---------------------------------------------------------------
            var hash = artifact.Value;
            var payloadPath = Path.Combine(archiveRoot, hash[..2], hash[2..4], hash + ".bin");
            var sidecarPath = Path.Combine(archiveRoot, hash[..2], hash[2..4], hash + ".json");

            Assert.True(File.Exists(payloadPath), payloadPath);
            Assert.True(File.Exists(sidecarPath), sidecarPath);
            Assert.Equal((long)row.ResponseByteLength!.Value, new FileInfo(payloadPath).Length);

            // The sidecar still describes the payload and nothing about the request. A URL here
            // would be a URL carrying an api_token.
            var sidecar = await File.ReadAllTextAsync(sidecarPath);

            Assert.DoesNotContain("http", sidecar, StringComparison.OrdinalIgnoreCase);

            // ---- normalisation --------------------------------------------------------------
            Assert.NotNull(result.Normalization);
            Assert.Equal(1, result.Normalization!.PayloadsRead);
            Assert.Equal(0, result.Normalization.PayloadsQuarantined);
            Assert.Equal(0, result.Normalization.RowsRejected);

            var observations = await verification.Observations.ToListAsync();

            Assert.Equal(result.Normalization.ObservationsRecorded, observations.Count);
            Assert.NotEmpty(observations);

            // Nothing beyond what the two canned sessions describe.
            Assert.All(observations, o => Assert.Equal(Symbol, o.Subject.Identifier));
            Assert.All(observations, o => Assert.Equal(SourceIdValue, o.Provenance.SourceId.Value));
            Assert.All(observations, o => Assert.Contains(
                o.Provenance.AsOfUtc.Date,
                new[] { WindowFrom.Date, WindowTo.Date }));

            // The Gate 12 shape: one row per subject, attribute, instant and source.
            var duplicates = observations
                .GroupBy(o => (o.Subject.Identifier, o.Attribute, o.Provenance.AsOfUtc, o.Provenance.SourceId.Value))
                .Where(g => g.Count() > 1)
                .ToList();

            Assert.Empty(duplicates);
        }
        finally
        {
            Cleanup(archiveRoot);
        }
    }

    // ================================================================= security

    [SkippableFact]
    public async Task No_credential_reaches_the_persisted_provenance_row()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var archiveRoot = TemporaryRoot();

        try
        {
            var authorization = new ScopedWriteAuthorization();
            await using var context = _fixture.CreateContext(authorization);

            // The provider is asked to hand up provenance that CARRIES a credential, in both the
            // parameter map and the headers. Two allow-list filters stand between that and the
            // database - one in the connector, one in RequestProvenance.Create - and a domain rule
            // refuses the row outright if anything gets past them. This asserts on what actually
            // landed, which is the only claim worth making.
            var harness = Build(context, authorization, archiveRoot, leakyProvenance: true);

            await harness.Acquisition.AcquireAsync(Request());

            await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

            var row = Assert.Single(await verification.ProviderExchanges.ToListAsync());

            var persisted = string.Join(
                " ",
                row.EndpointTemplate,
                row.RedactedRequestParameters,
                row.SelectedResponseHeaders,
                row.ProviderCorrelationId ?? "");

            Assert.DoesNotContain(Secret, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("api_token", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bearer", persisted, StringComparison.OrdinalIgnoreCase);

            // A template is a route, never a URI: no scheme, no host, no query.
            Assert.DoesNotContain("://", row.EndpointTemplate, StringComparison.Ordinal);
            Assert.DoesNotContain("?", row.EndpointTemplate, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(archiveRoot);
        }
    }

    // ================================================================= the guard is not disabled

    /// <summary>
    /// The exemption permits creating a provenance row. It permits nothing else.
    /// </summary>
    /// <remarks>
    /// The fix would be a bad fix if it had made <c>ProviderExchange</c> generally writable. It did
    /// the opposite: listing the type also brought it under the append-only rule, so a row cannot be
    /// edited or deleted even inside an open window.
    /// </remarks>
    [SkippableFact]
    public async Task A_persisted_provenance_row_cannot_be_modified_even_inside_a_window()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var archiveRoot = TemporaryRoot();

        try
        {
            var authorization = new ScopedWriteAuthorization();
            await using var context = _fixture.CreateContext(authorization);

            await Build(context, authorization, archiveRoot).Acquisition.AcquireAsync(Request());

            await using var second = _fixture.CreateContext(authorization);
            var tracked = await second.ProviderExchanges.FirstAsync();

            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                second.Entry(tracked).State = EntityState.Modified;

                var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                    () => second.SaveChangesAsync());

                Assert.Contains("append-only", exception.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Cleanup(archiveRoot);
        }
    }

    [SkippableFact]
    public async Task A_provenance_row_added_directly_must_still_go_through_its_store()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        context.ProviderExchanges.Add(ProviderExchange.Record(
            IngestionRunId.New(),
            0,
            SourceId.Create(SourceIdValue),
            new string('a', 64),
            EndpointTemplate,
            "{}",
            WindowFrom,
            WindowTo,
            200,
            "{}",
            Now,
            new string('b', 64),
            10,
            null,
            Now));

        var exception = await Assert.ThrowsAsync<UnauthorizedWriteException>(
            () => context.SaveChangesAsync());

        Assert.Contains("through their stores", exception.Message, StringComparison.Ordinal);
    }

    // ================================================================= the failure path

    /// <summary>
    /// Provenance persistence fails after the run is durable, against the real database.
    /// </summary>
    /// <remarks>
    /// The shape of the live pilot's crash, reproduced without a vendor. What must be true now:
    /// the run is inserted once, the original failure is what the caller sees, and nothing claims
    /// a provenance row that is not there.
    /// </remarks>
    [SkippableFact]
    public async Task A_provenance_failure_after_the_run_is_durable_leaves_one_run_and_no_row()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var archiveRoot = TemporaryRoot();

        try
        {
            var authorization = new ScopedWriteAuthorization();
            await using var context = _fixture.CreateContext(authorization);

            var harness = Build(
                context,
                authorization,
                archiveRoot,
                exchangeStore: new FailingProviderExchangeStore());

            var exception = await Assert.ThrowsAsync<ProvenanceWriteFailedException>(
                () => harness.Acquisition.AcquireAsync(Request()));

            // 1. The original failure, not one raised while handling it.
            Assert.Contains("must survive", exception.Message, StringComparison.Ordinal);

            // 2. No duplicate-key error standing in front of it, anywhere in the chain.
            for (Exception? walk = exception; walk is not null; walk = walk.InnerException)
            {
                Assert.DoesNotContain("PK_ingestion_runs", walk.Message, StringComparison.Ordinal);
            }

            await using var verification = _fixture.CreateContext(new ScopedWriteAuthorization());

            // 3. Exactly one run. The defect inserted it twice.
            var run = Assert.Single(await verification.IngestionRuns.ToListAsync());
            Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);

            // 4. Nothing partial, and nothing falsely claimed as persisted.
            Assert.Empty(await verification.ProviderExchanges.ToListAsync());

            // 5. The database is otherwise consistent: the archived bytes and the run agree, and
            //    normalisation never ran because the acquisition never returned.
            Assert.Single(run.Artifacts);
            Assert.Empty(await verification.Observations.ToListAsync());
        }
        finally
        {
            Cleanup(archiveRoot);
        }
    }

    // ================================================================= fixtures

    private sealed record Harness(IDataAcquisition Acquisition);

    private static Harness Build(
        AppDbContext context,
        ScopedWriteAuthorization authorization,
        string archiveRoot,
        bool leakyProvenance = false,
        IProviderExchangeStore? exchangeStore = null)
    {
        var clock = new FixedClock(Now);

        // The real seam. It opens the write-authorisation window for the duration of the effect and
        // closes it when the effect returns - which is what puts RecordExchangesAsync outside the
        // window and is the whole reason this test exists.
        var actionGateway = new ActionGateway(
            new PolicyEngine(),
            new IngestionPolicyContext(),
            new EfAuditSink(context),
            new EfIdempotencyStore(context),
            new EfActionExecutionStore(context),
            authorization,
            clock);

        var registry = new SingleSourceRegistry(ActiveSource());

        var archive = new FileSystemRawResponseArchive(
            Options.Create(new RawArchiveOptions { RootPath = archiveRoot }));

        var gateway = new IngestionGateway(
            registry,
            new SingleProviderCatalogue(new StubPriceProvider(leakyProvenance)),
            new AlwaysAllowRateLimiter(),
            archive,
            new EfIngestionRunStore(context),
            actionGateway,
            clock,
            exchangeStore ?? new EfProviderExchangeStore(context));

        var eodhd = Options.Create(EodhdTestOptions.Build());

        var pipeline = new NormalizationPipeline(
            archive,
            [new EodhdDailyPriceNormalizer(eodhd)],
            new EfObservationStore(context),
            new EfQuarantineStore(context),
            actionGateway,
            clock);

        return new Harness(new DataAcquisitionService(gateway, pipeline));
    }

    private static IngestionRequest Request() =>
        IngestionRequest.Create(
            SourceId.Create(SourceIdValue),
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create(SubjectKind, Symbol),
            CorrelationId.New(),
            Now,
            DateRange.Create(WindowFrom, WindowTo));

    private static DataSource ActiveSource()
    {
        var source = DataSource.Register(
            SourceId.Create(SourceIdValue),
            "EODHD end-of-day prices (test)",
            SourceType.DataVendor,
            SourceAuthority.Secondary,
            Region.Global,
            [DataCategory.MarketPrices],
            UpdateCadence.Daily(),
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: false,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: "Test licensing note. Storage and automated processing permitted.",
                retention: RetentionLimit.Unlimited),
            VerificationPolicy.RequiresCorroboration,
            Now);

        source.Activate(Now);

        return source;
    }

    private static string TemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "aiinv-provenance-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(path);

        return path;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------- doubles: the transport, and nothing else ----------

    /// <summary>Canned bytes and canned provenance. It touches no network.</summary>
    private sealed class StubPriceProvider : IDataProvider
    {
        private readonly bool _leaky;

        public StubPriceProvider(bool leaky) => _leaky = leaky;

        public SourceId SourceId { get; } = SourceId.Create(SourceIdValue);

        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Create(
            [DataCategory.MarketPrices],
            [Region.Global],
            [SubjectKind],
            supportsWindow: true,
            maxWindowDuration: null,
            quota: null);

        public Task<ProviderResponse> FetchAsync(
            IngestionRequest request,
            string? continuationToken = null,
            CancellationToken cancellationToken = default)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["symbol"] = Symbol,
                ["fmt"] = "json",
                ["period"] = "d",
                ["from"] = RequestProvenance.FormatDate(WindowFrom),
                ["to"] = RequestProvenance.FormatDate(WindowTo),
            };

            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = "application/json",
            };

            if (_leaky)
            {
                // What a careless connector would hand up. Nothing here may reach the database.
                parameters["api_token"] = Secret;
                headers["authorization"] = "Bearer " + Secret;
            }

            return Task.FromResult(ProviderResponse.Create(
                Encoding.UTF8.GetBytes(Payload),
                "application/json",
                Now,
                sourceRecordId: Symbol,
                continuationToken: null,
                provenance: RequestProvenance.Create(
                    EndpointTemplate,
                    parameters,
                    WindowFrom,
                    WindowTo,
                    200,
                    headers,
                    null)));
        }
    }

    /// <summary>Fails every provenance write, and says so unmistakably.</summary>
    private sealed class FailingProviderExchangeStore : IProviderExchangeStore
    {
        public Task RecordAsync(
            IReadOnlyList<ProviderExchange> exchanges,
            CancellationToken cancellationToken = default) =>
            throw new ProvenanceWriteFailedException();

        public Task<IReadOnlyList<ProviderExchange>> ForRunAsync(
            IngestionRunId runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderExchange>>([]);

        public Task<IReadOnlyList<ProviderExchange>> ForResponseContentHashAsync(
            string contentHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderExchange>>([]);
    }

    private sealed class ProvenanceWriteFailedException : Exception
    {
        public ProvenanceWriteFailedException()
            : base("The provenance write failed, and this is the message that must survive.")
        {
        }
    }

    private sealed class SingleSourceRegistry : ISourceRegistry
    {
        private readonly DataSource _source;

        public SingleSourceRegistry(DataSource source) => _source = source;

        public Task<DataSource?> GetByIdAsync(SourceId id, CancellationToken cancellationToken = default) =>
            Task.FromResult<DataSource?>(_source.Id == id ? _source : null);

        public Task<bool> ExistsAsync(SourceId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_source.Id == id);

        public Task<IReadOnlyList<DataSource>> FindSuppliersAsync(
            DataCategory category,
            Region region,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DataSource>>(
                _source.Supplies(category, region) ? [_source] : []);

        public Task<IReadOnlyList<DataSource>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DataSource>>([_source]);

        /// <summary>
        /// Staging a registration is not something this double supports.
        /// </summary>
        /// <remarks>
        /// It holds exactly one source, fixed at construction, because these tests are about what
        /// the gateway READS from the registry on its way to a provenance row. Silently accepting a
        /// second registration and continuing to serve the first would be the kind of quietly wrong
        /// double that makes a green test mean nothing, so it says so instead. The gateway never
        /// calls this; if it ever starts to, this fails loudly rather than lying.
        /// </remarks>
        public void Add(DataSource source) =>
            throw new NotSupportedException(
                "SingleSourceRegistry serves one fixed source and does not stage registrations.");
    }

    private sealed class SingleProviderCatalogue : IProviderCatalogue
    {
        private readonly IDataProvider _provider;

        public SingleProviderCatalogue(IDataProvider provider) => _provider = provider;

        public IDataProvider? Find(SourceId sourceId) =>
            _provider.SourceId == sourceId ? _provider : null;

        public IReadOnlyList<IDataProvider> All() => [_provider];
    }

    private sealed class AlwaysAllowRateLimiter : IProviderRateLimiter
    {
        public Task<bool> TryAcquireAsync(
            SourceId sourceId,
            ProviderQuota quota,
            DateTime nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// The Development policy for DataIngestion, stated directly.
    /// </summary>
    /// <remarks>
    /// The same shape <c>appsettings.Development.json</c> configures - enabled, unattended ceiling
    /// Low, no irreversible auto-execute, no AI proposers - supplied here rather than read from a
    /// file, so the test states the posture it is running under instead of inheriting one.
    /// </remarks>
    private sealed class IngestionPolicyContext : IPolicyContextProvider
    {
        public Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyContext.Create(
                "Development",
                KillSwitchState.Disengaged,
                [
                    CapabilityPolicy.Create(Capability.DataIngestion, enabled: true, RiskTier.Low),
                    CapabilityPolicy.Create(Capability.ReferenceDataManagement, enabled: true, RiskTier.Low),
                ]));
    }

    private sealed class FixedClock : IClock
    {
        private readonly DateTime _now;

        public FixedClock(DateTime now) => _now = now;

        public DateTime UtcNow => _now;
    }
}
