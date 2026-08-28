using AI.Investment.Domain.Limits;

namespace AI.Investment.Application.Abstractions;

/// <summary>Supplies the configured limits.</summary>
/// <remarks>
/// An implementation must never signal failure by returning <see cref="LimitSet.Empty"/>. Empty
/// means "no ceilings are configured"; unavailable means "the ceilings are unknown", and the two
/// have opposite safe readings. <see cref="LimitSet.FailClosed"/> is what an implementation returns
/// when it cannot read them, and the limit engine refuses everything on it.
/// </remarks>
public interface ILimitProvider
{
    Task<LimitSet> GetAsync(CancellationToken cancellationToken = default);
}
