using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operations;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>What the platform did while nobody was watching.</summary>
/// <remarks>
/// <para>
/// Read only, and that is the design rather than an omission. There is no endpoint that starts a
/// cycle, resumes one, resolves an escalation or issues a grant. Starting work is what watches are
/// for; issuing a grant is the most consequential write in the system and belongs behind an
/// authenticated human path that does not exist yet; and an endpoint that resolved an escalation
/// without knowing who was calling would make the record of who decided a fiction.
/// </para>
/// <para>
/// Shadow decisions are exposed because somebody has to be able to read them before arguing for a
/// promotion, and because a measurement nobody can see is a measurement nobody checks.
/// </para>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there
/// is, the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/operations")]
[Produces("application/json")]
public sealed class OperationsController : ControllerBase
{
    private const int MaxPageSize = 200;

    private readonly ICycleStore _cycles;
    private readonly IEscalationStore _escalations;
    private readonly IShadowDecisionStore _shadow;
    private readonly IAutonomyGrantStore _grants;
    private readonly IClock _clock;

    public OperationsController(
        ICycleStore cycles,
        IEscalationStore escalations,
        IShadowDecisionStore shadow,
        IAutonomyGrantStore grants,
        IClock clock)
    {
        _cycles = cycles ?? throw new ArgumentNullException(nameof(cycles));
        _escalations = escalations ?? throw new ArgumentNullException(nameof(escalations));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Cycles waiting to be worked on, oldest first.</summary>
    [HttpGet("cycles")]
    [ProducesResponseType(typeof(IReadOnlyList<CycleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCyclesAsync(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var cycles = await _cycles
            .GetRunnableAsync(Clamp(limit), _clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return Ok(cycles.Select(OperationsMapper.ToDto).ToList());
    }

    /// <summary>
    /// Escalations nobody has answered, soonest to expire first.
    /// </summary>
    /// <remarks>
    /// Ordered by expiry rather than by when they were raised, because the question that matters is
    /// which one goes stale next. An escalation answered after it expired is an answer to a
    /// different question.
    /// </remarks>
    [HttpGet("escalations")]
    [ProducesResponseType(typeof(IReadOnlyList<EscalationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEscalationsAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var outstanding = await _escalations.GetOutstandingAsync(cancellationToken).ConfigureAwait(false);

        return Ok(outstanding.Select(e => OperationsMapper.ToDto(e, now)).ToList());
    }

    /// <summary>What a higher autonomy level would have decided. Nothing here was executed.</summary>
    [HttpGet("shadow")]
    [ProducesResponseType(typeof(IReadOnlyList<ShadowDecisionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetShadowDecisionsAsync(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var decisions = await _shadow.GetRecentAsync(Clamp(limit), cancellationToken).ConfigureAwait(false);

        return Ok(decisions.Select(OperationsMapper.ToDto).ToList());
    }

    /// <summary>Every autonomy grant, live or not, newest first.</summary>
    [HttpGet("grants")]
    [ProducesResponseType(typeof(IReadOnlyList<AutonomyGrantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGrantsAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var grants = await _grants.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(grants.Select(g => OperationsMapper.ToDto(g, now)).ToList());
    }

    private static int Clamp(int limit) => limit < 1 ? 1 : Math.Min(limit, MaxPageSize);
}
