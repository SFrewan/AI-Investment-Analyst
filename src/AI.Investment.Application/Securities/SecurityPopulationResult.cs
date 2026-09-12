using System.Collections.ObjectModel;
using AI.Investment.Domain.Securities;

namespace AI.Investment.Application.Securities;

/// <summary>What one population run did to one member.</summary>
public enum SecurityPopulationOutcome
{
    /// <summary>Unset. Present so the default value is representable and obviously wrong.</summary>
    Unknown = 0,

    /// <summary>A security and its identifier were created.</summary>
    Created = 1,

    /// <summary>
    /// The identifier already existed, so the security it belongs to already existed. Nothing was
    /// written. This is what a second run produces for everything the first run created.
    /// </summary>
    AlreadyPresent = 2,

    /// <summary>The evidence does not support creating a security. <see cref="SecurityPopulationRefusal"/> says why.</summary>
    Refused = 3,
}

/// <summary>One member's result, carrying enough to answer why the row exists or does not.</summary>
/// <param name="Cik">The member.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Refusal">The single reason, when refused.</param>
/// <param name="VendorSymbol">The identifier asserted or found, when there is one.</param>
/// <param name="IdentifierKind">
/// Which identifier system was asserted, when one was. Recorded rather than assumed so the census
/// can count kinds instead of stating a total it was told: a run that somehow created a ticker
/// assertion would show up here rather than behind a constant.
/// </param>
public sealed record SecurityPopulationEntry(
    string Cik,
    SecurityPopulationOutcome Outcome,
    SecurityPopulationRefusal Refusal,
    string? VendorSymbol,
    SecurityIdentifierKind? IdentifierKind = null);

/// <summary>
/// The census of one population run, derived from what the run actually did.
/// </summary>
/// <remarks>
/// <para>
/// Every count here is computed from <see cref="Entries"/> rather than accumulated as the run
/// proceeds, so a count and the entries behind it cannot disagree. A reader who distrusts a total
/// can re-derive it.
/// </para>
/// <para>
/// The declaration's identity and digest are carried so the census answers the provenance question
/// the architecture asks of any populated row: which sealed evidence produced this, and would the
/// same evidence produce it again.
/// </para>
/// </remarks>
public sealed class SecurityPopulationResult
{
    public SecurityPopulationResult(
        string declarationId,
        string identityDigest,
        IEnumerable<SecurityPopulationEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityDigest);
        ArgumentNullException.ThrowIfNull(entries);

        DeclarationId = declarationId;
        IdentityDigest = identityDigest;
        Entries = new ReadOnlyCollection<SecurityPopulationEntry>(entries.ToList());
    }

    /// <summary>Which sealed declaration this run read.</summary>
    public string DeclarationId { get; }

    /// <summary>The digest the declaration carried and the reader verified.</summary>
    public string IdentityDigest { get; }

    /// <summary>Every member, in declaration order. Nothing is dropped, including refusals.</summary>
    public IReadOnlyList<SecurityPopulationEntry> Entries { get; }

    public int MembersRead => Entries.Count;

    public int SecuritiesCreated => Count(SecurityPopulationOutcome.Created);

    public int AlreadyPresent => Count(SecurityPopulationOutcome.AlreadyPresent);

    public int Refused => Count(SecurityPopulationOutcome.Refused);

    /// <summary>How many identifier assertions this run created, by kind.</summary>
    /// <remarks>
    /// Counted from the entries rather than tallied as the run proceeds. Against the sealed
    /// declaration it contains one key, <c>VendorSymbol</c>; <c>Ticker</c> is absent because the
    /// declaration evidences no validity interval for one and an assertion requires a
    /// <c>ValidFrom</c>. The absence is visible here as an absence rather than asserted as a zero.
    /// </remarks>
    public IReadOnlyDictionary<SecurityIdentifierKind, int> IdentifiersCreatedByKind =>
        Entries
            .Where(e => e.Outcome == SecurityPopulationOutcome.Created && e.IdentifierKind.HasValue)
            .GroupBy(e => e.IdentifierKind!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Vendor-symbol assertions created: one per created security, and never more.</summary>
    public int VendorSymbolIdentifiersCreated => CountOfKind(SecurityIdentifierKind.VendorSymbol);

    /// <summary>
    /// Ticker assertions created. Zero against this declaration, and derived rather than declared,
    /// so that a run which created one could not report otherwise.
    /// </summary>
    public int TickerIdentifiersCreated => CountOfKind(SecurityIdentifierKind.Ticker);

    /// <summary>How many members each refusal accounted for.</summary>
    public IReadOnlyDictionary<SecurityPopulationRefusal, int> RefusalsByReason =>
        Entries
            .Where(e => e.Outcome == SecurityPopulationOutcome.Refused)
            .GroupBy(e => e.Refusal)
            .ToDictionary(g => g.Key, g => g.Count());

    private int Count(SecurityPopulationOutcome outcome) =>
        Entries.Count(e => e.Outcome == outcome);

    private int CountOfKind(SecurityIdentifierKind kind) =>
        Entries.Count(e =>
            e.Outcome == SecurityPopulationOutcome.Created && e.IdentifierKind == kind);
}
