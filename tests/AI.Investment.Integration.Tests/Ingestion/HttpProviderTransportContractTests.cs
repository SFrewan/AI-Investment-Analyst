using System.Net.Sockets;
using System.Reflection;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// Every HTTP connector reports a transport failure the same way, and none may quietly stop.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here reaches a real vendor.</strong> Each case drives a connector against a
/// handler that throws, in-process, and asserts what the connector does with the exception. No
/// network, no database, no fixture.
/// </para>
/// <para>
/// <strong>Why this test exists.</strong> <c>ProviderTransportException</c> and its classifier were
/// introduced after fifty-seven transport failures reached the ledger indistinguishable from one
/// another, and were covered by tests that exercised the classifier <em>in isolation</em>. Nothing
/// asserted that any connector actually used it. The price connector was updated; the splits
/// connector was not, and the divergence survived for as long as it did because no test could see
/// it - so two more failures, <c>GPC.US</c> and <c>MYO.US</c>, were recorded as a bare
/// <c>HttpRequestException</c> with the socket error underneath thrown away.
/// </para>
/// <para>
/// This is the test that would have caught it. It is written against the contract rather than
/// against either implementation, and the guard below fails when a connector appears that is
/// neither held to it nor deliberately exempted from it.
/// </para>
/// </remarks>
public sealed class HttpProviderTransportContractTests
{
    private const string Key = "test-key-not-a-real-credential";

    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The connectors held to the contract, and how to build one.</summary>
    private static readonly Dictionary<string, Func<HttpMessageHandler, Connector>> Covered =
        new(StringComparer.Ordinal)
        {
            [nameof(EodhdProvider)] = handler => new Connector(
                new EodhdProvider(Client(handler), Credentialed(), new FixedClock(Now)),
                DataCategory.MarketPrices,
                EodhdProvider.Id),

            [nameof(EodhdSplitsProvider)] = handler => new Connector(
                new EodhdSplitsProvider(Client(handler), Credentialed(), new FixedClock(Now)),
                DataCategory.CorporateActions,
                EodhdSplitsProvider.Id),
        };

    /// <summary>
    /// Connectors deliberately outside this contract, each with the reason written down.
    /// </summary>
    /// <remarks>
    /// An exemption is a decision, so it is recorded as one. <c>SecEdgarProvider</c> does not
    /// intercept transport failures at all - it neither wraps nor discards, so the framework's own
    /// chain reaches the ledger intact but unclassified. Bringing it under this contract is a
    /// change to a connector nobody has asked to change, and guessing at it here would be exactly
    /// the kind of unrequested edit this project does not make.
    /// </remarks>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        [nameof(SecEdgarProvider)] =
            "does not intercept transport failures; its chain reaches the ledger unwrapped and "
            + "unclassified, and bringing it under the contract has not been approved",
    };

    /// <summary>A transport failure is wrapped, classified, and keeps what caused it.</summary>
    [Theory]
    [InlineData(nameof(EodhdProvider))]
    [InlineData(nameof(EodhdSplitsProvider))]
    public async Task A_transport_failure_is_wrapped_classified_and_keeps_its_cause(string name)
    {
        var socket = new SocketException((int)SocketError.HostNotFound);
        var inner = new HttpRequestException($"connect failed ?api_token={Key}", socket);
        var handler = new ThrowingHandler(inner);

        var connector = Covered[name](handler);

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => connector.Provider.FetchAsync(connector.Request("AAPL.US")));

        // 1 and 3: wrapped, and wrapped in the type that carries a diagnosis.
        Assert.IsType<ProviderTransportException>(error);
        Assert.NotSame(inner, error);

        // 2: the cause survives, and so does the cause's cause.
        Assert.Same(inner, error.InnerException);
        Assert.Same(socket, error.InnerException!.InnerException);

        // 4: classified by the existing mechanism, and by nothing else. Compared against what
        // Classify itself returns, so a second taxonomy cannot creep in beside the first.
        var diagnostic = Assert.IsAssignableFrom<ITransportDiagnostic>(error).TransportDiagnostic;

        Assert.Equal(ProviderTransportException.Classify(inner), diagnostic);
        Assert.Equal("socket:HostNotFound", diagnostic);

        // 5: one attempt. A connector that retried would spend authorisation the runner never
        // charged for, and the accounting would be wrong in the direction that costs money.
        Assert.Equal(1, handler.Calls);

        // And the diagnosis is safe to store: a closed-set token, never the transport's words.
        Assert.DoesNotContain(Key, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("api_token", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, error.Message, StringComparison.Ordinal);
    }

    /// <summary>An unclassifiable failure still wraps, and says so rather than guessing.</summary>
    [Theory]
    [InlineData(nameof(EodhdProvider))]
    [InlineData(nameof(EodhdSplitsProvider))]
    public async Task A_failure_with_nothing_to_classify_is_still_wrapped(string name)
    {
        var inner = new HttpRequestException("something went wrong");
        var handler = new ThrowingHandler(inner);

        var connector = Covered[name](handler);

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => connector.Provider.FetchAsync(connector.Request("AAPL.US")));

        Assert.IsType<ProviderTransportException>(error);
        Assert.Same(inner, error.InnerException);

        var diagnostic = Assert.IsAssignableFrom<ITransportDiagnostic>(error).TransportDiagnostic;

        Assert.Equal(ProviderTransportException.Classify(inner), diagnostic);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// Every HTTP connector in the assembly is either held to this contract or exempt on the record.
    /// </summary>
    /// <remarks>
    /// The guard, and the part that actually prevents a recurrence. A new connector that takes an
    /// <see cref="HttpClient"/> fails this test until somebody decides which side of the contract
    /// it is on - which is the decision that was never made for the splits connector.
    /// </remarks>
    [Fact]
    public void Every_http_connector_is_covered_or_exempt_on_the_record()
    {
        var connectors = typeof(EodhdProvider).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDataProvider).IsAssignableFrom(t))
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpClient))))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var accounted = Covered.Keys.Concat(Exempt.Keys).ToHashSet(StringComparer.Ordinal);

        var unaccounted = connectors.Where(c => !accounted.Contains(c)).ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"{string.Join(", ", unaccounted)} take an HttpClient but are neither held to the "
            + "transport contract nor exempt from it. Add each to Covered, or to Exempt with the "
            + "reason - a connector that is in neither is how the splits connector diverged.");

        // And nothing is named here that no longer exists, so the exemptions cannot outlive
        // the connectors they excuse.
        Assert.All(accounted, name => Assert.Contains(name, connectors, StringComparer.Ordinal));

        // Every exemption states a reason. An empty one is a silence with a name on it.
        Assert.All(Exempt.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://eodhd.test/") };

    private static IOptions<EodhdOptions> Credentialed() =>
        Microsoft.Extensions.Options.Options.Create(EodhdTestOptions.Build(
            credential: Key,
            exchanges: [EodhdTestOptions.UnitedStates()]));

    private sealed record Connector(IDataProvider Provider, DataCategory Category, SourceId Source)
    {
        public IngestionRequest Request(string symbol) =>
            IngestionRequest.Create(
                Source,
                Category,
                Region.Global,
                IngestionSubject.Create("Security", symbol),
                CorrelationId.New(),
                Now);
    }

    private sealed class FixedClock : IClock
    {
        private readonly DateTime _now;

        public FixedClock(DateTime now) => _now = now;

        public DateTime UtcNow => _now;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        /// <summary>How many times a connector reached the transport. The contract says once.</summary>
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;

            throw _exception;
        }
    }
}
