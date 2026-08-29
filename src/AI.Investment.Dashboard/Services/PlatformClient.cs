using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AI.Investment.Dashboard.Services;

/// <summary>
/// The one place this application talks to the platform.
/// </summary>
/// <remarks>
/// <para>
/// Every request goes through <see cref="GetAsync{T}"/>, so the credential is attached in one place,
/// the status codes are classified in one place, and no page can accidentally call an endpoint
/// without either. There is no second HTTP client anywhere in this project.
/// </para>
/// <para>
/// <strong>It reads.</strong> Operator actions belong to the existing operator console, which routes
/// them through the action gateway and the policy engine; a dashboard that grew its own write path
/// would be a second way to change the platform, and the safety argument only holds while there is
/// one.
/// </para>
/// <para>
/// <strong>Nothing from the platform's error bodies is shown.</strong> A failure becomes a status
/// classification and a localized sentence; a raw message could carry a connection string, a
/// provider's response, or a key. The status code is the whole of what this class keeps.
/// </para>
/// </remarks>
public sealed class PlatformClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly OperatorSession _session;

    public PlatformClient(HttpClient http, OperatorSession session)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>Reads one endpoint, classifying every way it can fail.</summary>
    public async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        _session.ApplyTo(request);

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ApiResult.Failed<T>(ApiFailure.Network);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, not an abandoned request. The distinction matters: an abandoned request
            // is the page moving on, and must not be reported to anybody as a failure.
            return ApiResult.Failed<T>(ApiFailure.Network);
        }

        using (response)
        {
            var failure = Classify(response.StatusCode);

            if (failure != ApiFailure.None)
            {
                return ApiResult.Failed<T>(failure);
            }

            try
            {
                var value = await response.Content
                    .ReadFromJsonAsync<T>(Json, cancellationToken)
                    .ConfigureAwait(false);

                return value is null
                    ? ApiResult.Failed<T>(ApiFailure.ServerError)
                    : ApiResult.Ok<T>(value);
            }
            catch (JsonException)
            {
                // A success status carrying something this client cannot read is a broken contract,
                // and reporting it as an empty result would render as "no data".
                return ApiResult.Failed<T>(ApiFailure.ServerError);
            }
        }
    }

    /// <summary>Whether an endpoint answers at all, without reading a body.</summary>
    /// <remarks>Used by the health indicator, which needs the status and nothing else.</remarks>
    public async Task<ApiFailure> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        _session.ApplyTo(request);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return Classify(response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return ApiFailure.Network;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiFailure.Network;
        }
    }

    internal static ApiFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => ApiFailure.Unauthorized,
        HttpStatusCode.Forbidden => ApiFailure.Forbidden,
        HttpStatusCode.NotFound => ApiFailure.NotFound,
        HttpStatusCode.TooManyRequests => ApiFailure.RateLimited,
        HttpStatusCode.BadRequest or HttpStatusCode.Conflict => ApiFailure.Refused,
        >= HttpStatusCode.InternalServerError => ApiFailure.ServerError,
        >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous => ApiFailure.None,
        _ => ApiFailure.ServerError,
    };
}
