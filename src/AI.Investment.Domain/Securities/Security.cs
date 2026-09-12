using AI.Investment.Domain.Common;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Securities;

/// <summary>
/// A tradable instrument issued by a company. Created once; everything that changes is an event.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is not.</strong> It is not a company - <c>Company</c> keeps legal identity, and
/// the Security Master explicitly does not own it. It is not a symbol: a ticker is a claim made by a
/// source over an interval, held in <see cref="Identifiers"/>, never as the thing itself. And it is
/// not venue-bound - where it trades is a <see cref="Listing"/>, because one instrument may be
/// admitted on several venues with different statuses at the same moment.
/// </para>
/// <para>
/// <strong>Why it carries so little.</strong> Every attribute that changes over time belongs in an
/// interval-scoped assertion or an event, not in a column. <c>Company</c> demonstrates the failure
/// this avoids: it holds a <c>Ticker</c> and an <c>Exchange</c> and rewrites them through
/// <c>ChangeListing</c>, so the previous value is gone and no historical question can be answered
/// from it. A security therefore has an identity, an issuer, and a record of when it was created -
/// and its state is read from history.
/// </para>
/// <para>
/// <strong>Identity is immutable.</strong> No operation changes the id or the issuer. A security
/// that turned out to be a different instrument was never this one.
/// </para>
/// </remarks>
public sealed class Security : AggregateRoot<SecurityId>
{
    private readonly List<SecurityIdentifier> _identifiers = [];

    private Security(SecurityId id, CompanyId companyId, DateTime createdAtUtc)
        : base(id)
    {
        CompanyId = companyId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Security()
    {
    }

    /// <summary>The legal entity that issued this instrument. Never changes.</summary>
    public CompanyId CompanyId { get; private set; }

    /// <summary>When this security was first recorded here. Not when it was issued.</summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Every identifier ever asserted for this security, each with its interval and its source.
    /// </summary>
    public IReadOnlyList<SecurityIdentifier> Identifiers => _identifiers;

    /// <summary>Creates a security. The issuer is required and is fixed from here on.</summary>
    /// <param name="nowUtc">
    /// Supplied by the caller. The domain does not read the clock - in a system that replays
    /// historical decisions, "now" is an input like any other.
    /// </param>
    public static Security Create(SecurityId id, CompanyId companyId, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (id.Value == Guid.Empty)
        {
            throw new DomainValidationException(nameof(id), "A security id is required.");
        }

        if (companyId.Value == Guid.Empty)
        {
            throw new DomainValidationException(
                nameof(companyId),
                "A security must name the company that issued it.");
        }

        return new Security(id, companyId, nowUtc);
    }

    /// <summary>
    /// Records that a source asserted an identifier for this security over an interval.
    /// </summary>
    /// <remarks>
    /// Append-only by construction: there is no operation that edits or removes an assertion. A
    /// source that later disagrees makes a new claim, and both are kept, because which one was
    /// believed on a past date is exactly the question this model exists to answer.
    /// </remarks>
    public void AssertIdentifier(SecurityIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (_identifiers.Contains(identifier))
        {
            return;
        }

        _identifiers.Add(identifier);
    }

    /// <summary>
    /// The identifiers of a given kind whose intervals cover <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// Returns every covering assertion rather than picking one. Two sources disagreeing about a
    /// symbol on a date is a real state of the evidence, and resolving it silently here would hide
    /// the disagreement from the caller that has to decide what to do about it.
    /// </remarks>
    public IReadOnlyList<SecurityIdentifier> IdentifiersAsAt(
        SecurityIdentifierKind kind,
        DateOnly asOf) =>
        _identifiers.Where(i => i.Kind == kind && i.CoversDate(asOf)).ToList();
}
