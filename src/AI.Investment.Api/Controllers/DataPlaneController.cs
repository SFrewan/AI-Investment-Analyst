using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Freshness;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>What the data plane knows about itself: currency, activity, and failures.</summary>
/// <remarks>
/// <para>
/// Every endpoint here is read-only and therefore deliberately outside the Action/Policy seam. The
/// seam gates side effects; asking a question is not one, and auditing reads would bury the record
/// of what actually changed under a record of who looked.
/// </para>
/// <para>
/// <strong>This is the surface that makes silence legible.</strong> A platform that ingests data
/// fails in two ways: loudly, which needs no help, and quietly - a source that stopped publishing,
/// a schema that changed, a policy that has been refusing every deletion for a month. The three
/// listings below are how each of those becomes visible instead of being discovered later by an
/// analysis that returned less than it should have.
/// </para>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there is,
/// the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/data-plane")]
[Produces("application/json")]
public sealed class DataPlaneController : ControllerBase
{
    /// <summary>The most rows any of these listings will return.</summary>
    /// <remarks>
    /// A status surface is read by humans and by dashboards, and neither benefits from an
    /// unbounded page. Bounded here rather than trusted from the query string, because a caller
    /// asking for a million rows should get a hundred, not an outage.
    /// </remarks>
    public const int MaxTake = 200;

    /// <summary>The default when a caller does not say.</summary>
    public const int DefaultTake = 50;

    private readonly IFreshnessReport _freshness;
    private readonly IIngestionRunStore _runs;
    private readonly IQuarantineStore _quarantine;
    private readonly IClock _clock;

    public DataPlaneController(
        IFreshnessReport freshness,
        IIngestionRunStore runs,
        IQuarantineStore quarantine,
        IClock clock)
    {
        _freshness = freshness ?? throw new ArgumentNullException(nameof(freshness));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>How current every registered source's data is.</summary>
    /// <remarks>
    /// Ordered so it reads as a queue: what needs attention first, longest-neglected first within
    /// that. Only successful runs count as a refresh - a source refused fifty times running has not
    /// been refreshed, and reporting it as current is precisely the failure this exists to catch.
    /// </remarks>
    [HttpGet("freshness")]
    [ProducesResponseType(typeof(IReadOnlyList<FreshnessDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFreshnessAsync(CancellationToken cancellationToken)
    {
        var lines = await _freshness.GetAsync(cancellationToken).ConfigureAwait(false);

        return Ok(lines.Select(line => FreshnessMapper.ToDto(line)).ToList());
    }

    /// <summary>How current one source's data is.</summary>
    [HttpGet("freshness/{sourceId}")]
    [ProducesResponseType(typeof(FreshnessDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSourceFreshnessAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        SourceId parsed;

        try
        {
            parsed = SourceId.Create(sourceId);
        }
        catch (DomainValidationException ex)
        {
            return Problem(
                title: "The source identifier is not valid.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var line = await _freshness.GetAsync(parsed, cancellationToken).ConfigureAwait(false);

        // Null means "not registered", which is a different statement from "registered and never
        // fetched" - and the latter is what a freshness line would say.
        return line is null ? NotFound() : Ok(FreshnessMapper.ToDto(line));
    }

    /// <summary>Recent ingestion runs, including the refused ones.</summary>
    /// <remarks>
    /// <strong>Refusals are the point.</strong> A successful run is visible in the data it
    /// produced; a refused one produces nothing, and without this listing the only symptom is
    /// data that never appeared. Each carries the versioned rule that stopped it.
    /// </remarks>
    [HttpGet("runs")]
    [ProducesResponseType(typeof(IReadOnlyList<IngestionRunDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRunsAsync(
        CancellationToken cancellationToken,
        [FromQuery] int? sinceHours = null,
        [FromQuery] int? take = null)
    {
        // A week by default: long enough to cover a weekend nobody was watching, short enough that
        // the listing is about now rather than about history.
        var hours = Math.Clamp(sinceHours ?? 168, 1, 24 * 90);
        var since = _clock.UtcNow.AddHours(-hours);

        var runs = await _runs
            .GetRecentAsync(since, Clamp(take), cancellationToken)
            .ConfigureAwait(false);

        return Ok(runs.Select(run => IngestionMapper.ToDto(run)).ToList());
    }

    /// <summary>Payloads that were archived but could not be read, newest first.</summary>
    /// <remarks>
    /// The operator's queue. A source that quietly changed its schema shows up here and nowhere
    /// else: the fetch succeeded, the bytes are archived, and the only sign anything is wrong is
    /// that nothing was learned from them. No reason here contains an excerpt of a payload.
    /// </remarks>
    [HttpGet("quarantine")]
    [ProducesResponseType(typeof(IReadOnlyList<QuarantinedPayloadDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQuarantineAsync(
        CancellationToken cancellationToken,
        [FromQuery] int? take = null)
    {
        var payloads = await _quarantine
            .GetRecentAsync(Clamp(take), cancellationToken)
            .ConfigureAwait(false);

        return Ok(payloads.Select(payload => IngestionMapper.ToDto(payload)).ToList());
    }

    /// <summary>Bounds a caller's page size without rejecting them for asking too much.</summary>
    private static int Clamp(int? take) => Math.Clamp(take ?? DefaultTake, 1, MaxTake);
}
