using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Sources;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using AI.Investment.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>The source registry: where information may come from, and on what terms.</summary>
/// <remarks>
/// <para>
/// <strong>Activation is the consequential operation here</strong>, and its status codes say so.
/// From the moment a source is active, its content becomes things the platform believes, so the
/// act goes through the Action/Policy seam like any other side effect and can be refused, deferred
/// for approval, or denied on licensing grounds by the domain itself.
/// </para>
/// <list type="bullet">
/// <item><c>200 OK</c> - activated, or already active.</item>
/// <item><c>202 Accepted</c> - policy requires a human decision. Nothing changed. Not an error.</item>
/// <item><c>403 Forbidden</c> - refused. Either policy denied it, or the source's terms permit
/// neither storage nor automated processing, which is a domain rule rather than a policy one.</item>
/// <item><c>404 Not Found</c> - not registered.</item>
/// </list>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there
/// is, the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/sources")]
[Produces("application/json")]
public sealed class SourcesController : ControllerBase
{
    private readonly ISourceRegistry _registry;
    private readonly ActivateSourceHandler _activate;

    public SourcesController(ISourceRegistry registry, ActivateSourceHandler activate)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
    }

    /// <summary>Every registered source, active or not.</summary>
    /// <remarks>
    /// Inactive sources are included. A registry listing that showed only what is switched on
    /// would hide the thing an operator most often wants to find: a source that exists and is not
    /// being used.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SourceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllAsync(CancellationToken cancellationToken)
    {
        var sources = await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(sources
            .OrderBy(s => s.Id.Value, StringComparer.Ordinal)
            .Select(source => SourceMapper.ToDto(source))
            .ToList());
    }

    /// <summary>One source, including its licensing terms.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(SourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(string id, CancellationToken cancellationToken)
    {
        var problem = Validate(id, out var sourceId);

        if (problem is not null)
        {
            return problem;
        }

        var source = await _registry.GetByIdAsync(sourceId, cancellationToken).ConfigureAwait(false);

        return source is null ? NotFound() : Ok(SourceMapper.ToDto(source));
    }

    /// <summary>Activates a source. Routed through the Action/Policy seam.</summary>
    /// <remarks>
    /// The deliberate act that makes a source usable. The domain does the refusing:
    /// <c>DataSource.Activate</c> rejects terms that permit neither storage nor automated
    /// processing, so a licensing failure surfaces even if some future caller bypassed this
    /// handler.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <strong>Authenticated as of development block 1.</strong> Activating a source is what makes
    /// the platform start fetching from it, and it was anonymous until there was an identity to
    /// record. It requires the same privilege as creating a watch, because the two together are
    /// what point the platform at something.
    /// </para>
    /// </remarks>
    [HttpPost("{id}/activation")]
    [Authorize(Policy = OperatorPolicies.AdministerWatches)]
    [ProducesResponseType(typeof(ActivateSourceResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ActivateSourceResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        var problem = Validate(id, out var sourceId);

        if (problem is not null)
        {
            return problem;
        }

        var result = await _activate.HandleAsync(sourceId, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            ActivateSourceStatus.Activated or ActivateSourceStatus.AlreadyActive => Ok(result),

            // Not an error. Policy requires a human decision, nothing was written, and a 4xx would
            // tell the caller they did something wrong.
            ActivateSourceStatus.ApprovalRequired => Accepted(result),

            ActivateSourceStatus.NotFound => Problem(
                title: "The source is not registered.",
                detail: result.Reason,
                statusCode: StatusCodes.Status404NotFound),

            // Denied, and anything a later build adds. Refusing by default is the correct reading
            // of an outcome this build does not recognise.
            _ => Problem(
                title: "Activation was refused.",
                detail: result.Reason,
                statusCode: StatusCodes.Status403Forbidden),
        };
    }

    /// <summary>
    /// Parses a source identifier, producing the 400 response when it is not one.
    /// </summary>
    /// <remarks>
    /// A malformed identifier is a bad request, not a missing resource. Returning 404 would tell a
    /// caller that their well-formed id does not exist, which is a different and untrue statement -
    /// and one that sends them looking in the registry rather than at what they sent.
    /// </remarks>
    private ObjectResult? Validate(string id, out SourceId sourceId)
    {
        try
        {
            sourceId = SourceId.Create(id);

            return null;
        }
        catch (DomainValidationException ex)
        {
            sourceId = null!;

            return Problem(
                title: "The source identifier is not valid.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
