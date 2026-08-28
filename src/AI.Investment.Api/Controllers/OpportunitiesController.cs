using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Opportunities;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>The opportunity read models: the pipeline, and the escalation queue.</summary>
/// <remarks>
/// <para>
/// <strong>Read only, deliberately.</strong> Approving an action and executing one are the two
/// operations in this platform that commit capital, and neither has an HTTP surface in this phase.
/// The reason is recorded rather than left to be discovered: an approval token is bound to the
/// exact <c>ActionProposal</c> a person was shown, proposals are not persisted yet, and a second
/// request rebuilding "the same" proposal would produce a different identity - so an endpoint pair
/// would either refuse every token or would have to loosen the binding that makes a token mean
/// anything. Persisting proposals and decisions is the prerequisite, and it belongs with the phase
/// that needs it rather than being guessed at here.
/// </para>
/// <para>
/// <c>GET /api/opportunities?status=Proposed</c> is the escalation queue: everything waiting on a
/// human, newest change first, with the risk summary and the stated confidence beside the money.
/// </para>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there
/// is, the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/opportunities")]
[Produces("application/json")]
public sealed class OpportunitiesController : ControllerBase
{
    /// <summary>The most rows one listing returns. A queue nobody can page is a queue nobody reads.</summary>
    public const int MaxLimit = 200;

    private readonly IOpportunityRepository _opportunities;

    public OpportunitiesController(IOpportunityRepository opportunities) =>
        _opportunities = opportunities ?? throw new ArgumentNullException(nameof(opportunities));

    /// <summary>Lists opportunities in one lifecycle state.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OpportunityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListAsync(
        [FromQuery] string status = nameof(OpportunityStatus.Proposed),
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<OpportunityStatus>(status, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            return Problem(
                title: "Unknown opportunity status.",
                detail: $"'{status}' is not a lifecycle state. Valid states: " +
                        $"{string.Join(", ", Enum.GetNames<OpportunityStatus>())}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (limit is < 1 or > MaxLimit)
        {
            return Problem(
                title: "The limit is out of range.",
                detail: $"A listing returns between 1 and {MaxLimit} rows.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var opportunities = await _opportunities
            .ListAsync(parsed, limit, cancellationToken)
            .ConfigureAwait(false);

        return Ok(opportunities.Select(OpportunityMapper.ToDto).ToList());
    }

    /// <summary>Fetches one opportunity, with its economics, risk and score.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OpportunityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            return NotFound();
        }

        var opportunity = await _opportunities
            .GetAsync(OpportunityId.Create(id), cancellationToken)
            .ConfigureAwait(false);

        return opportunity is null ? NotFound() : Ok(OpportunityMapper.ToDto(opportunity));
    }
}
