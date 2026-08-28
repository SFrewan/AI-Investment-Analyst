namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// Declares what must exist before an opportunity of one type may leave <c>Draft</c>.
/// </summary>
/// <remarks>
/// <para>
/// The third per-type interface, and the gate that stops a half-formed candidate reaching a ranking
/// list. Equities need financials, price history and at least one risk claim; a resale needs
/// supplier verification, a demand signal and a landed-cost quote. The core does not know either
/// list, and should not.
/// </para>
/// <para>
/// It returns reasons rather than a boolean. "Not ready" is not actionable; "missing a landed-cost
/// quote" is, and it is what a person looking at a stuck draft needs to read.
/// </para>
/// </remarks>
public interface IEvidenceRequirement
{
    OpportunityType Type { get; }

    /// <summary>
    /// What is still missing. An empty list means the opportunity may be evaluated.
    /// </summary>
    IReadOnlyList<string> MissingRequirements(Opportunity opportunity);
}
