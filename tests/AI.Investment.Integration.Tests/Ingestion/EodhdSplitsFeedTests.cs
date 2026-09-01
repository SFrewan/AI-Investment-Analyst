using System.Globalization;
using System.Net;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Normalization;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The corporate-actions feed, from the request it builds to the series it makes readable.
/// </summary>
/// <remarks>
/// <para>
/// The last test is the one that matters. Everything before it checks a link; that one runs the
/// whole chain - a vendor document, normalised into observations, stored in PostgreSQL, read back
/// point-in-time, and used to restate a price series that would otherwise be refused as a
/// seventy-five per cent collapse. Until this feed existed, that series had no explanation
/// available and the platform correctly declined to screen it.
/// </para>
/// <para>
/// No network. The provider is given a stub handler; the payload is the shape EODHD actually
/// returns.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class EodhdSplitsFeedTests : IAsyncLifetime
{
    private const string Key = "test-key-not-a-real-credential";

    private static readonly decimal[] RestatedByFour = [100m, 101m, 101m, 102m];

    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A four-for-one split, in the vendor's own wire format.</summary>
    private const string SplitsDocument =
        """[{"date":"2026-06-03","split":"4.000000/1.000000"}]""";

    private readonly PostgresFixture _fixture;

    public EodhdSplitsFeedTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- the connector -----------------------------------------------------

    [Fact]
    public async Task The_request_goes_to_the_splits_endpoint_for_the_symbol()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Encoding.UTF8.GetBytes(SplitsDocument));

        var response = await Provider(handler).FetchAsync(Request("AAPL.US"));

        Assert.Equal("AAPL.US", response.SourceRecordId);
        Assert.NotNull(handler.LastUri);
        Assert.Contains("api/splits/AAPL.US", handler.LastUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("fmt=json", handler.LastUri.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The credential never reaches an exception message.
    /// </summary>
    /// <remarks>
    /// The redaction is the price connector's, used rather than copied, and this asserts that the
    /// sharing actually holds for this connector too. A second implementation of it would be a
    /// second place for it to drift.
    /// </remarks>
    [Fact]
    public async Task A_transport_failure_does_not_name_the_credential()
    {
        var handler = new ThrowingHandler(
            new HttpRequestException($"connect failed for https://eodhd.test/api/splits/AAPL.US?api_token={Key}"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
        Assert.Contains(EodhdProvider.Redaction, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_body_is_refused_rather_than_read_as_no_splits()
    {
        var handler = new StubHandler(HttpStatusCode.OK, []);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.Contains("different facts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_connector_declares_corporate_actions_and_nothing_else() =>
        Assert.Equal(
            [DataCategory.CorporateActions],
            Provider(new StubHandler(HttpStatusCode.OK, [1])).Capabilities.Categories.ToArray());

    // ---- the normaliser ----------------------------------------------------

    [Fact]
    public async Task A_split_row_becomes_one_observation_stamped_at_the_effective_session()
    {
        var result = await Normalize(SplitsDocument);

        Assert.False(result.IsQuarantined);

        var observation = Assert.Single(result.Observations);

        Assert.Equal(EodhdSplitsNormalizer.SplitAttribute, observation.Attribute);
        Assert.Equal(4m, observation.Value.AsNumber());

        // 2026-06-03 at the configured session close, which is what leaves that session's own
        // close alone and restates only what came before it.
        Assert.Equal(
            new DateTime(2026, 6, 3, 20, 0, 0, DateTimeKind.Utc),
            observation.Provenance.AsOfUtc);
    }

    /// <summary>The attribute the normaliser writes is the one the reader asks for.</summary>
    /// <remarks>
    /// A mismatch here would produce a series that is silently refused rather than an error
    /// anybody would see, which is the worst of both outcomes.
    /// </remarks>
    [Fact]
    public void The_written_attribute_is_the_one_discovery_reads() =>
        Assert.Equal(
            EodhdSplitsNormalizer.SplitAttribute,
            DiscoverySettings.Standard.SplitAttribute);

    [Fact]
    public async Task An_instrument_that_has_never_split_normalises_to_nothing_rather_than_a_quarantine()
    {
        var result = await Normalize("[]");

        Assert.False(result.IsQuarantined);
        Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("""[{"date":"2026-06-03","split":"0/1"}]""")]
    [InlineData("""[{"date":"2026-06-03","split":"4/0"}]""")]
    [InlineData("""[{"date":"2026-06-03","split":"four-for-one"}]""")]
    [InlineData("""[{"date":"2026-06-03"}]""")]
    [InlineData("""[{"date":"not-a-date","split":"4/1"}]""")]
    public async Task A_row_that_cannot_state_a_ratio_quarantines_the_document(string payload)
    {
        var result = await Normalize(payload);

        Assert.True(result.IsQuarantined);
        Assert.Equal(EodhdSplitsNormalizer.UnreadableRowRule, result.RuleId);
    }

    [Fact]
    public async Task A_document_that_is_not_an_array_quarantines()
    {
        var result = await Normalize("""{"date":"2026-06-03"}""");

        Assert.True(result.IsQuarantined);
        Assert.Equal(EodhdSplitsNormalizer.UnexpectedShapeRule, result.RuleId);
    }

    [Fact]
    public async Task A_reverse_split_is_read_as_a_ratio_below_one()
    {
        var result = await Normalize("""[{"date":"2026-06-03","split":"1.000000/10.000000"}]""");

        Assert.False(result.IsQuarantined);
        Assert.Equal(0.1m, Assert.Single(result.Observations).Value.AsNumber());
    }

    // ---- the whole chain ---------------------------------------------------

    /// <summary>
    /// <strong>End to end.</strong> A vendor document makes a stepped series readable.
    /// </summary>
    /// <remarks>
    /// The same four closes are refused without the feed and restated with it, and both halves are
    /// asserted here so the feed's contribution is visible rather than assumed.
    /// </remarks>
    [SkippableFact]
    public async Task A_fetched_split_document_makes_a_stepped_series_readable()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        // Four closes spanning the split: ~400 before, ~100 after. Raw, a 75% collapse.
        await SeedClosesAsync(400m, 404m, 101m, 102m);

        // Without the split, the series is refused rather than screened.
        var before = await ReadAdjustedAsync();

        Assert.False(before.IsUsable);
        Assert.Equal(SeriesRefusal.UnexplainedDiscontinuity, before.Refusal);

        // Now fetch and normalise the vendor document, and store what it produced.
        var handler = new StubHandler(HttpStatusCode.OK, Encoding.UTF8.GetBytes(SplitsDocument));
        var fetched = await Provider(handler).FetchAsync(Request("AAPL.US"));

        var normalised = await new EodhdSplitsNormalizer(Options.Create(SplitOptions())).NormalizeAsync(
            new NormalizationInput(
                EodhdSplitsProvider.Id,
                DataCategory.CorporateActions,
                Apple(),
                ContentHash.Compute(fetched.Payload.Span),
                fetched.Payload,
                fetched.RetrievedAtUtc));

        Assert.False(normalised.IsQuarantined);

        await StoreAsync(normalised.Observations);

        // And the same window now resolves.
        var after = await ReadAdjustedAsync();

        Assert.True(after.IsUsable);
        Assert.Equal(
            RestatedByFour,
            after.Observations.Select(o => o.Close).ToArray());
    }

    // ---- helpers -----------------------------------------------------------

    private static readonly DateTime FirstSession = new(2026, 6, 1, 20, 0, 0, DateTimeKind.Utc);

    private static IngestionSubject Apple() => IngestionSubject.Create("Security", "AAPL.US");

    private static EodhdOptions SplitOptions() => new()
    {
        Enabled = true,
        ApiKey = Key,
        LicensingNotes = "test",
        // The session close is stated, not defaulted: it is what the split's instant is built from,
        // and a zero here would place the split at midnight rather than at the close - the same
        // off-by-one this normaliser's remarks warn about.
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

    private static EodhdSplitsProvider Provider(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://eodhd.test/") },
            Options.Create(SplitOptions()),
            new FixedClock(Now));

    private static IngestionRequest Request(string symbol) =>
        IngestionRequest.Create(
            EodhdSplitsProvider.Id,
            DataCategory.CorporateActions,
            Region.Global,
            IngestionSubject.Create("Security", symbol),
            CorrelationId.New(),
            Now);

    private static Task<NormalizationResult> Normalize(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);

        return new EodhdSplitsNormalizer(Options.Create(SplitOptions())).NormalizeAsync(
            new NormalizationInput(
                EodhdSplitsProvider.Id,
                DataCategory.CorporateActions,
                Apple(),
                ContentHash.Compute(bytes),
                bytes,
                Now));
    }

    private async Task<AdjustedPriceSeries> ReadAdjustedAsync()
    {
        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        return await new PriceSeriesReader(new EfObservationStore(context)).ReadAdjustedAsync(
            Apple(),
            EodhdDailyPriceNormalizer.CloseAttribute,
            EodhdSplitsNormalizer.SplitAttribute,
            120,
            Now,
            SplitAdjustment.DefaultMaxUnexplainedMove);
    }

    private Task SeedClosesAsync(params decimal[] closes) =>
        StoreAsync(closes
            .Select((close, index) =>
            {
                var session = FirstSession.AddDays(index);

                return Observation.RecordFact(
                    Apple(),
                    EodhdDailyPriceNormalizer.CloseAttribute,
                    ObservationValue.Number(close),
                    Domain.Evidence.Provenance.Create(
                        "eodhd-eod", session, session.AddMinutes(15), Now));
            })
            .ToList());

    private async Task StoreAsync(IReadOnlyList<Observation> observations)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await new EfObservationStore(context).RecordAsync(observations);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTime now) => UtcNow = now;

        public DateTime UtcNow { get; }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _payload;

        public StubHandler(HttpStatusCode status, byte[] payload)
        {
            _status = status;
            _payload = payload;
        }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            LastUri = request.RequestUri;

            var content = new ByteArrayContent(_payload);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(EodhdProvider.MediaType);

            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw _exception;
    }
}
