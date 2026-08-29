using System.ComponentModel.DataAnnotations;
using AI.Investment.Api.Security;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operators;
using AI.Investment.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>What a named, authenticated person may do to a running platform.</summary>
/// <remarks>
/// <para>
/// Phase 6 left these out with a reason: *"an endpoint that resolved an escalation without knowing
/// who was calling would make the record of who decided a fiction."* Now there is an identity, so
/// there are endpoints - and every one of them carries that identity into the audit trail.
/// </para>
/// <para>
/// <strong>No business logic lives here.</strong> Each action validates its request shape, calls one
/// method on <see cref="OperatorConsole"/> and maps the outcome to a status code. Every decision -
/// whether the operator may, whether policy permits, whether the domain allows it - is made below
/// this class, so a second caller that is not HTTP gets the same answers.
/// </para>
/// <para>
/// <strong>There is no approve.</strong> An approval token binds to the identity of the exact
/// proposal a person was shown, and proposals are not persisted, so an approve endpoint would either
/// refuse every token or would have to loosen the binding that makes a token mean anything. Phase 5
/// recorded that and named its prerequisite; rejecting needs no token and is here.
/// </para>
/// <para>
/// <strong>There is no disengage.</strong> The policy engine denies every action while the switch is
/// engaged, so a disengage proposal would be refused by the state it exists to clear - and the only
/// implementation that would work is one that bypassed the gate. Disengaging stays out of band, with
/// whoever has database or environment access.
/// </para>
/// </remarks>
[ApiController]
[Route("api/operator")]
[Produces("application/json")]
public sealed class OperatorController : ControllerBase
{
    private readonly OperatorConsole _console;
    private readonly IOperatorContext _operators;

