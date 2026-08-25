namespace AI.Investment.Domain.Enums;

/// <summary>
/// The coarse-grained class of thing an action does. The primary key of every policy lookup.
/// </summary>
/// <remarks>
/// Capabilities are deliberately few and coarse. Fine-grained distinctions belong in
/// <c>ActionType</c>; policy is expressed per capability so that permissions stay reviewable
/// by a human rather than sprawling into hundreds of rows.
/// </remarks>
public enum Capability
{
    /// <summary>Managing reference data: companies, securities, exchanges, sectors.</summary>
    ReferenceDataManagement = 0,

    /// <summary>Fetching data from external providers. Costs money and consumes rate limits.</summary>
    DataIngestion = 1,

    /// <summary>Running analysis, including calls to AI providers. Costs money.</summary>
    Analysis = 2,

    /// <summary>Creating, ranking and closing opportunities.</summary>
    OpportunityManagement = 3,

    /// <summary>Creating and deciding approval requests.</summary>
    ApprovalAdministration = 4,

    /// <summary>Changing policies. An AI proposer is refused unconditionally.</summary>
    PolicyAdministration = 5,

    /// <summary>Changing autonomy grants. An AI proposer is refused unconditionally.</summary>
    AutonomyAdministration = 6,

    /// <summary>Moving real money. Refused unconditionally until the execution plane exists.</summary>
    FinancialExecution = 7,
}
