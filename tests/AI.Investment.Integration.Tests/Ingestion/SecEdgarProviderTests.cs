using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The parts of a connector that can be wrong without a network being involved.
/// </summary>
/// <remarks>
/// These live in the integration test project because they reach Infrastructure internals, not
/// because they touch a database - none of them does, and none needs the Postgres fixture. The
/// HTTP call itself is deliberately not exercised: hitting the SEC from a test suite would consume
/// somebody's fair-access quota on every CI run, which is the behaviour this connector exists to
/// avoid.
/// </remarks>
public sealed class SecEdgarEndpointTests
{
    [Theory]
    [InlineData("320193", "0000320193")]
    [InlineData("0000320193", "0000320193")]
    [InlineData("CIK0000320193", "0000320193")]
    [InlineData("cik320193", "0000320193")]
    [InlineData("  320193  ", "0000320193")]
    public void A_cik_is_normalised_to_ten_digits(string input, string expected) =>
        Assert.Equal(expected, SecEdgarEndpoints.NormaliseCik(input));

    /// <summary>
    /// EDGAR takes CIKs, not tickers. Accepting one silently would produce a 404 that looks like
    /// "this company has filed nothing".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AAPL")]
    [InlineData("12345678901")]
    [InlineData("0000000000")]
    [InlineData("123-456")]
    public void Anything_that_is_not_a_cik_is_rejected(string? input) =>
        Assert.Null(SecEdgarEndpoints.NormaliseCik(input));

    [Fact]
    public void Filings_and_profile_come_from_the_submissions_document()
    {
        Assert.Equal(
            "submissions/CIK0000320193.json",
            SecEdgarEndpoints.ForCategory(DataCategory.RegulatoryFilings, "0000320193"));

        Assert.Equal(
            "submissions/CIK0000320193.json",
            SecEdgarEndpoints.ForCategory(DataCategory.CompanyProfile, "0000320193"));
    }

    [Fact]
    public void Financial_statements_come_from_the_xbrl_facts_document() =>
        Assert.Equal(
            "api/xbrl/companyfacts/CIK0000320193.json",
            SecEdgarEndpoints.ForCategory(DataCategory.FinancialStatements, "0000320193"));

    [Fact]
    public void A_category_edgar_does_not_serve_has_no_endpoint()
    {
        Assert.Null(SecEdgarEndpoints.ForCategory(DataCategory.MarketPrices, "0000320193"));
        Assert.Null(SecEdgarEndpoints.ForCategory(DataCategory.ShippingAndLogistics, "0000320193"));
    }
}

public sealed class SecEdgarSourceTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SecEdgarSource Source = new();

    /// <summary>A connector shipping in the box does not get to switch itself on.</summary>
    [Fact]
    public void The_definition_is_registered_inactive() =>
        Assert.False(Source.Definition(Now).IsActive);

    [Fact]
    public void Edgar_is_a_primary_regulatory_source()
    {
        var source = Source.Definition(Now);

        Assert.Equal(SourceAuthority.Primary, source.Authority);
        Assert.Equal(SourceType.RegulatoryAuthority, source.Type);
        Assert.True(source.IsAuthoritative);
    }

    /// <summary>
    /// Public-domain government records, so ingestion is permitted - and the definition says so
    /// explicitly rather than leaving the registry to assume it.
    /// </summary>
    [Fact]
    public void The_recorded_licensing_permits_storage_and_automated_processing()
    {
        var source = Source.Definition(Now);

        Assert.True(source.Licensing.StorageAllowed);
        Assert.True(source.Licensing.AutomatedProcessingAllowed);
        Assert.Contains("fair-access", source.Licensing.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_definition_is_admissible_once_activated()
    {
        var source = Source.Definition(Now);
        source.Activate(Now);

        var admission = SourceAdmission.Evaluate(
            source,
            DataCategory.RegulatoryFilings,
            Region.UnitedStates);

        Assert.True(admission.IsAdmitted);
    }

    [Fact]
    public void The_definition_and_the_connector_agree_on_the_source_id() =>
        Assert.Equal(SecEdgarProvider.Id, Source.Definition(Now).Id);
}

public sealed class SecEdgarOptionsTests
{
    private static IReadOnlyList<string> Validate(SecEdgarOptions options) =>
        options
            .Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options))
            .Select(r => r.ErrorMessage ?? string.Empty)
            .ToList();

    /// <summary>
    /// A disabled connector needs no contact address, because it will never make a request.
    /// </summary>
    [Fact]
    public void A_disabled_connector_needs_no_contact_details() =>
        Assert.Empty(Validate(new SecEdgarOptions { Enabled = false }));

    /// <summary>
    /// The SEC's fair-access policy requires identification, so an enabled connector without it is
    /// a configuration error rather than a connector that quietly sends an anonymous request.
    /// </summary>
    [Fact]
    public void An_enabled_connector_requires_an_application_name_and_contact()
    {
        var errors = Validate(new SecEdgarOptions { Enabled = true });

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void A_fully_configured_connector_validates() =>
        Assert.Empty(Validate(new SecEdgarOptions
        {
            Enabled = true,
            ApplicationName = "AI Investment Analyst",
            ContactEmail = "ops@example.com",
        }));

    [Fact]
    public void A_non_https_base_address_is_rejected() =>
        Assert.Single(Validate(new SecEdgarOptions
        {
            Enabled = true,
            ApplicationName = "AI Investment Analyst",
            ContactEmail = "ops@example.com",
            BaseAddress = "http://data.sec.gov/",
        }));

    /// <summary>The header the SEC asks for: application name, then contact address.</summary>
    [Fact]
    public void The_user_agent_is_assembled_from_configuration() =>
        Assert.Equal(
            "AI Investment Analyst ops@example.com",
            new SecEdgarOptions
            {
                ApplicationName = "AI Investment Analyst",
                ContactEmail = "ops@example.com",
            }.UserAgent);
}

public sealed class SlidingWindowRateLimiterTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId Source = SourceId.Create("test-source");

    [Fact]
    public async Task Requests_within_the_quota_are_granted()
    {
        var limiter = new SlidingWindowRateLimiter();
        var quota = ProviderQuota.PerSecond(3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(await limiter.TryAcquireAsync(Source, quota, Now));
        }
    }

    [Fact]
    public async Task The_request_after_the_quota_is_refused()
    {
        var limiter = new SlidingWindowRateLimiter();
        var quota = ProviderQuota.PerSecond(2);

        await limiter.TryAcquireAsync(Source, quota, Now);
        await limiter.TryAcquireAsync(Source, quota, Now);

        Assert.False(await limiter.TryAcquireAsync(Source, quota, Now));
    }

    /// <summary>
    /// A fixed window would permit a full quota at the end of one interval and another at the
    /// start of the next - twice the declared rate across the boundary, which a provider
    /// enforcing its own limit would rightly treat as a violation.
    /// </summary>
    [Fact]
    public async Task Capacity_returns_gradually_rather_than_all_at_once()
    {
        var limiter = new SlidingWindowRateLimiter();
        var quota = ProviderQuota.PerSecond(2);

        await limiter.TryAcquireAsync(Source, quota, Now);
        await limiter.TryAcquireAsync(Source, quota, Now.AddMilliseconds(500));

        Assert.False(await limiter.TryAcquireAsync(Source, quota, Now.AddMilliseconds(900)));

        // The first grant has aged out; the second has not.
        Assert.True(await limiter.TryAcquireAsync(Source, quota, Now.AddMilliseconds(1100)));
        Assert.False(await limiter.TryAcquireAsync(Source, quota, Now.AddMilliseconds(1200)));
    }

    [Fact]
    public async Task Quotas_are_tracked_per_source()
    {
        var limiter = new SlidingWindowRateLimiter();
        var quota = ProviderQuota.PerSecond(1);
        var other = SourceId.Create("other-source");

        Assert.True(await limiter.TryAcquireAsync(Source, quota, Now));
        Assert.False(await limiter.TryAcquireAsync(Source, quota, Now));
        Assert.True(await limiter.TryAcquireAsync(other, quota, Now));
    }
}

public sealed class ProviderCatalogueTests
{
    /// <summary>
    /// Which connector answered must not depend on registration order, so a duplicate is refused
    /// where it is a configuration error rather than resolved silently where it is a mystery.
    /// </summary>
    [Fact]
    public void Two_connectors_for_one_source_is_a_configuration_error()
    {
        var capabilities = ProviderCapabilities.Create(
            [DataCategory.RegulatoryFilings],
            [Region.UnitedStates],
            ["Company"]);

        Assert.Throws<InvalidOperationException>(() =>
            new ProviderCatalogue(
            [
                new StubProvider(SecEdgarProvider.Id, capabilities),
                new StubProvider(SecEdgarProvider.Id, capabilities),
            ]));
    }

    [Fact]
    public void An_unregistered_source_resolves_to_no_connector() =>
        Assert.Null(new ProviderCatalogue([]).Find(SourceId.Create("nothing-here")));

    private sealed class StubProvider : IDataProvider
    {
        public StubProvider(SourceId sourceId, ProviderCapabilities capabilities)
        {
            SourceId = sourceId;
            Capabilities = capabilities;
        }

        public SourceId SourceId { get; }

        public ProviderCapabilities Capabilities { get; }

        public Task<ProviderResponse> FetchAsync(
            IngestionRequest request,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This stub exists to be catalogued, not called.");
    }
}
