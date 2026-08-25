using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Companies;

namespace AI.Investment.Application.Companies.GetCompany;

/// <summary>
/// Reads one company.
/// </summary>
/// <remarks>
/// No action proposal, no policy evaluation, no audit record. A read has no effect on the
/// world, and routing reads through the safety seam would add latency and audit volume while
/// protecting nothing. The seam guards side effects; that is precisely its scope.
/// <para>
/// Read authorisation - who may see what - is a different concern that arrives with
/// authentication. Its absence is recorded in docs/SECURITY.md rather than papered over here.
/// </para>
/// </remarks>
public sealed class GetCompanyHandler
{
    private readonly ICompanyRepository _companies;

    public GetCompanyHandler(ICompanyRepository companies)
    {
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
    }

    public async Task<CompanyDto?> HandleAsync(
        GetCompanyQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.CompanyId == Guid.Empty)
        {
            return null;
        }

        var company = await _companies
            .GetByIdAsync(CompanyId.Create(query.CompanyId), cancellationToken)
            .ConfigureAwait(false);

        return company?.ToDto();
    }
}
