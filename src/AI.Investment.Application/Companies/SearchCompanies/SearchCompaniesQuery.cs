namespace AI.Investment.Application.Companies.SearchCompanies;

/// <summary>Search companies by name or ticker, paged.</summary>
public sealed record SearchCompaniesQuery(string? Query = null, int Skip = 0, int Take = 25);
