using System.Net;
using AI.Investment.Dashboard.Localization;
using AI.Investment.Dashboard.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Investment.Dashboard.Tests;

/// <summary>
/// A rendering host wired to a stubbed platform.
/// </summary>
/// <remarks>
/// Nothing here reaches a network. Every response is a fixture, so a test asserts what the
/// dashboard does with an answer rather than whether a backend happened to be running - and no
/// test can depend on EODHD, a database, or the observation window having started.
/// </remarks>
public sealed class TestHost : Bunit.TestContext
{
    public TestHost()
    {
        Handler = new StubHandler();
        Session = new OperatorSession();
        Localization = new LocalizationState();
        Refresh = new RefreshState();

        var http = new HttpClient(Handler) { BaseAddress = new Uri("https://platform.test/") };

        Services.AddSingleton(http);
        Services.AddSingleton(Session);
        Services.AddSingleton(Localization);
        Services.AddSingleton(Refresh);
        Services.AddSingleton(new PlatformClient(http, Session));

        // The shell's JavaScript is document manipulation the renderer has no document for. Loose
        // mode records the calls and answers null, which is exactly the "browser refused storage"
        // path the components already handle.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public StubHandler Handler { get; }

    public OperatorSession Session { get; }

    public LocalizationState Localization { get; }

    public RefreshState Refresh { get; }

    /// <summary>Signs a test operator in without going through the form.</summary>
    public void SignIn(params string[] privileges) =>
        Session.Establish(
            "test-key-not-a-real-credential",
            new OperatorIdentityDto("operator@example.test", "Test Operator", privileges));

    /// <summary>Answers whatever the components ask for, from fixtures.</summary>
    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requested { get; } = [];

        public List<string> SentKeys { get; } = [];

        /// <summary>Every path answers 404 unless a fixture says otherwise.</summary>
        public HttpStatusCode Default { get; set; } = HttpStatusCode.NotFound;

        public void When(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _routes[path] = (status, body);

        public void WhenAll(HttpStatusCode status)
        {
            _routes.Clear();
            Default = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? string.Empty;

            Requested.Add(path);

            if (request.Headers.TryGetValues(OperatorSession.HeaderName, out var keys))
            {
                SentKeys.AddRange(keys);
            }

            foreach (var route in _routes)
            {
                if (path.StartsWith(route.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(route.Value.Status)
                    {
                        Content = new StringContent(route.Value.Body, System.Text.Encoding.UTF8, "application/json"),
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(Default)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>The fixtures. Deterministic, and none of them are market data.</summary>
public static class Fixtures
{
    public const string Whoami =
        """{"id":"operator@example.test","displayName":"Test Operator","privileges":["ViewPortfolio"]}""";

    /// <summary>A book with one valued position and one the platform has no price for.</summary>
    public const string PartlyValuedPortfolio =
        """
        {"currency":"USD","asAtUtc":"2026-08-29T00:00:00Z","cash":50000,"costBasis":3000,
         "realisedPnL":120,"unrealisedPnL":null,"marketValue":null,"totalValue":null,
         "isFullyValued":false,"openPositions":2,"valuedPositions":1,"unvaluedPositions":1,
         "positions":[
           {"instrument":"AAPL.US","quantity":10,"averageCost":100,"costBasis":1000,"exposure":1000,
            "realisedPnL":0,"priceAvailability":"Available","currentPrice":130,
            "priceAsOfUtc":"2026-08-27T20:00:00Z","pricePublishedAtUtc":"2026-08-28T00:00:00Z",
            "marketValue":1300,"unrealisedPnL":300,"isOpen":true},
           {"instrument":"MSFT.US","quantity":5,"averageCost":400,"costBasis":2000,"exposure":2000,
            "realisedPnL":0,"priceAvailability":"NoObservedPrice","currentPrice":null,
            "priceAsOfUtc":null,"pricePublishedAtUtc":null,"marketValue":null,"unrealisedPnL":null,
            "isOpen":true}]}
        """;

    public const string EmptyPortfolio =
        """
        {"currency":"USD","asAtUtc":"2026-08-29T00:00:00Z","cash":0,"costBasis":0,"realisedPnL":0,
         "unrealisedPnL":0,"marketValue":0,"totalValue":0,"isFullyValued":true,"openPositions":0,
         "valuedPositions":0,"unvaluedPositions":0,"positions":[]}
        """;

    public const string NoOpportunities = "[]";

    public const string NoEscalations = "[]";

    public const string NoFreshness = "[]";
}
