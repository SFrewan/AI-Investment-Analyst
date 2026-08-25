using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Common;

namespace AI.Investment.Application.Companies.SearchCompanies;

/// <summary>Searches companies. A read: no proposal, no policy, no audit - see GetCompanyHandler.</summary>
public sealed class SearchCompaniesHandler
{
    public const int MaxTake = 200;
    public const int DefaultTake = 25;

    private readonly ICompanyRepository _companies;

    public SearchCompaniesHandler(ICompanyRepository companies)
    {
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
    }

    public async Task<PagedResult<CompanyDto>> HandleAsync(
        SearchCompaniesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Clamped rather than rejected. An out-of-range page size is a caller mistake with an
        // obvious safe interpretation; refusing the request would be unhelpful, while honouring
        // take=1000000 is a denial-of-service vector against our own database.
        var skip = Math.Max(0, query.Skip);
        var take = query.Take <= 0 ? DefaultTake : Math.Min(query.Take, MaxTake);

        var total = await _companies.CountAsync(query.Query, cancellationToken).ConfigureAwait(false);

        if (total == 0)
        {
            return PagedResult.Empty<CompanyDto>(skip, take);
        }

        var companies = await _companies
            .SearchAsync(query.Query, skip, take, cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<CompanyDto>(
            companies.Select(c => c.ToDto()).ToList(),
            total,
            skip,
            take);
    }
}
