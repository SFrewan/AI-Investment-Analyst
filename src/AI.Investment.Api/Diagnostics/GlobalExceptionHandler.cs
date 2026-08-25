using AI.Investment.Api.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AI.Investment.Api.Diagnostics;

/// <summary>
/// Converts any unhandled exception into an RFC 7807 <see cref="ProblemDetails"/> response.
/// </summary>
/// <remarks>
/// Two rules are deliberate. First, the response never contains an exception message, stack
/// trace or type name: this API will eventually hold capital state, and internal detail in an
/// error body is a reconnaissance gift. Second, the correlation identifier IS returned, so a
/// caller can report a failure precisely without the server having leaked anything.
/// </remarks>
internal sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.HttpContextItemKey, out var value)
            ? value as string
            : httpContext.TraceIdentifier;

        LogUnhandledException(
            exception,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            correlationId);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Detail = "The request could not be completed. Quote the correlation identifier when reporting this.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
            Instance = httpContext.Request.Path.Value,
        };

        problem.Extensions["correlationId"] = correlationId;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Source-generated logging method (CA1848).
    /// </summary>
    /// <remarks>
    /// The generator emits a cached <c>LoggerMessage</c> delegate, so the message template is
    /// parsed once at start-up rather than on every call, and the arguments are not boxed into
    /// an <c>object[]</c> when the level is disabled. That is a micro-optimisation on an error
    /// path today; it stops being one when the same pattern is used by ingestion and analysis
    /// loops running continuously.
    /// </remarks>
    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unhandled exception while processing {Method} {Path}. CorrelationId={CorrelationId}")]
    private partial void LogUnhandledException(
        Exception exception,
        string method,
        string? path,
        string? correlationId);
}
