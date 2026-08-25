using AI.Investment.Domain.Actions;

namespace AI.Investment.Application.Abstractions;

/// <summary>Assembles the policy context for an evaluation.</summary>
/// <remarks>
/// <strong>Implementations must fail closed.</strong> If configuration is missing, a store is
/// unreachable, or the kill switch state cannot be read, the correct return value is
/// <see cref="PolicyContext.FailClosed"/> - never a permissive default and never an exception
/// that a caller might catch and continue past. This is where I/O happens so that
/// <see cref="IPolicyEngine"/> can stay pure.
/// </remarks>
public interface IPolicyContextProvider
{
    Task<PolicyContext> GetAsync(CancellationToken cancellationToken = default);
}
