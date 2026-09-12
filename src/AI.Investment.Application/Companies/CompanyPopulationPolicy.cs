using System.Text.RegularExpressions;
using AI.Investment.Application.Securities;

namespace AI.Investment.Application.Companies;

/// <summary>Why a member of the sealed declaration may not become a company.</summary>
public enum CompanyPopulationRejection
{
    /// <summary>Not a rejection. Present so the default value is obviously wrong.</summary>
    None = 0,

    /// <summary>The CIK is absent or is not the canonical ten-digit SEC form.</summary>
    InvalidCik = 1,

    /// <summary>No issuer name. A company cannot be created without one, and none is invented.</summary>
    MissingIssuerName = 2,

    /// <summary>
    /// The same CIK appears twice in one declaration.
    /// </summary>
    /// <remarks>
    /// Refused rather than merged. Two records for one legal entity is a contradiction in the
    /// evidence, and picking whichever came first would resolve it by accident of ordering.
    /// </remarks>
    DuplicateCikInSource = 3,
}

/// <summary>What the policy decided about one member.</summary>
/// <param name="Cik">The member.</param>
/// <param name="IsEligible">Whether a company may be created from this evidence.</param>
/// <param name="Rejection">The single reason, when it may not.</param>
/// <param name="IssuerName">The name that will be written, when it may.</param>
public sealed record CompanyPopulationDecision(
    string Cik,
    bool IsEligible,
    CompanyPopulationRejection Rejection,
    string? IssuerName);

/// <summary>
/// Decides which members of the sealed identity declaration are legal entities the platform knows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three predicates, and deliberately no more.</strong> G3R established that the minimum
/// sufficient evidence for a company is an issuer name, plus a CIK where one exists - which the
/// domain already says, because <c>Company.Create</c> refuses to default exactly one parameter.
/// Requiring anything further would invent a stronger rule for the comfort of it, and would have
/// cost a real issuer its row.
/// </para>
/// <para>
/// <strong>Nothing about instruments appears here.</strong> No ticker, security, vendor symbol,
/// listing or venue, and not the declaration's identity class, resolution status, acquisition
/// readiness or ambiguity status either - those last four describe how well a member's *symbol* is
/// known, and a legal entity does not stop existing because nobody agreed on what it traded as.
/// Section 6.1 of the architecture makes a company "a legal entity" and explicitly "not tradable";
/// letting an instrument predicate in here would be anti-pattern 15 arriving from the other side.
/// </para>
/// <para>
/// <strong>Pure and total.</strong> Every input produces a decision carrying its reason, and the
/// duplicate check is the only one that depends on what came before it in the same population.
/// </para>
/// </remarks>
public static partial class CompanyPopulationPolicy
{
    /// <summary>
    /// Decides every member, in the order the declaration states them.
    /// </summary>
    /// <remarks>
    /// The whole population at once rather than member by member, because duplicate detection is a
    /// property of the population and cannot be decided from one record in isolation.
    /// </remarks>
    public static IReadOnlyList<CompanyPopulationDecision> Decide(
        IEnumerable<SecurityIdentityEvidence> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<CompanyPopulationDecision>();

        foreach (var member in members)
        {
            decisions.Add(Decide(member, seen));
        }

        return decisions;
    }

    private static CompanyPopulationDecision Decide(
        SecurityIdentityEvidence member,
        HashSet<string> seen)
    {
        ArgumentNullException.ThrowIfNull(member);

        if (member.Cik is not { } cik || !CanonicalCik().IsMatch(cik))
        {
            return Reject(member.Cik, CompanyPopulationRejection.InvalidCik);
        }

        if (string.IsNullOrWhiteSpace(member.IssuerName))
        {
            return Reject(cik, CompanyPopulationRejection.MissingIssuerName);
        }

        if (!seen.Add(cik))
        {
            return Reject(cik, CompanyPopulationRejection.DuplicateCikInSource);
        }

        return new CompanyPopulationDecision(
            cik,
            IsEligible: true,
            CompanyPopulationRejection.None,
            member.IssuerName.Trim());
    }

    private static CompanyPopulationDecision Reject(string? cik, CompanyPopulationRejection reason) =>
        new(cik ?? string.Empty, IsEligible: false, reason, IssuerName: null);

    /// <summary>
    /// The canonical SEC CIK: exactly ten decimal digits, leading zeros significant.
    /// </summary>
    /// <remarks>
    /// Matched rather than parsed. Parsing to a number and reformatting would accept
    /// <c>1503584</c> and silently pad it, which is how a CIK that was never canonical in the
    /// evidence becomes canonical in the database.
    /// </remarks>
    [GeneratedRegex(@"^[0-9]{10}$")]
    private static partial Regex CanonicalCik();
}