    public OperatorController(OperatorConsole console, IOperatorContext operators)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _operators = operators ?? throw new ArgumentNullException(nameof(operators));
    }

    /// <summary>Who the platform thinks is calling, and what they may do.</summary>
    /// <remarks>
    /// The console's own identity check. It requires the weakest policy rather than none, because an
    /// endpoint any authenticated caller could reach is one that grants a privilege by existing.
    /// </remarks>
    [HttpGet("whoami")]
    [Authorize(AuthenticationSchemes = OperatorAuthentication.Scheme)]
    [ProducesResponseType(typeof(OperatorIdentityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult WhoAmI()
    {
        var identity = _operators.Current;

        return identity is null
            ? Unauthorized()
            : Ok(new OperatorIdentityDto(
                identity.Id,
                identity.DisplayName,
                identity.Privileges.Select(privilege => privilege.ToString()).ToList()));
    }

    /// <summary>Refuses an opportunity, with a reason that is kept and later measured.</summary>
    [HttpPost("opportunities/{id:guid}/rejection")]
    [Authorize(Policy = OperatorPolicies.DecideOpportunities)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectAsync(
        Guid id,
        [FromBody] RejectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Map(await _console
            .RejectOpportunityAsync(id, request.Reason, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Records that a person has seen an escalation and is dealing with it.</summary>
    [HttpPost("escalations/{id:guid}/acknowledgement")]
    [Authorize(Policy = OperatorPolicies.AnswerEscalations)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcknowledgeAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        Map(await _console.AcknowledgeEscalationAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>Records that an escalation has been dealt with, and how.</summary>
    [HttpPost("escalations/{id:guid}/resolution")]
    [Authorize(Policy = OperatorPolicies.AnswerEscalations)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResolveAsync(
        Guid id,
        [FromBody] ResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Map(await _console
            .ResolveEscalationAsync(id, request.Resolution, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Engages the kill switch. One way, and audited under the operator's own name.
    /// </summary>
    /// <remarks>
    /// A denial here usually means the switch is already engaged, which is the outcome the caller
    /// wanted. The denial is audited either way, so a second attempt during an incident is visible
    /// afterwards.
    /// </remarks>
    [HttpPost("kill-switch/engagement")]
    [Authorize(Policy = OperatorPolicies.AdministerKillSwitch)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EngageKillSwitchAsync(
        [FromBody] KillSwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Map(await _console
            .EngageKillSwitchAsync(request.Reason, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Puts a scheduled watch on an instrument, which is how the observation window is pointed at
    /// something.
    /// </summary>
    [HttpPost("watches")]
    [Authorize(Policy = OperatorPolicies.AdministerWatches)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateWatchAsync(
        [FromBody] ScheduledWatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<Capability>(request.Capability, ignoreCase: true, out var capability) ||
            !Enum.IsDefined(capability))
        {
            return BadRequest(new OperatorOutcomeDto(
                nameof(OperatorOutcomeStatus.Refused),
                $"'{request.Capability}' is not a capability a watch can run under."));
        }

        var definition = new ScheduledWatchDefinition(
            request.Name,
            request.TargetKind,
            request.TargetIdentifier,
            TimeSpan.FromMinutes(request.IntervalMinutes),
            TimeSpan.FromMinutes(request.CooldownMinutes),
            capability,
            request.CycleTemplate);

        return Map(await _console
            .CreateScheduledWatchAsync(definition, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Switches a scheduled watch off. The reversal of creating one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disable, not delete. The row, its stated reason and its firing history stay where they are;
    /// only <c>Enabled</c> changes, and the store that feeds the trigger evaluator already filters
    /// on it. There is deliberately no delete: a watch that never existed and a watch somebody
    /// stopped are different facts, and only one of them can be audited.
    /// </para>
    /// <list type="bullet">
    /// <item><c>200 OK</c> - disabled, or already disabled. Either way the watch is off.</item>
    /// <item><c>400 Bad Request</c> - no reason was given.</item>
    /// <item><c>401 / 403</c> - not authenticated, or without <c>AdministerWatches</c>.</item>
    /// <item><c>404 Not Found</c> - no such watch.</item>
    /// <item><c>409 Conflict</c> - policy denied it, or requires an approval this path cannot
    /// supply. Nothing changed.</item>
    /// </list>
    /// </remarks>
    [HttpPost("watches/{id:guid}/disablement")]
    [Authorize(Policy = OperatorPolicies.AdministerWatches)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DisableWatchAsync(
        Guid id,
        [FromBody] WatchDisablementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Map(await _console
            .DisableScheduledWatchAsync(id, request.Reason, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Puts a scheduled watch on a different interval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the interval moves. The watch's creation time, last firing, fire count, cooldown and
    /// enabled state are all left as they are, so its history survives and setting the interval
    /// back restores the schedule completely. Both changes are audited, which is what makes a
    /// temporary schedule an auditable act rather than a quiet edit.
    /// </para>
    /// <list type="bullet">
    /// <item><c>200 OK</c> - rescheduled, or already on that interval.</item>
    /// <item><c>400 Bad Request</c> - no reason, an interval the domain refuses, or a watch that
    /// waits for something other than a schedule.</item>
    /// <item><c>401 / 403</c> - not authenticated, or without <c>AdministerWatches</c>.</item>
    /// <item><c>404 Not Found</c> - no such watch.</item>
    /// <item><c>409 Conflict</c> - policy denied it. Nothing changed.</item>
    /// </list>
    /// </remarks>
    [HttpPost("watches/{id:guid}/schedule")]
    [Authorize(Policy = OperatorPolicies.AdministerWatches)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(OperatorOutcomeDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RescheduleWatchAsync(
        Guid id,
        [FromBody] WatchScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Map(await _console
            .RescheduleWatchAsync(
                id,
                TimeSpan.FromMinutes(request.IntervalMinutes),
                request.Reason,
                cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// The outcome as a status code, with each refusal kept distinct.
    /// </summary>
    /// <remarks>
    /// Collapsing these would cost an operator an incident: a privilege problem that looked like a
    /// login problem sends somebody to re-enter a key that was fine, and a policy denial that looked
    /// like a bad request sends them to change the request instead of the policy.
    /// </remarks>
    private IActionResult Map(OperatorOutcome outcome)
    {
        var body = new OperatorOutcomeDto(outcome.Status.ToString(), outcome.Reason);

        return outcome.Status switch
        {
            OperatorOutcomeStatus.Done => Ok(body),
            OperatorOutcomeStatus.DuplicateSuppressed => Ok(body),
            OperatorOutcomeStatus.NotAuthenticated => Unauthorized(),
            OperatorOutcomeStatus.NotPermitted => StatusCode(StatusCodes.Status403Forbidden, body),
            OperatorOutcomeStatus.NotFound => NotFound(body),
            OperatorOutcomeStatus.Refused => BadRequest(body),
            OperatorOutcomeStatus.DeniedByPolicy => StatusCode(StatusCodes.Status409Conflict, body),
            OperatorOutcomeStatus.ApprovalRequired => StatusCode(StatusCodes.Status409Conflict, body),

            // Unreachable while the enum has the members it has. A status nobody mapped is refused
            // rather than reported as success.
            _ => StatusCode(StatusCodes.Status409Conflict, body),
        };
    }
}

/// <summary>Who is calling, as the console sees them.</summary>
public sealed record OperatorIdentityDto(string Id, string DisplayName, IReadOnlyList<string> Privileges);

/// <summary>What an operator action did, and why it did not.</summary>
public sealed record OperatorOutcomeDto(string Status, string Reason);

/// <summary>Why an opportunity is being refused.</summary>
public sealed record RejectionRequest
{
    [Required]
    [MaxLength(1000)]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>What was done about an escalation.</summary>
public sealed record ResolutionRequest
{
    [Required]
    [MaxLength(500)]
    public string Resolution { get; init; } = string.Empty;
}

/// <summary>Why the kill switch is being engaged.</summary>
public sealed record KillSwitchRequest
{
    [Required]
    [MaxLength(500)]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>An instrument to review on a schedule.</summary>
public sealed record ScheduledWatchRequest
{
    [Required]
    [MaxLength(120)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [MaxLength(60)]
    public string TargetKind { get; init; } = "Security";

    [Required]
    [MaxLength(200)]
    public string TargetIdentifier { get; init; } = string.Empty;

    [Range(1, 100000)]
    public int IntervalMinutes { get; init; } = 360;

    [Range(1, 100000)]
    public int CooldownMinutes { get; init; } = 60;

    [Required]
    [MaxLength(60)]
    public string Capability { get; init; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string CycleTemplate { get; init; } = string.Empty;
}

/// <summary>Why a watch is being switched off.</summary>
public sealed record WatchDisablementRequest
{
    /// <remarks>
    /// Bounded to the length the domain keeps (<c>Watch.MaxNameLength</c>), so an over-long reason
    /// comes back as a clean 400 rather than being silently truncated into the record.
    /// </remarks>
    [Required]
    [MaxLength(120)]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>How often a watch should run, and why that is changing.</summary>
public sealed record WatchScheduleRequest
{
    /// <remarks>
    /// The same bound the create request uses, so the two cannot disagree about what a schedule
    /// may be. The domain refuses zero or negative independently.
    /// </remarks>
    [Range(1, 100000)]
    public int IntervalMinutes { get; init; }

    /// <remarks>
    /// Bounded to the length the domain keeps for a watch's own text, so an over-long reason is a
    /// clean 400 rather than a silent truncation.
    /// </remarks>
    [Required]
    [MaxLength(120)]
    public string Reason { get; init; } = string.Empty;
}
