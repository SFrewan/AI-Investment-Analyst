using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Common;
using AI.Investment.Application.Companies;
using AI.Investment.Application.Companies.CreateCompany;
using AI.Investment.Application.Companies.GetCompany;
using AI.Investment.Application.Companies.SearchCompanies;
using AI.Investment.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>Reference data: companies.</summary>
/// <remarks>
/// <para>
/// The status codes for creation are worth reading carefully, because they are where the safety
/// architecture becomes visible to a caller:
/// </para>
/// <list type="bullet">
/// <item><c>201 Created</c> - policy permitted it and it was written.</item>
/// <item><c>202 Accepted</c> - policy requires a human decision. Nothing was written. This is
/// not an error, and returning 4xx would misrepresent it.</item>
/// <item><c>403 Forbidden</c> - policy refused it. Nothing was written.</item>
/// <item><c>409 Conflict</c> - the ticker already exists, or a duplicate request was
/// suppressed.</item>
/// </list>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there
/// is, the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/companies")]
[Produces("application/json")]
public sealed class CompaniesController : ControllerBase
{
    private readonly CreateCompanyHandler _createCompany;
    private readonly GetCompanyHandler _getCompany;
    private readonly SearchCompaniesHandler _searchCompanies;

    public CompaniesController(
        CreateCompanyHandler createCompany,
        GetCompanyHandler getCompany,
        SearchCompaniesHandler searchCompanies)
    {
        _createCompany = createCompany ?? throw new ArgumentNullException(nameof(createCompany));
        _getCompany = getCompany ?? throw new ArgumentNullException(nameof(getCompany));
        _searchCompanies = searchCompanies ?? throw new ArgumentNullException(nameof(searchCompanies));
    }

    /// <summary>Adds a company. Routed through the Action/Policy seam.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CompanyDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateCompanyCommand command,
        CancellationToken cancellationToken)
    {
        CreateCompanyResult result;

        try
        {
            result = await _createCompany.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ValidationFailedException ex)
        {
            return Problem(
                title: "The request failed validation.",
                detail: string.Join(" ", ex.Errors),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DomainException ex)
        {
            // A domain rule rejected the input - an invalid ticker, a name too long. That is the
            // caller's problem to fix, so 400 rather than 500.
            return Problem(
                title: "The request violates a business rule.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return result.Status switch
        {
            CreateCompanyStatus.Created => CreatedAtAction(
                nameof(GetByIdAsync),
                new { id = result.Company!.Id },
                result.Company),

            CreateCompanyStatus.ApprovalRequired => Problem(
                title: "Human approval is required before this action can be performed.",
                detail: result.Reason,
                statusCode: StatusCodes.Status202Accepted),

            CreateCompanyStatus.Denied => Problem(
                title: "The action was refused by policy.",
                detail: result.Reason,
                statusCode: StatusCodes.Status403Forbidden),

            CreateCompanyStatus.AlreadyExists => Problem(
                title: "A company with that ticker already exists.",
                detail: result.Reason,
                statusCode: StatusCodes.Status409Conflict),

            _ => Problem(
                title: "The request was already performed.",
                detail: result.Reason,
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>Fetches one company.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CompanyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanyDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var company = await _getCompany
            .HandleAsync(new GetCompanyQuery(id), cancellationToken)
            .ConfigureAwait(false);

        return company is null ? NotFound() : Ok(company);
    }

    /// <summary>Searches companies by name, or by exact ticker.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<CompanyDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CompanyDto>>> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] int skip = 0,
        [FromQuery] int take = SearchCompaniesHandler.DefaultTake,
        CancellationToken cancellationToken = default)
    {
        var result = await _searchCompanies
            .HandleAsync(new SearchCompaniesQuery(q, skip, take), cancellationToken)
            .ConfigureAwait(false);

        return Ok(result);
    }
}
