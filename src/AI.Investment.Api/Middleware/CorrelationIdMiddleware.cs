using AI.Investment.Api.Configuration;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace AI.Investment.Api.Middleware;

/// <summary>
/// Assigns a correlation identifier to every request and makes it available to logging,
/// to the response, and (from Phase 1 onward) to the audit trail.
/// </summary>
/// <remarks>
/// This exists in Phase 0 rather than later because a correlation identifier has to flow
/// through every stage of the pipeline from the very first one. Retrofitting it once
/// ingestion, analysis and action execution already exist means touching all of them.
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    /// <summary>Key under which the correlation identifier is stored in <see cref="HttpContext.Items"/>.</summary>
    public const string HttpContextItemKey = "AIInvestment.CorrelationId";

    private const int MaxInboundLength = 128;

    private readonly RequestDelegate _next;
    private readonly ObservabilityOptions _options;

    public CorrelationIdMiddleware(RequestDelegate next, IOptions<ObservabilityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = ResolveCorrelationId(context);

        context.Items[HttpContextItemKey] = correlationId;
        context.TraceIdentifier = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[_options.CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    private string ResolveCorrelationId(HttpContext context)
    {
        if (!_options.AcceptInboundCorrelationId)
        {
            return NewCorrelationId();
        }

        var inbound = context.Request.Headers[_options.CorrelationIdHeader].ToString();

        // An inbound value is untrusted input that ends up in log records. Accept it only
        // if it is short and alphanumeric; anything else is replaced rather than sanitised,
        // because a partially-sanitised identifier is not the caller's identifier anyway.
        return IsAcceptableInboundValue(inbound) ? inbound : NewCorrelationId();
    }

    private static bool IsAcceptableInboundValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxInboundLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isAllowed = char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }

    private static string NewCorrelationId() =>
        Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Pipeline registration for <see cref="CorrelationIdMiddleware"/>.</summary>
public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}
