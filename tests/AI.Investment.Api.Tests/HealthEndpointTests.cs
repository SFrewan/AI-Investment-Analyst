using System.Net;
using Xunit;

namespace AI.Investment.Api.Tests;

public sealed class HealthEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Liveness must not touch the database: it answers "is this process up?", and a liveness
    /// probe that fails on a database blip gets the container killed for no reason.
    /// </summary>
    [Fact]
    public async Task Liveness_returns_200_without_a_database()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Readiness does touch the database, so with no database reachable it must report
    /// unhealthy rather than throw.
    /// </summary>
    [Fact]
    public async Task Readiness_reports_unhealthy_when_the_database_is_unreachable()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// Every response carries the correlation identifier, so a caller can report a failure
    /// precisely.
    /// </summary>
    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        Assert.False(string.IsNullOrWhiteSpace(response.Headers.GetValues("X-Correlation-ID").First()));
    }

    [Fact]
    public async Task The_removed_template_endpoint_is_gone()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/WeatherForecast", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
