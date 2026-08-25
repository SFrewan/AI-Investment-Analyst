namespace AI.Investment.Application.Companies;

/// <summary>A company as returned across the application boundary.</summary>
/// <remarks>
/// A separate shape from the <c>Company</c> aggregate on purpose. The aggregate protects its
/// invariants with private setters and value objects; serialising it directly would both expose
/// internal structure and couple the wire format to the domain model, so that a domain
/// refactor becomes a breaking API change.
/// </remarks>
public sealed record CompanyDto(
    Guid Id,
    string Name,
    string Ticker,
    string? Exchange,
    string? Sector,
    string? Industry,
    string? Country,
    string? Description,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
