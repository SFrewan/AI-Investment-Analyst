using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Autonomy;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>Whether the platform may act while nobody is watching, and why not.</summary>
/// <remarks>
/// <para>
/// Read only, and more deliberately so than any other controller here. There is no endpoint that
/// issues a warrant, writes a grant or authorises a venue. Those are decisions with a person's name
/// on them, and an HTTP endpoint has no name attached to it until there is authentication - which
/// there is not. Adding them before then would create exactly the shape this whole phase exists to
/// prevent: a permission that can be obtained by whoever can reach the port.
/// </para>
/// <para>
/// What it does expose is the refusal. <c>GET /api/autonomy/promotion</c> answers the question
/// "could this capability be promoted, and if not, what is missing" - which today is a list, and is
/// the most useful thing this API can say.
/// </para>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there is,
/// the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/autonomy")]
[Produces("application/json")]
public sealed class AutonomyController : ControllerBase
{
    private readonly PromotionService _promotion;
    private readonly LiveVenueService _liveVenue;
    private readonly IPromotionWarrantStore _warrants;
    private readonly ILiveVenueAuthorizationStore _authorizations;
    private readonly IClock _clock;

    public AutonomyController(
        PromotionService promotion,
        LiveVenueService liveVenue,
        IPromotionWarrantStore warrants,
        ILiveVenueAuthorizationStore authorizations,
        IClock clock)
    {
        _promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        _liveVenue = liveVenue ?? throw new ArgumentNullException(nameof(liveVenue));
        _warrants = warrants ?? throw new ArgumentNullException(nameof(warrants));
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Whether the measured evidence justifies unattended execution for a capability.</summary>
    [HttpGet("promotion")]
    [ProducesResponseType(typeof(PromotionAssessmentDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPromotionAsync(
        [FromQuery] Capability capability = Capability.SimulatedExecution,
        CancellationToken cancellationToken = default)
    {
        var assessment = await _promotion
            .AssessAsync(capability, AutonomyMode.AutoExecuteBounded, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new PromotionAssessmentDto(
            assessment.Capability.ToString(),
            assessment.ProposedMode.ToString(),
            assessment.IsJustified,
            assessment.IsJustified ? "PROMOTION JUSTIFIED" : "PROMOTION NOT JUSTIFIED",
            assessment.ValidationRunId,
            assessment.BenchmarkFingerprint,
            assessment.AssessedAtUtc,
            assessment.Refusals.Select(refusal => refusal.ToString()).ToList(),
            assessment.Reasons));
    }

    /// <summary>Every warrant ever issued. Expected to be empty.</summary>
    [HttpGet("warrants")]
    [ProducesResponseType(typeof(IReadOnlyList<PromotionWarrantDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWarrantsAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var warrants = await _warrants.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(warrants
            .Select(warrant => new PromotionWarrantDto(
                warrant.PromotionWarrantId,
                warrant.Capability.ToString(),
                warrant.ActionType,
                warrant.EnvironmentName,
                warrant.MaxMode.ToString(),
                warrant.IssuedBy,
                warrant.ValidationRunId,
                warrant.IssuedAtUtc,
                warrant.ExpiresAtUtc,
                warrant.IsActive(now)))
            .ToList());
    }

    /// <summary>Whether a venue may be activated. Expected to refuse.</summary>
    [HttpGet("live-venue")]
    [ProducesResponseType(typeof(LiveVenueDecisionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLiveVenueAsync(
        [FromQuery] string venueId,
        [FromQuery] string environment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(venueId) || string.IsNullOrWhiteSpace(environment))
        {
            return BadRequest(new { error = "venueId and environment are both required." });
        }

        var decision = await _liveVenue
            .EvaluateAsync(venueId, environment, fromConfiguration: false, cancellationToken)
            .ConfigureAwait(false);

        var authorizations = await _authorizations.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new LiveVenueDecisionDto(
            venueId,
            environment,
            decision.MayActivate,
            decision.Refusal.ToString(),
            decision.Explanation,
            authorizations.Count));
    }
}

/// <summary>Whether a capability could be promoted, and what is missing.</summary>
public sealed record PromotionAssessmentDto(
    string Capability,
    string ProposedMode,
    bool IsJustified,
    string Verdict,
    Guid? ValidationRunId,
    string? BenchmarkFingerprint,
    DateTime AssessedAtUtc,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Reasons);

/// <summary>A warrant, as data.</summary>
public sealed record PromotionWarrantDto(
    Guid PromotionWarrantId,
    string Capability,
    string? ActionType,
    string EnvironmentName,
    string MaxMode,
    string IssuedBy,
    Guid ValidationRunId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc,
    bool IsActive);

/// <summary>The live-venue gate's answer.</summary>
public sealed record LiveVenueDecisionDto(
    string VenueId,
    string EnvironmentName,
    bool MayActivate,
    string Refusal,
    string Explanation,
    int AuthorizationsOnRecord)
{
    /// <summary>Stated in words, so a reader is never left inferring it from a boolean.</summary>
    public string Summary => MayActivate
        ? "LIVE VENUE AUTHORISED"
        : string.Create(CultureInfo.InvariantCulture, $"LIVE VENUE NOT AUTHORISED: {Refusal}");
}
