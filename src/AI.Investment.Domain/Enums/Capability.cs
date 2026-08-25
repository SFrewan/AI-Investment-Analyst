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

    /// <summary>
    /// Deleting archived evidence because a source's licence requires it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own capability because it is the one routine operation in this platform that destroys
    /// evidence. Routing it through the seam means a deletion is proposed, policy-evaluated,
    /// executed and audited like any other side effect - and, because a capability with no
    /// configured policy is denied, an installation that has not deliberately enabled retention
    /// enforcement deletes nothing at all.
    /// </para>
    /// <para>
    /// Distinct from <see cref="DataIngestion"/> on purpose. Permission to fetch data is not
    /// permission to destroy it, and a single capability covering both would grant the second
    /// every time someone wanted the first.
    /// </para>
    /// </remarks>
    DataRetention = 8,
}
