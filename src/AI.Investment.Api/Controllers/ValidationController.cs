using AI.Investment.Application.Validation;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>Whether any of this works.</summary>
/// <remarks>
/// <para>
/// Read only, like the operations endpoints, and for a stronger reason. A validation run measures the
/// platform; an endpoint that let a caller supply the window, the horizon, the threshold or the
/// benchmark would let a caller search for a flattering result and publish it. All four come from
/// configuration under change control, so this endpoint takes no parameters at all beyond the format
/// it answers in.
/// </para>
/// <para>
/// The run is a read, but not a cheap one: it walks the window, judges every prediction against the
/// point-in-time guard, resolves outcomes and reads a price series. It is meant to be called
/// occasionally by a person, not polled.
/// </para>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there is,
/// the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/validation")]
public sealed class ValidationController : ControllerBase
{
    private readonly ValidationService _validation;
    private readonly IValidationRequestFactory _requests;

    public ValidationController(ValidationService validation, IValidationRequestFactory requests)
    {
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
    }

    /// <summary>The measured performance report, as data.</summary>
    [HttpGet("report")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ValidationReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var report = await _validation
            .RunAsync(_requests.Create(), cancellationToken)
            .ConfigureAwait(false);

        return Ok(ValidationMapper.ToDto(report));
    }

    /// <summary>The same report, rendered for a human to read.</summary>
    /// <remarks>
    /// Markdown rather than JSON because the exit criterion for this phase is that a report exists
    /// <em>and has been read</em>, and the numbers here are meaningless without the methodology,
    /// gaps and limitations that surround them. A JSON payload invites a caller to extract the hit
    /// rate and discard the paragraph explaining what it is a hit rate of.
    /// </remarks>
    [HttpGet("report.md")]
    [Produces("text/markdown")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReportMarkdownAsync(CancellationToken cancellationToken = default)
    {
        var report = await _validation
            .RunAsync(_requests.Create(), cancellationToken)
            .ConfigureAwait(false);

        return Content(ValidationReportWriter.ToMarkdown(report), "text/markdown");
    }
}
