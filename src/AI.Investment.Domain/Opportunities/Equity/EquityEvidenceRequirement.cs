namespace AI.Investment.Domain.Opportunities.Equity;

/// <summary>
/// What must exist before an equity opportunity may leave <see cref="OpportunityStatus.Draft"/>.
/// </summary>
/// <remarks>
/// <para>
/// It reports every missing requirement rather than the first, because a candidate short of four
/// things needs a different response from one short of a single field, and only the full list
/// distinguishes them.
/// </para>
/// <para>
/// <strong>Registered per type, and an unregistered type is refused outright</strong> by the
/// workflow. That direction matters: it means adding a type without stating what it must prove
/// makes it unusable, rather than making it the one type nothing checks.
/// </para>
/// </remarks>
public sealed class EquityEvidenceRequirement : IEvidenceRequirement
{
    public OpportunityType Type => EquityOpportunity.Type;

    public IReadOnlyList<string> MissingRequirements(Opportunity opportunity)
    {
        ArgumentNullException.ThrowIfNull(opportunity);

        var missing = new List<string>();

        if (opportunity.Evidence.Count == 0)
        {
            missing.Add(
                "At least one evidence claim is required. A candidate resting on nothing cannot be " +
                "checked and would rank beside ones that can.");
        }

        if (!opportunity.Subject.IsSpecific)
        {
            missing.Add(
                "The subject must name a specific instrument. A sector-wide subject cannot be ordered, " +
                "position-sized or reconciled against a fill.");
        }

        foreach (var problem in EquityDetail.TryParse(opportunity.Detail, out _))
        {
            missing.Add($"Detail payload: {problem}");
        }

        return missing;
    }
}
