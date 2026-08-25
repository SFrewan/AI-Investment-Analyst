using AI.Investment.Domain.Companies;

namespace AI.Investment.Application.Companies;

/// <summary>Maps the company aggregate to its transport shape.</summary>
/// <remarks>
/// Hand-written rather than convention-based. At this size a mapping library would be a
/// dependency, a start-up cost and a source of runtime surprises in exchange for saving twelve
/// lines - and mapping mistakes caught by the compiler are better than mapping mistakes caught
/// by a configuration test.
/// </remarks>
public static class CompanyMapper
{
    public static CompanyDto ToDto(this Company company)
    {
        ArgumentNullException.ThrowIfNull(company);

        return new CompanyDto(
            company.Id.Value,
            company.Name,
            company.Ticker.Value,
            company.Exchange?.Code,
            company.Sector,
            company.Industry,
            company.Country,
            company.Description,
            company.CreatedAtUtc,
            company.UpdatedAtUtc);
    }
}
