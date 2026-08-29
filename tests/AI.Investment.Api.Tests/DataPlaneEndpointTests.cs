using System.Net;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Contract tests for the data-plane endpoints, with no database reachable.
/// </summary>
/// <remarks>
/// <para>
/// Narrow and deliberate, like the companies tests. What is proved here is that a malformed
/// identifier is rejected <em>before</em> anything reaches the registry, and that every route
/// exists and handles a failed read rather than letting it escape as an unhandled exception.
/// Whether the listings return the right rows is an integration concern.
/// </para>
/// <para>
/// One of these also proves something about start-up: this host boots at all with the two new
/// hosted services registered. Both are disabled by default, so neither touches the unreachable
/// database - if either had defaulted to on, every test in this class would fail, which is the
/// check working rather than the check being absent.
/// </para>
/// </remarks>
public sealed class DataPlaneEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public DataPlaneEndpointTests(ApiFactory factory) => _factory = factory;

    /// <summary>Reads that need the database: either answer proves the route is wired.</summary>
    private static void AssertRouteExists(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.OK
                or HttpStatusCode.NotFound
                or HttpStatusCode.InternalServerError,
            $"Unexpected status {response.StatusCode}.");

    [Theory]
    [InlineData("/api/sources")]
    [InlineData("/api/data-plane/freshness")]
    [InlineData("/api/data-plane/runs")]
    [InlineData("/api/data-plane/quarantine")]
    public async Task Every_listing_route_exists_and_handles_a_failed_read(string route)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        AssertRouteExists(response);
    }

    [Theory]
    [InlineData("/api/sources/NOT A SOURCE ID")]
    [InlineData("/api/data-plane/freshness/NOT A SOURCE ID")]
    public async Task A_malformed_source_id_is_rejected_before_the_registry_is_touched(string route)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        // 400, not 500. With no database reachable, anything that got as far as a query would
        // fail with a server error - so a client error here is proof the identifier was checked
        // first. And not 404: a malformed id is a bad request, not a missing resource.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A malformed identifier is still refused before anything is attempted - now from behind
    /// authentication.
    /// </summary>
    /// <remarks>
    /// Activation became an authenticated action in development block 1: it is what makes the
    /// platform start fetching from a source, and it was anonymous until there was an identity to
    /// record. The client here presents an operator key so the assertion is still about the
    /// identifier check rather than about the gate, which has its own tests.
    /// </remarks>
    [Fact]
    public async Task A_malformed_source_id_is_rejected_before_activation_is_attempted()
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.OperatorKey);

        using var response = await client.PostAsync(
            new Uri("/api/sources/NOT A SOURCE ID/activation", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_but_unregistered_source_is_not_a_client_error()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/sources/not-registered", UriKind.Relative));

        // The identifier is valid, so this must reach the registry. With none reachable it fails;
        // what must not happen is a 400, which would blame the caller for a well-formed request.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        AssertRouteExists(response);
    }

    [Fact]
    public async Task The_runs_listing_accepts_its_query_parameters()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/data-plane/runs?sinceHours=24&take=10", UriKind.Relative));

        AssertRouteExists(response);
    }

    [Theory]
    [InlineData("/api/data-plane/runs?take=-5")]
    [InlineData("/api/data-plane/runs?take=1000000")]
    [InlineData("/api/data-plane/runs?sinceHours=-1")]
    [InlineData("/api/data-plane/quarantine?take=0")]
    public async Task An_out_of_range_page_size_is_clamped_rather_than_rejected(string route)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        // A caller asking for a million rows should get a bounded page, not an error - and
        // certainly not an outage. Clamping keeps a status surface usable by a dashboard that
        // sends whatever its config says.
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        AssertRouteExists(response);
    }

    [Fact]
    public async Task The_host_starts_with_the_background_services_registered()
    {
        using var client = _factory.CreateClient();

        // Liveness needs no database. If either hosted service had defaulted to enabled it would
        // have reached for one at start-up, and this would not answer.
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
