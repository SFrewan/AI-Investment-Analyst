using AI.Investment.Domain.Autonomy;

namespace AI.Investment.Application.Abstractions;

/// <summary>Stores promotion warrants.</summary>
/// <remarks>
/// <para>
/// Append, read and revoke. There is no update, because a warrant that could be edited after it was
/// issued would be a permission whose terms drift away from the evidence and the signature behind
/// them - and the whole reason a warrant exists is that those three things stay together.
/// </para>
/// <para>
/// Revocation is a mutation of two fields on an existing row rather than a new one, so that a grant
/// referring to a warrant refers to the same warrant afterwards. A revoked warrant is still the
/// record of what was permitted and when it stopped being permitted.
/// </para>
/// </remarks>
public interface IPromotionWarrantStore
{
    Task AddAsync(PromotionWarrant warrant, CancellationToken cancellationToken = default);

    Task<PromotionWarrant?> FindAsync(Guid promotionWarrantId, CancellationToken cancellationToken = default);

    /// <summary>Warrants that are neither revoked nor expired, for one capability and environment.</summary>
    Task<IReadOnlyList<PromotionWarrant>> GetActiveAsync(
        Domain.Enums.Capability capability,
        string environmentName,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Every warrant ever issued, newest first. The record somebody audits.</summary>
    Task<IReadOnlyList<PromotionWarrant>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stores live-venue authorisations.</summary>
/// <remarks>
/// The most consequential table in the system, and the one that is expected to stay empty. It exists
/// so that the day somebody wants to move real money, the record they have to create already has a
/// shape, an expiry, two signature columns and an audit trail - rather than being designed under the
/// pressure of wanting the answer to be yes.
/// </remarks>
public interface ILiveVenueAuthorizationStore
{
    Task AddAsync(LiveVenueAuthorization authorization, CancellationToken cancellationToken = default);

    Task<LiveVenueAuthorization?> FindAsync(
        Guid liveVenueAuthorizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The authorisation covering one venue in one environment, if there is one.
    /// </summary>
    /// <remarks>
    /// Returns the record whether or not it is withdrawn or expired, because the gate reports
    /// <em>which</em> refusal applies and cannot do that if the store has already filtered the row
    /// away. "There is no authorisation" and "the authorisation expired last week" are different
    /// things to tell somebody.
    /// </remarks>
    Task<LiveVenueAuthorization?> FindForAsync(
        string venueId,
        string environmentName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LiveVenueAuthorization>> GetAllAsync(CancellationToken cancellationToken = default);
}
