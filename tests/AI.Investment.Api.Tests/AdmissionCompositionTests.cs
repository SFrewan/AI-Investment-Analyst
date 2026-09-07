using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// That the admission bar and the companyfacts normalizer are actually in the container.
/// </summary>
/// <remarks>
/// Both are the kind of component that is easy to write and easy to forget to register, and both
/// fail silently when missing: an unregistered normalizer means every payload of that category is
/// quarantined under a rule saying nothing could read it, and an unregistered bar means whatever
/// consumes it falls back to a default nobody chose. Asserted from the real container rather than
/// from the classes, because the failure being guarded is a wiring failure.
/// </remarks>
public sealed class AdmissionCompositionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdmissionCompositionTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void The_admission_bar_is_resolvable_and_is_the_shipped_one()
    {
        using var scope = _factory.Services.CreateScope();

        var criteria = scope.ServiceProvider.GetRequiredService<AdmissionCriteria>();

        Assert.Equal(
            AdmissionCriteria.Standard.MinimumResolvedPredictions,
            criteria.MinimumResolvedPredictions);

        Assert.Equal(AdmissionCriteria.Standard.MaximumBrierScore, criteria.MaximumBrierScore);

        // A ceiling at or above 0.25 admits a strategy that always says fifty per cent, which is a
        // strategy carrying no information at all.
        Assert.True(criteria.MaximumBrierScore < 0.25m);
    }

    /// <summary>The bar travels from configuration rather than from the type's own defaults.</summary>
    [Fact]
    public void The_bar_travels_from_configuration()
    {
        var options = new AdmissionOptions
        {
            MinimumResolvedPredictions = 250,
            MaximumBrierScore = 0.15m,
            MaximumTrialsPerSearchFamily = 2,
            MaximumEvidenceAge = TimeSpan.FromDays(30),
        };

        var criteria = options.ToCriteria();

        Assert.Equal(250, criteria.MinimumResolvedPredictions);
        Assert.Equal(0.15m, criteria.MaximumBrierScore);
        Assert.Equal(2, criteria.MaximumTrialsPerSearchFamily);
        Assert.Equal(TimeSpan.FromDays(30), criteria.MaximumEvidenceAge);
    }

    /// <summary>
    /// The evidence base is named, so a strategy can say whether it was fitted to this one.
    /// </summary>
    [Fact]
    public void The_evidence_base_is_named_in_configuration()
    {
        using var scope = _factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<AdmissionOptions>>().Value;

        Assert.False(string.IsNullOrWhiteSpace(options.EvidenceBaseFingerprint));
    }

    /// <summary>
    /// Exactly one registered normalizer claims EDGAR's financial statements, and it is the new one.
    /// </summary>
    /// <remarks>
    /// Two normalizers claiming one source and category would make the winner depend on registration
    /// order, and none would mean the endpoint the connector can already reach stays unreadable.
    /// </remarks>
    [Fact]
    public void One_registered_normalizer_reads_edgar_financial_statements()
    {
        using var scope = _factory.Services.CreateScope();

        var claiming = scope.ServiceProvider
            .GetServices<INormalizer>()
            .Where(n => n.CanNormalize(SecEdgarProvider.Id, DataCategory.FinancialStatements))
            .ToList();

        Assert.Single(claiming);
        Assert.IsType<SecEdgarCompanyFactsNormalizer>(claiming[0]);
    }

    /// <summary>The company-profile path is untouched and still belongs to the submissions reader.</summary>
    [Fact]
    public void The_submissions_normalizer_still_owns_the_company_profile()
    {
        using var scope = _factory.Services.CreateScope();

        var claiming = scope.ServiceProvider
            .GetServices<INormalizer>()
            .Where(n => n.CanNormalize(SecEdgarProvider.Id, DataCategory.CompanyProfile))
            .ToList();

        Assert.Single(claiming);
        Assert.IsType<SecEdgarSubmissionsNormalizer>(claiming[0]);
    }
}
