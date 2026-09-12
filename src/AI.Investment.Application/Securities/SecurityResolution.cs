using System.Collections.ObjectModel;
using AI.Investment.Domain.Securities;

namespace AI.Investment.Application.Securities;

/// <summary>Why an identifier resolved to no security. Exactly one reason, never a default.</summary>
/// <remarks>
/// <para>
/// The reasons are deliberately not interchangeable. "Nobody ever asserted a ticker interval" and
/// "this symbol was asserted but not on that date" are different states of the evidence, and a
/// caller that cannot tell them apart cannot tell a gap in acquisition from a gap in history.
/// </para>
/// <para>
/// <strong>There is no reason for ambiguity.</strong> More than one candidate is a distinct result -
/// <see cref="SecurityResolution.Ambiguous"/> - because it carries the candidates, and letting the
/// same condition arrive both as a result and as a reason would give a caller two shapes to handle
/// for one thing.
/// </para>
/// <para>
/// <strong>There is no reason for a missing security either.</strong> An identifier assertion is an
/// owned row of the security that carries it and cannot exist without one, so "the identifier
/// resolved but its security is gone" is unreachable rather than unhandled.
/// </para>
/// </remarks>
public enum SecurityResolutionFailure
{
    /// <summary>Unset. Present so the default value is representable and obviously wrong.</summary>
    None = 0,

    /// <summary>The identifier value was blank. Nothing was searched for.</summary>
    NoIdentifier = 1,

    /// <summary>
    /// The kind names no identifier system - <see cref="SecurityIdentifierKind.Unknown"/>, which the
    /// domain refuses to assert in the first place.
    /// </summary>
    UnsupportedIdentifierKind = 2,

    /// <summary>
    /// No dated assertion of this kind is held at all, so no instant can be answered for it.
    /// </summary>
    /// <remarks>
    /// This is the state D6 records for tickers: historical ticker validity is evidenced for 0 of
    /// 400 members, because no held source dates a symbol change. A ticker query therefore cannot be
    /// answered from evidence, and saying so is the correct answer rather than a failure to find
    /// one.
    /// </remarks>
    NoTemporalIdentifierEvidence = 3,

    /// <summary>Assertions of this kind exist, but none carries this value.</summary>
    IdentifierNotPersisted = 4,

    /// <summary>This kind and value are asserted, but by a different source.</summary>
    /// <remarks>
    /// A source is part of the claim, not a label on it. EODHD asserting <c>TBCH.US</c> says nothing
    /// about what an issuer asserted, and resolving across the two would merge two parties' claims
    /// into one.
    /// </remarks>
    NoMatchingSource = 5,

    /// <summary>
    /// This source asserts this identifier, but its validity interval does not cover the instant.
    /// </summary>
    OutsideIdentifierValidity = 6,
}

/// <summary>
/// The answer to "which security did this identifier denote at this instant?".
/// </summary>
/// <remarks>
/// <para>
/// Three outcomes and no fourth. There is no null, no "best match", no ranking and no fallback to
/// current state: a caller must handle not knowing, and must handle the evidence disagreeing with
/// itself, because both are real states of a point-in-time reference model.
/// </para>
/// <para>
/// Closed by construction - the constructor is private and the three factories are the only way to
/// make one - so a fourth outcome cannot be added from outside this type.
/// </para>
/// </remarks>
public sealed class SecurityResolution
{
    private SecurityResolution(
        SecurityId? securityId,
        SecurityResolutionFailure failure,
        IReadOnlyList<SecurityId> candidates)
    {
        SecurityId = securityId;
        Failure = failure;
        Candidates = candidates;
    }

    /// <summary>The security, when exactly one assertion matched.</summary>
    public SecurityId? SecurityId { get; }

    /// <summary>Why nothing matched, when nothing did.</summary>
    public SecurityResolutionFailure Failure { get; }

    /// <summary>Every candidate, when more than one matched. Never narrowed to a winner.</summary>
    public IReadOnlyList<SecurityId> Candidates { get; }

    public bool IsResolved => SecurityId.HasValue;

    public bool IsAmbiguous => Candidates.Count > 1;

    public bool IsUnresolved => !IsResolved && !IsAmbiguous;

    /// <summary>Exactly one assertion matched the kind, value, source and instant.</summary>
    public static SecurityResolution Resolved(SecurityId securityId) =>
        new(securityId, SecurityResolutionFailure.None, []);

    /// <summary>Nothing matched, and the reason says which kind of nothing.</summary>
    public static SecurityResolution Unresolved(SecurityResolutionFailure failure)
    {
        if (failure == SecurityResolutionFailure.None)
        {
            throw new ArgumentException(
                "An unresolved result states why. Constructing one with no reason would hide the "
                    + "difference between evidence that is absent and evidence that is out of range.",
                nameof(failure));
        }

        return new SecurityResolution(null, failure, []);
    }

    /// <summary>
    /// More than one assertion matched. Every candidate is returned and none is chosen.
    /// </summary>
    /// <remarks>
    /// Two securities claiming one identifier at one instant is a real contradiction in the
    /// evidence. Picking the newest, the oldest, or whichever the database returned first would
    /// resolve it by accident and hide that it happened.
    /// </remarks>
    public static SecurityResolution Ambiguous(IEnumerable<SecurityId> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var list = new ReadOnlyCollection<SecurityId>(candidates.ToList());

        if (list.Count < 2)
        {
            throw new ArgumentException(
                "Ambiguity needs at least two candidates. One candidate is a resolution and none is "
                    + "an absence; calling either ambiguous would misreport the evidence.",
                nameof(candidates));
        }

        return new SecurityResolution(null, SecurityResolutionFailure.None, list);
    }
}
