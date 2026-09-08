using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// The safe, redacted description of one provider exchange, carried from a connector to the gateway.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate type rather than fields on <see cref="ProviderResponse"/>.</strong>
/// <c>ProviderResponse</c> is handed to the archive, and its own documentation explains why it
/// carries no request detail: the archive is long-lived, and a credential written there outlives
/// every rotation. That reasoning is sound and this type does not overturn it. It carries the
/// evidence on a distinct object that the gateway reads and the archive never sees, so the
/// separation is structural rather than a matter of remembering.
/// </para>
/// <para>
/// <strong>Allow-list, never deny-list.</strong> Only the named parameter and header keys below are
/// kept; anything else is dropped at construction. A deny-list fails open the day a vendor adds a
/// parameter, and this object feeds an append-only record that cannot be corrected afterwards.
/// </para>
/// <para>
/// <strong>No HTTP types.</strong> The status arrives as an <see cref="int"/> and the headers as
/// strings, so the application layer stores them and interprets neither -
/// <c>DataPlaneRuleTests.Application_cannot_reach_the_network</c> stays satisfied. This follows the
/// path <see cref="ITransportDiagnostic"/> already established for failure classification.
/// </para>
/// </remarks>
public sealed class RequestProvenance
{
    /// <summary>Request parameter keys that may be recorded. Everything else is dropped.</summary>
    /// <remarks>
    /// <c>api_token</c> is absent and must stay absent. EODHD puts the credential in the query
    /// string, so this list is the thing standing between that credential and an append-only table.
    /// </remarks>
    public static readonly IReadOnlySet<string> AllowedParameterKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "symbol", "from", "to", "fmt", "period", "cik", "accession",
        };

    /// <summary>Response header names that may be recorded.</summary>
    /// <remarks>
    /// Chosen for the questions an empty answer raises: was the caller throttled, is the endpoint
    /// deprecated, what did the vendor say the body was, and does the vendor have a reference we
    /// could quote in a support case. Never <c>Authorization</c>, <c>Set-Cookie</c> or anything
    /// else that carries identity.
    /// </remarks>
    public static readonly IReadOnlySet<string> AllowedHeaderNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "content-type",
            "content-length",
            "date",
            "x-ratelimit-limit",
            "x-ratelimit-remaining",
            "x-ratelimit-reset",
            "retry-after",
            "deprecation",
            "sunset",
            "x-request-id",
            "x-correlation-id",
        };

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    private RequestProvenance(
        string endpointTemplate,
        IReadOnlyDictionary<string, string> redactedParameters,
        DateTime? requestedFromUtc,
        DateTime? requestedToUtc,
        int? httpStatusCode,
        IReadOnlyDictionary<string, string> selectedHeaders,
        string? providerCorrelationId)
    {
        EndpointTemplate = endpointTemplate;
        RedactedParameters = redactedParameters;
        RequestedFromUtc = requestedFromUtc;
        RequestedToUtc = requestedToUtc;
        HttpStatusCode = httpStatusCode;
        SelectedHeaders = selectedHeaders;
        ProviderCorrelationId = providerCorrelationId;
    }

    /// <summary>The route template, e.g. <c>api/eod/{symbol}</c>. Never a resolved URI.</summary>
    public string EndpointTemplate { get; }

    public IReadOnlyDictionary<string, string> RedactedParameters { get; }

    /// <summary>The range boundaries AS SENT, not as intended.</summary>
    public DateTime? RequestedFromUtc { get; }

    public DateTime? RequestedToUtc { get; }

    public int? HttpStatusCode { get; }

    public IReadOnlyDictionary<string, string> SelectedHeaders { get; }

    public string? ProviderCorrelationId { get; }

    /// <summary>
    /// Builds a provenance record, dropping every parameter and header not on the allow-lists.
    /// </summary>
    /// <remarks>
    /// Dropping rather than throwing is deliberate for unknown keys: a vendor adding a header must
    /// not break acquisition. A key that IS on the allow-list but arrives carrying something that
    /// looks like a credential is a different matter, and the domain refuses that outright.
    /// </remarks>
    public static RequestProvenance Create(
        string endpointTemplate,
        IReadOnlyDictionary<string, string>? parameters = null,
        DateTime? requestedFromUtc = null,
        DateTime? requestedToUtc = null,
        int? httpStatusCode = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? providerCorrelationId = null)
    {
        if (string.IsNullOrWhiteSpace(endpointTemplate))
        {
            throw new ArgumentException(
                "An exchange must say which route it called, otherwise the evidence cannot show "
                + "what question was asked.",
                nameof(endpointTemplate));
        }

        return new RequestProvenance(
            endpointTemplate.Trim(),
            Filter(parameters, AllowedParameterKeys),
            requestedFromUtc,
            requestedToUtc,
            httpStatusCode,
            Filter(headers, AllowedHeaderNames),
            string.IsNullOrWhiteSpace(providerCorrelationId) ? null : providerCorrelationId.Trim());
    }

    /// <summary>The allow-listed parameters as canonical JSON, ordered so the output is stable.</summary>
    public string ParametersAsJson() => ToJson(RedactedParameters);

    /// <summary>The allow-listed headers as canonical JSON, ordered so the output is stable.</summary>
    public string HeadersAsJson() => ToJson(SelectedHeaders);

    private static ReadOnlyDictionary<string, string> Filter(
        IReadOnlyDictionary<string, string>? source,
        IReadOnlySet<string> allowed)
    {
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (source is not null)
        {
            foreach (var pair in source)
            {
                if (pair.Key is not null && allowed.Contains(pair.Key))
                {
                    kept[pair.Key.ToLowerInvariant()] = pair.Value ?? "";
                }
            }
        }

        return new ReadOnlyDictionary<string, string>(kept);
    }

    /// <summary>
    /// Canonical because it is compared, not just read: two exchanges that asked the same thing must
    /// serialise identically, so the ordering is the dictionary's and never the insertion order.
    /// </summary>
    private static string ToJson(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(
            new SortedDictionary<string, string>(
                values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            Canonical);

    /// <summary>A date formatted the way a request states it, for the parameter map.</summary>
    public static string FormatDate(DateTime value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
