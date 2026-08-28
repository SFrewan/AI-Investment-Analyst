using AI.Investment.Application.Capital;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Controllers;

/// <summary>The capital read model: balances, and the postings behind them.</summary>
/// <remarks>
/// <para>
/// Read only. There is no endpoint that posts a ledger entry, and there is no field anywhere in
/// this platform that sets a balance: a balance is a projection of immutable entries, and entries
/// are written by the execution path or not at all.
/// </para>
/// <para>
/// The report states whether the books balance rather than assuming it. Double entry is a
/// guarantee only while something checks it.
/// </para>
/// <para>
/// There is no authentication on these endpoints yet, deliberately and temporarily. Until there
/// is, the API must not be exposed beyond localhost. See docs/SECURITY.md.
/// </para>
/// </remarks>
[ApiController]
[Route("api/capital")]
[Produces("application/json")]
public sealed class CapitalController : ControllerBase
{
    private readonly ILedgerReport _ledger;

    public CapitalController(ILedgerReport ledger) =>
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <summary>Balances for one currency, and whether they sum to zero.</summary>
    [HttpGet("ledger")]
    [ProducesResponseType(typeof(LedgerReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetLedgerAsync(
        [FromQuery] string currency = "USD",
        CancellationToken cancellationToken = default)
    {
        Currency parsed;

        try
        {
            parsed = Currency.Create(currency);
        }
        catch (DomainValidationException ex)
        {
            return Problem(
                title: "The currency is not usable.",
                detail: ex.Reason,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var report = await _ledger.GetAsync(parsed, cancellationToken).ConfigureAwait(false);

        return Ok(report);
    }

    /// <summary>The postings made for one opportunity - the reconciliation view.</summary>
    [HttpGet("ledger/{opportunityId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<LedgerEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEntriesAsync(
        Guid opportunityId,
        CancellationToken cancellationToken)
    {
        if (opportunityId == Guid.Empty)
        {
            return NotFound();
        }

        var entries = await _ledger
            .GetEntriesAsync(OpportunityId.Create(opportunityId), cancellationToken)
            .ConfigureAwait(false);

        return Ok(entries);
    }
}
