using System.Net;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The authorization boundary around financial state.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These tests are about who may call, not about what comes back.</strong> The API test
/// host points at a deliberately unreachable database, so an endpoint that reads one answers 500 -
/// which is exactly why the assertions below are phrased as "not refused" rather than "200". The
/// read model's own correctness is established in <c>PortfolioReaderTests</c> against fixtures, and
/// its persistence in <c>PositionPersistenceTests</c> against a real PostgreSQL.
/// </para>
/// <para>
/// A 500 here is therefore evidence the request passed authentication and authorization and reached
/// the controller. A 401 or 403 would mean it did not.
/// </para>
/// </remarks>
public sealed class PortfolioEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PortfolioEndpointTests(ApiFactory factory) => _factory = factory;

    public static TheoryData<string> PortfolioRoutes()
    {
        var routes = new TheoryData<string>();

        routes.Add("/api/portfolio");
        routes.Add("/api/portfolio/positions");

        return routes;
    }

    /// <summary>Financial state is not anonymous.</summary>
    [Theory]
    [MemberData(nameof(PortfolioRoutes))]
    public async Task An_anonymous_caller_is_refused(string route)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(PortfolioRoutes))]
    public async Task An_unrecognised_key_is_refused(string route)
    {
        using var client = _factory.CreateOperatorClient("not-a-configured-key");

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Reading the book is its own privilege. An authenticated operator who was not granted it is
    /// refused, which is the point of adding a fifth privilege rather than reusing one of the four.
    /// </summary>
    [Theory]
    [MemberData(nameof(PortfolioRoutes))]
    public async Task An_authenticated_operator_without_the_privilege_is_refused(string route)
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.ObserverKey);

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// An operator holding the privilege gets past the gate. Where it lands after that is the
    /// database's business, and this host does not have one.
    /// </summary>
    [Theory]
    [MemberData(nameof(PortfolioRoutes))]
    public async Task An_operator_with_the_privilege_reaches_the_endpoint(string route)
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.OperatorKey);

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Whatever the endpoint answers - including a failure - it does not name a credential.
    /// </summary>
    [Theory]
    [MemberData(nameof(PortfolioRoutes))]
    public async Task The_response_carries_no_credential(string route)
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.OperatorKey);

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(ApiFactory.OperatorKey, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// There is no write on this surface. A portfolio endpoint that could change a holding would be
    /// a way to write financial state without an execution behind it.
    /// </summary>
    [Theory]
    [MemberData(nameof(PortfolioRoutes))]
    public async Task The_portfolio_surface_accepts_no_writes(string route)
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.OperatorKey);

        using var post = await client.PostAsync(new Uri(route, UriKind.Relative), content: null);
        using var delete = await client.DeleteAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }
}
