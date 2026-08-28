using AI.Investment.Domain.Limits;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Abstractions;

/// <summary>Assembles the current exposure the limit engine is evaluated against.</summary>
/// <remarks>
/// Separate from the limit engine so that the engine stays a pure function of its inputs. Where the
/// numbers come from - the capital ledger, the audit trail, the current cycle - is a question about
/// storage; whether they breach a ceiling is a question about safety, and the second is the one held
/// to the highest test bar.
/// </remarks>
public interface IExposureProvider
{
    Task<ExposureSnapshot> GetAsync(Currency currency, CancellationToken cancellationToken = default);
}
