using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Universe;

/// <summary>
/// That one security was in one sealed universe version, at these cohort cuts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Cuts, not a span.</strong> This records which cohort cuts the member was present at, and
/// nothing else. <c>MembershipSpan.Derive</c> turns those cuts plus the universe's window into a
/// <c>(from, to)</c> pair on read. Storing the span instead would freeze a derived value as
/// evidence, and would have to be rewritten if the derivation were ever corrected - which is
/// precisely the mutation the whole reference model exists to remove.
/// </para>
/// <para>
/// <strong>Membership is not a statement that the security still trades.</strong> A member acquired,
/// delisted or wound up in 2023 stays in the universe it belonged to, with the cuts it held. The
/// survivorship doctrine depends on exactly that: the members with short series are the ones a
/// universe built from frames exists to contain, and dropping them would flatter every measurement
/// taken afterwards.
/// </para>
/// <para>
/// <strong>Immutable and append-only.</strong> There is no mutator here and no correction path. A
/// membership that turned out to be wrong belongs to a universe whose content has changed, and a
/// universe whose content has changed is a new fingerprint and a new set of rows.
/// </para>
/// <para>
/// <strong>It names a security, never a company and never a symbol.</strong> The sealed manifests
/// key members by CIK, which identifies a legal entity rather than an instrument - one CIK can issue
/// several securities. Resolving CIK to a security runs through <c>Company</c>, which keeps legal
/// identity, and that resolution is a later stage's work. This type takes the resolved
/// <see cref="SecurityId"/> and takes nothing else.
/// </para>
/// </remarks>
public sealed class UniverseMembership
{
    private readonly List<DateOnly> _cohortCuts = [];

    private UniverseMembership(
        Guid id,
        UniverseId universeId,
        SecurityId securityId,
        IEnumerable<DateOnly> cohortCuts,
        DateTime recordedAtUtc)
    {
        Id = id;
        UniverseId = universeId;
        SecurityId = securityId;
        _cohortCuts.AddRange(cohortCuts);
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private UniverseMembership() => UniverseId = null!;

    public Guid Id { get; private set; }

    /// <summary>The sealed version this membership belongs to.</summary>
    public UniverseId UniverseId { get; private set; }

    /// <summary>The security that was a member. Never a company, never a symbol.</summary>
    public SecurityId SecurityId { get; private set; }

    /// <summary>
    /// The cohort cuts this member was present at, ascending and distinct.
    /// </summary>
    /// <remarks>
    /// Ordering is load-bearing: the derivation reads the first and the last, and compares the last
    /// to the universe's final cut to decide whether the member was still there when the cutting
    /// stopped.
    /// </remarks>
    public IReadOnlyList<DateOnly> CohortCuts => _cohortCuts;

    /// <summary>When this row was written here.</summary>
    public DateTime RecordedAtUtc { get; private set; }

    /// <summary>Records a membership. Empty cuts are refused rather than stored as "none".</summary>
    public static UniverseMembership Record(
        Guid id,
        UniverseId universeId,
        SecurityId securityId,
        IEnumerable<DateOnly> cohortCuts,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(universeId);
        ArgumentNullException.ThrowIfNull(cohortCuts);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (id == Guid.Empty)
        {
            throw new DomainValidationException(nameof(id), "A membership id is required.");
        }

        if (securityId.Value == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(securityId),
                "A membership must name the security that was a member.");
        }

        var cuts = cohortCuts.Distinct().OrderBy(d => d).ToList();

        if (cuts.Count == 0)
        {
            throw new DomainValidationException(
                nameof(cohortCuts),
                "A membership records the cohort cuts the member was present at. A member present "
                + "at no cut was not a member.");
        }

        return new UniverseMembership(id, universeId, securityId, cuts, nowUtc);
    }

    /// <summary>Whether this member was still present at the universe's final cut.</summary>
    /// <remarks>
    /// The one question the derivation asks of the cuts beyond their endpoints. Answered here rather
    /// than at each call site so that the comparison cannot drift between them.
    /// </remarks>
    public bool SurvivedTo(DateOnly finalCut) => _cohortCuts[^1] == finalCut;
}
