using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Api.Configuration;

/// <summary>
/// Strongly-typed, validated configuration for logging and diagnostics.
/// Bound from the "Observability" configuration section and validated at application start,
/// so a misconfigured deployment fails immediately rather than at the first request.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// Logical name of this service, attached to every log event. Required.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 3)]
    public string ServiceName { get; init; } = string.Empty;

    /// <summary>
    /// Name of the HTTP header used to accept and echo a correlation identifier.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(64, MinimumLength = 3)]
    public string CorrelationIdHeader { get; init; } = "X-Correlation-ID";

    /// <summary>
    /// When true, a correlation identifier supplied by the caller is trusted and reused.
    /// When false, one is always generated server-side.
    /// </summary>
    /// <remarks>
    /// Trusting a caller-supplied value is convenient behind an internal gateway and is a
    /// log-injection vector when the API is exposed publicly. It is configuration, not a
    /// hard-coded assumption, precisely so that the production answer can differ.
    /// </remarks>
    public bool AcceptInboundCorrelationId { get; init; }
}
