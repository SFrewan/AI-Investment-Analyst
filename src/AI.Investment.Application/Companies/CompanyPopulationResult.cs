using System.Collections.ObjectModel;

namespace AI.Investment.Application.Companies;

/// <summary>What one population run did to one member.</summary>
public enum CompanyPopulationOutcome
{
    /// <summary>Unset. Present so the default value is representable and obviously wrong.</summary>
    Unknown = 0,

    /// <summary>A company was created.</summary>
    Created = 1,

    /// <summary>
    /// A company already held this CIK, so nothing was written. What a second run produces for
    /// everything the first created, and what a run over G2's post-state produces for its 77.
    /// </summary>
    AlreadyExisting = 2,

    /// <summary>The evidence does not support creating a company. <see cref="CompanyPopulationRejection"/> says why.</summary>
    Rejected = 3,
}

/// <summary>One member's result, carrying enough to answer why the row exists or does not.</summary>
/// <param name="Cik">The member.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Rejection">The single reason, when rejected.</param>
/// <param name="IssuerName">The name written or found, when there is one.</param>
public sealed record CompanyPopulationEntry(
    string Cik,
    CompanyPopulationOutcome Outcome,
    CompanyPopulationRejection Rejection,
    string? IssuerName);

/// <summary>
/// The census of one population run, derived from what the run actually did.
/// </summary>
/// <remarks>
/// Every count is computed from <see cref="Entries"/> rather than accumulated as the run proceeds,
/// so a total and the entries behind it cannot disagree. <see cref="FinalCompanies"/> is the one
/// figure that is not - it is read back from the repository after the commit, because the point of
/// reporting it is to say what the database holds rather than what the run believes it holds.
/// </remarks>
public sealed class CompanyPopulationResult
{
    public CompanyPopulationResult(
        string declarationId,
        string identityDigest,
        int finalCompanies,
        IEnumerable<CompanyPopulationEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDigest);
        ArgumentOutOfRangeException.ThrowIfNegative(finalCompanies);
        ArgumentNullException.ThrowIfNull(entries);

        DeclarationId = declarationId;
        IdentityDigest = identityDigest;
        FinalCompanies = finalCompanies;
        Entries = new ReadOnlyCollection<CompanyPopulationEntry>(entries.ToList());
    }

    /// <summary>Which sealed declaration this run read.</summary>
    public string DeclarationId { get; }

    /// <summary>The digest the declaration carried and the reader verified.</summary>
    public string IdentityDigest { get; }

    /// <summary>Companies in the database after the run, counted there rather than here.</summary>
    public int FinalCompanies { get; }

    /// <summary>Every member, in declaration order. Nothing is dropped, rejections included.</summary>
    public IReadOnlyList<CompanyPopulationEntry> Entries { get; }

    public int MembersRead => Entries.Count;

    public int Eligible => Entries.Count(e => e.Outcome != CompanyPopulationOutcome.Rejected);

    public int Created => Count(CompanyPopulationOutcome.Created);

    public int AlreadyExisting => Count(CompanyPopulationOutcome.AlreadyExisting);

    public int Rejected => Count(CompanyPopulationOutcome.Rejected);

    public int InvalidCik => CountRejection(CompanyPopulationRejection.InvalidCik);

    public int MissingName => CountRejection(CompanyPopulationRejection.MissingIssuerName);

    public int DuplicateInput => CountRejection(CompanyPopulationRejection.DuplicateCikInSource);

    /// <summary>How many members each rejection accounted for.</summary>
    public IReadOnlyDictionary<CompanyPopulationRejection, int> RejectionsByReason =>
        Entries
            .Where(e => e.Outcome == CompanyPopulationOutcome.Rejected)
            .GroupBy(e => e.Rejection)
            .ToDictionary(g => g.Key, g => g.Count());

    private int Count(CompanyPopulationOutcome outcome) =>
        Entries.Count(e => e.Outcome == outcome);

    private int CountRejection(CompanyPopulationRejection rejection) =>
        Entries.Count(e =>
            e.Outcome == CompanyPopulationOutcome.Rejected && e.Rejection == rejection);
}
