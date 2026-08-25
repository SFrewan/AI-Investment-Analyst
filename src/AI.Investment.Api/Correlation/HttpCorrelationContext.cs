using AI.Investment.Api.Middleware;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Common;

namespace AI.Investment.Api.Correlation;

/// <summary>
/// Supplies the correlation identifier assigned to the current HTTP request.
/// </summary>
/// <remarks>
/// The adapter between the transport and the application. When background processing arrives,
/// its own implementation supplies a cycle's identifier instead, and nothing in the application
/// or domain changes - which is why the application depends on
/// <see cref="ICorrelationContext"/> rather than on <c>HttpContext</c>.
/// </remarks>
public sealed class HttpCorrelationContext : ICorrelationContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCorrelationContext(IHttpContextAccessor accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public CorrelationId Current
    {
        get
        {
            var items = _accessor.HttpContext?.Items;

            if (items is not null &&
                items.TryGetValue(CorrelationIdMiddleware.HttpContextItemKey, out var value) &&
                value is string existing)
            {
                return CorrelationId.Create(existing);
            }

            // No request context - a startup task, or a test resolving the service directly.
            // A fresh identifier is correct: the work still needs to be traceable, and it is
            // genuinely not part of any request.
            return CorrelationId.New();
        }
    }
}
