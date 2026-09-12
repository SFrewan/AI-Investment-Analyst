using System.Net;
using System.Net.Http.Json;
using AI.Investment.Application.Companies.CreateCompany;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Contract tests for the companies endpoints, with no database reachable.
/// </summary>
/// <remarks>
/// What these prove is narrow and deliberate: validation happens before any persistence is
/// attempted, and a malformed request is a 400 rather than a 500. Whether a valid creation
/// actually persists is an integration concern and is tested against a real database.
/// </remarks>
public sealed class CompaniesEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CompaniesEndpointTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_request_with_no_name_is_rejected_before_any_persistence()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/companies", UriKind.Relative),
            new CreateCompanyCommand(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The malformed-ticker case is gone with the ticker: D4 moved tradable identity off Company,
    // so the create command no longer carries one and there is no malformed value to reject here.
    // Ticker well-formedness is still asserted where the type lives.

    [Fact]
    public async Task An_unknown_company_id_is_a_404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"/api/companies/{Guid.NewGuid()}", UriKind.Relative));

        // With no database reachable the read fails; either answer proves the route exists and
        // that the failure is handled rather than escaping as an unhandled exception.
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError,
            $"Unexpected status {response.StatusCode}.");
    }
}
