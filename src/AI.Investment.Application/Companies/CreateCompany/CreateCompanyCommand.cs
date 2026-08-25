namespace AI.Investment.Application.Companies.CreateCompany;

/// <summary>Request to add a company to reference data.</summary>
public sealed record CreateCompanyCommand(
    string Name,
    string Ticker,
    string? Exchange = null,
    string? Sector = null,
    string? Industry = null,
    string? Country = null,
    string? Description = null);
