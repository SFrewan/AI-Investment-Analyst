using AI.Investment.Domain.Common;

namespace AI.Investment.Application.Abstractions;

/// <summary>The correlation identifier for the operation currently in flight.</summary>
/// <remarks>
/// Supplied by whatever started the work - an HTTP request today, a scheduled trigger or an
/// operating cycle later - so that a proposal, its decision, its execution and its audit
/// records all carry the same identifier.
/// </remarks>
public interface ICorrelationContext
{
    CorrelationId Current { get; }
}
