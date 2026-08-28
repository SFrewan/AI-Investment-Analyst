using AI.Investment.Domain.Operations;

namespace AI.Investment.Application.Abstractions;

/// <summary>Supplies the configured concurrency and firing-rate ceilings.</summary>
/// <remarks>
/// An implementation must never signal failure by returning permissive limits. Limits that cannot
/// be read are <see cref="AdmissionLimits.FailClosed"/>, which admits nothing: a platform that
/// cannot determine how much work it is already doing must not start more.
/// </remarks>
public interface IAdmissionLimitProvider
{
    Task<AdmissionLimits> GetAsync(CancellationToken cancellationToken = default);
}
