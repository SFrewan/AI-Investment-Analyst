namespace AI.Investment.Domain.Opportunities;

/// <summary>
/// Computes the economics block for one opportunity type, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// The second per-type interface, and the one carrying the architecture's firmest rule: profit,
/// margin and risk-adjusted return are this method's outputs and nothing else's inputs. An agent
/// may contribute an estimated sale price as a claim, with provenance and stated confidence; the
/// arithmetic on top of it is the system's, versioned, and reproducible from the same inputs.
/// </para>
/// <para>
/// Named <c>IOpportunityEconomicsCalculator</c> rather than the sketch's <c>IEconomicsCalculator</c>
/// because Phase 3 already has <c>IMetricCalculator</c> in the analytics namespace and a bare
/// "economics calculator" beside it reads as a sibling of that rather than as a member of the
/// opportunity trio.
/// </para>
/// </remarks>
public interface IOpportunityEconomicsCalculator
{
    OpportunityType Type { get; }

    /// <summary>The version of this calculation. Stored results are only comparable within one.</summary>
    Analytics.CalculationVersion Version { get; }

    /// <summary>
    /// Computes the economics for an opportunity from its detail payload and the evidence available.
    /// </summary>
    OpportunityEconomics Calculate(Opportunity opportunity, DateTime nowUtc);
}
