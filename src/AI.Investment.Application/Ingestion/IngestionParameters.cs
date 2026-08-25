using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// The ingestion request, as the safety seam sees it.
/// </summary>
/// <remarks>
/// <see cref="Describe"/> is written into the audit trail, so it states what was asked for and
/// nothing else - no URL, no headers, no token. The audit trail is append-only and cannot be
/// redacted, which makes it the last place a credential should be able to reach.
/// </remarks>
public sealed record IngestionParameters : IActionParameters
{
    public IngestionParameters(IngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Request = request;
    }

    public IngestionRequest Request { get; }

    public string Describe()
    {
        var window = Request.Window;
        var windowText = window is null ? "none" : window.ToString();

        return $"Ingest {Request.Category} for {Request.Subject} from {Request.SourceId} " +
               $"({Request.Region}), window {windowText}";
    }
}
