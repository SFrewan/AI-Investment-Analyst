using System.Net;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The EODHD connector, against a stubbed transport.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here reaches the real EODHD.</strong> A unit test that called a metered vendor
/// API would spend somebody's quota on every CI run and would fail when a network did, neither of
/// which says anything about this code. The handler below answers from a fixture.
/// </para>
/// <para>
/// These live in the integration project because they reach Infrastructure internals, not because
/// they touch a database - none of them does, and none needs the Postgres fixture.
/// </para>
/// </remarks>
public sealed class EodhdProviderTests
{
    private const string Key = "test-key-not-a-real-credential";

    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly byte[] TwoRows = Encoding.UTF8.GetBytes(
        """[{"date":"2026-08-26","open":1.0,"close":2.5,"volume":10},""" +
        """{"date":"2026-08-27","open":2.5,"close":2.75,"volume":11}]""");

    [Fact]
    public async Task A_successful_response_is_returned_as_the_exact_bytes()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoRows);

        var response = await Provider(handler).FetchAsync(Request("AAPL.US"));

        Assert.Equal(TwoRows, response.Payload.ToArray());
        Assert.Equal(EodhdProvider.MediaType, response.MediaType);
        Assert.Equal(Now, response.RetrievedAtUtc);
        Assert.Equal("AAPL.US", response.SourceRecordId);
        Assert.False(response.HasMore);
    }

    [Fact]
    public async Task The_request_asks_for_the_daily_json_series()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoRows);

        await Provider(handler).FetchAsync(Request("AAPL.US"));

        var uri = handler.LastUri!.ToString();

        Assert.Contains("api/eod/AAPL.US", uri, StringComparison.Ordinal);
        Assert.Contains("fmt=json", uri, StringComparison.Ordinal);
        Assert.Contains("period=d", uri, StringComparison.Ordinal);
    }

    /// <summary>The connector declares a window, so it must actually send one.</summary>
    [Fact]
    public async Task A_requested_window_becomes_from_and_to()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoRows);

        var request = Request(
            "AAPL.US",
            DateRange.Create(
                new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)));

        await Provider(handler).FetchAsync(request);

        Assert.Contains("from=2026-01-05", handler.LastUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("to=2026-03-09", handler.LastUri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_key_is_refused_before_anything_is_sent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TwoRows);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(handler, key: string.Empty).FetchAsync(Request("AAPL.US")));

        Assert.Equal(0, handler.Calls);
        Assert.Contains("Providers:Eodhd:ApiKey", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_provider_error_is_raised_and_never_swallowed(HttpStatusCode status)
    {
        var handler = new StubHandler(status, Encoding.UTF8.GetBytes("""{"error":"nope"}"""));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.Equal(status, exception.StatusCode);
    }

    /// <summary>
    /// The distinctions an operator acts on differently. A rate limit that read like a bad key
    /// sends somebody to rotate a credential that was fine.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthenticated")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate-limited")]
    [InlineData(HttpStatusCode.NotFound, "no end-of-day series")]
    public async Task Each_failure_says_what_kind_it_is(HttpStatusCode status, string expected)
    {
        var handler = new StubHandler(status, []);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The connector does not retry. The gateway owns the run and its pacing.</summary>
    [Fact]
    public async Task A_failure_is_attempted_exactly_once()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, []);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// A success with no body is a broken answer, not an instrument that did not trade.
    /// </summary>
    [Fact]
    public async Task An_empty_body_is_refused_rather_than_archived()
    {
        var handler = new StubHandler(HttpStatusCode.OK, []);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.Contains("no body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_without_the_request()
    {
        var handler = new ThrowingHandler(new HttpRequestException($"connect to ?api_token={Key} failed"));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(handler).FetchAsync(Request("AAPL.US")));

        Assert.DoesNotContain(Key, exception.Message, StringComparison.Ordinal);
        Assert.Contains(EodhdProvider.Redaction, exception.Message, StringComparison.Ordinal);
    }

    // ---- symbols ----------------------------------------------------------------------------

    [Theory]
    [InlineData("AAPL.US")]
    [InlineData("BP.LSE")]
    [InlineData("BRK-B.US")]
    [InlineData("7203.TSE")]
    public void A_ticker_with_an_exchange_suffix_is_accepted(string symbol) =>
        Assert.Equal(symbol, EodhdProvider.SafeSymbol(IngestionSubject.Create("Security", symbol)));

    /// <summary>
    /// Refused rather than escaped. Escaping turns a malformed identifier into a valid request for
    /// something else; refusing turns it into a recorded failure.
    /// </summary>
    [Theory]
    [InlineData("AAPL")]
    [InlineData("AAPL.US.EXTRA")]
    [InlineData(".US")]
    [InlineData("AAPL.")]
    [InlineData("AAPL.US?api_token=stolen")]
    [InlineData("../../etc/passwd")]
    [InlineData("AAPL US")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_symbol_is_refused(string? identifier) =>
        Assert.Null(EodhdProvider.SafeSymbol(IngestionSubject.Create("Security", identifier)));

    [Fact]
    public void A_sweep_has_no_symbol() =>
        Assert.Null(EodhdProvider.SafeSymbol(IngestionSubject.Sweep("Security")));

    [Fact]
    public void A_subject_that_is_not_a_security_is_refused() =>
        Assert.Null(EodhdProvider.SafeSymbol(IngestionSubject.Create("Company", "AAPL.US")));

    [Theory]
    [InlineData("AAPL.US", "AAPL", "US")]
    [InlineData("BP.LSE", "BP", "LSE")]
    public void A_symbol_splits_into_a_ticker_and_an_exchange(
        string symbol,
        string ticker,
        string exchange)
    {
        Assert.Equal(ticker, EodhdProvider.TickerOf(symbol));
        Assert.Equal(exchange, EodhdProvider.ExchangeOf(symbol));
    }

    // ---- capabilities -----------------------------------------------------------------------

    [Fact]
    public void The_connector_declares_only_what_it_serves()
    {
        var capabilities = Provider(new StubHandler(HttpStatusCode.OK, TwoRows)).Capabilities;

        Assert.Contains(DataCategory.MarketPrices, capabilities.Categories);
        Assert.Single(capabilities.Categories);
        Assert.True(capabilities.SupportsWindow);
        Assert.NotNull(capabilities.Quota);
        Assert.Equal(TimeSpan.FromMinutes(1), capabilities.Quota!.Window);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static EodhdProvider Provider(HttpMessageHandler handler, string key = Key) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://eodhd.test/") },
            Options.Create(new EodhdOptions
            {
                Enabled = true,
                ApiKey = key,
                LicensingNotes = "test",
                Exchanges = [new ExchangeSessionOptions { Code = "US" }],
            }),
            new FixedClock(Now));

    private static IngestionRequest Request(string symbol, DateRange? window = null) =>
        IngestionRequest.Create(
            EodhdProvider.Id,
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create("Security", symbol),
            CorrelationId.New(),
            Now,
            window);

    private sealed class FixedClock : IClock
    {
        private readonly DateTime _now;

        public FixedClock(DateTime now) => _now = now;

        public DateTime UtcNow => _now;
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

        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Calls++;
            LastUri = request.RequestUri;

            var content = new ByteArrayContent(_payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                EodhdProvider.MediaType);

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
            Task.FromException<HttpResponseMessage>(_exception);
    }
}
