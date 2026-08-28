using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Application.Abstractions;

/// <summary>Reads and stores autonomy grants.</summary>
/// <remarks>
/// <para>
/// There is no update method and no delete method, and that is deliberate. A grant is changed by
/// revoking it and issuing another, both of which are actions under
/// <see cref="Capability.AutonomyAdministration"/>, so the history of what was permitted and when
/// stays legible. An <c>UpdateAsync</c> here would make "who widened this, and when" a question the
/// data could not answer.
/// </para>
/// <para>
/// Implementations must fail closed. If grants cannot be read the correct answer is an empty list,
/// which resolves to no autonomy at all - never a cached or assumed one.
/// </para>
/// </remarks>
public interface IAutonomyGrantStore
{
    /// <summary>Every grant that is live now for one capability in one environment.</summary>
    Task<IReadOnlyList<AutonomyGrant>> GetActiveAsync(
        Capability capability,
        string environmentName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Every grant, live or not, for an operator reading the current position.</summary>
    Task<IReadOnlyList<AutonomyGrant>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<AutonomyGrant?> FindAsync(Guid autonomyGrantId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new grant. Committed by the unit of work inside an authorisation window.</summary>
    Task AddAsync(AutonomyGrant grant, CancellationToken cancellationToken = default);
}
