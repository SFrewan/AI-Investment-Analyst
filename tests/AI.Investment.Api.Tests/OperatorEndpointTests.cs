using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Who may reach the operator endpoints, and who is turned away where.
/// </summary>
/// <remarks>
/// <para>
/// The refusals happen in the pipeline, before the controller and long before a database, which is
/// why they can be asserted here against a host with no database behind it. That is also the point:
/// an anonymous caller never reaches the code that would have written something.
/// </para>
/// <para>
/// The audit that started this project recorded finding F-03 - a <c>UseAuthorization()</c> call with
/// no authentication scheme, which is a no-op that reads as security in review. These tests exist so
/// that if the scheme is ever removed again, the suite says so rather than the endpoints quietly
/// becoming public.
/// </para>
/// </remarks>
public sealed class OperatorEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public OperatorEndpointTests(ApiFactory factory) => _factory = factory;

    /// <summary>Every write an operator surface exposes, including the one Sources owns.</summary>
    public static TheoryData<string> WriteEndpoints()
    {
        var id = Guid.NewGuid().ToString();

        return new TheoryData<string>
        {
            "/api/operator/opportunities/" + id + "/rejection",
            "/api/operator/escalations/" + id + "/acknowledgement",
            "/api/operator/escalations/" + id + "/resolution",
            "/api/operator/kill-switch/engagement",
            "/api/operator/watches",
            "/api/sources/sec-edgar/activation",
        };
    }

    // ---- anonymous ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public async Task No_operator_write_is_anonymously_callable(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            new Uri(path, UriKind.Relative),
            JsonContent.Create(new { reason = "x", resolution = "x" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unrecognised_key_is_not_authenticated()
    {
        using var client = _factory.CreateOperatorClient("not-a-real-key");

        using var response = await client.GetAsync(new Uri("/api/operator/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- authenticated, and permitted or not -------------------------------------------------

    /// <summary>
    /// Authentication and authorization are different questions, and the answers are different
    /// statuses. An operator who is signed in and lacks a privilege gets 403, not 401 - sending
    /// them to re-enter a key that was fine would cost an incident.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public async Task An_authenticated_operator_without_privileges_is_forbidden(string path)
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.ObserverKey);

        using var response = await client.PostAsync(
            new Uri(path, UriKind.Relative),
            JsonContent.Create(new { reason = "x", resolution = "x" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_recognised_key_identifies_the_operator_and_its_privileges()
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.OperatorKey);

        using var response = await client.GetAsync(new Uri("/api/operator/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        Assert.Equal(ApiFactory.OperatorId, body.GetProperty("id").GetString());

        var privileges = body.GetProperty("privileges")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToList();

        Assert.Contains("DecideOpportunities", privileges);
        Assert.Contains("AdministerKillSwitch", privileges);
        Assert.Contains("ViewPortfolio", privileges);
        Assert.Equal(5, privileges.Count);
    }

    [Fact]
    public async Task An_authenticated_operator_with_no_privileges_is_still_identified()
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.ObserverKey);

        using var response = await client.GetAsync(new Uri("/api/operator/whoami", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Empty(document.RootElement.GetProperty("privileges").EnumerateArray());
    }

    /// <summary>
    /// A permitted request reaches the controller. It cannot succeed here - there is no database -
    /// and what matters is that it was not turned away at the gate.
    /// </summary>
    [Fact]
    public async Task A_permitted_request_gets_past_authorization()
    {
        using var client = _factory.CreateOperatorClient(ApiFactory.OperatorKey);

        using var response = await client.PostAsync(
            new Uri("/api/operator/kill-switch/engagement", UriKind.Relative),
            JsonContent.Create(new { reason = "" }));

        // Refused for its empty reason by the console, not by the gate. Either way it is not a
        // 401 or a 403, which is the assertion.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- the operator console page -----------------------------------------------------------

    /// <summary>
    /// The console is served, and it is a page rather than a redirect to one. It is public: every
    /// operation it offers is an authenticated call it makes on the operator's behalf.
    /// </summary>
    [Fact]
    public async Task The_operator_console_is_served()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("operator console", html, StringComparison.OrdinalIgnoreCase);

        // The four statements the page must never stop making.
        Assert.Contains("autonomy L3", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live execution unavailable", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing here was executed", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("promotion", html, StringComparison.OrdinalIgnoreCase);
    }
}
