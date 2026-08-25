namespace AI.Investment.Application.Companies.CreateCompany;

/// <summary>What happened when a company creation was requested.</summary>
public enum CreateCompanyStatus
{
    /// <summary>Policy permitted it and the company was persisted.</summary>
    Created = 0,

    /// <summary>A company with this ticker already exists. Nothing was written.</summary>
    AlreadyExists = 1,

    /// <summary>Policy requires a human decision. Nothing was written.</summary>
    ApprovalRequired = 2,

    /// <summary>Policy refused it. Nothing was written.</summary>
    Denied = 3,

    /// <summary>A previous identical request already performed this. Nothing was written again.</summary>
    DuplicateSuppressed = 4,
}

/// <summary>
/// Outcome of a creation request, including the policy reason.
/// </summary>
/// <remarks>
/// The reason is surfaced rather than hidden behind a generic failure, because "the kill switch
/// is engaged" and "reference data management is disabled in this environment" are things an
/// operator needs to see, not debug.
/// </remarks>
public sealed record CreateCompanyResult(
    CreateCompanyStatus Status,
    CompanyDto? Company,
    string Reason,
    Guid? DecisionId);
