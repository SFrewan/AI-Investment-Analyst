namespace AI.Investment.Application.Securities;

/// <summary>
/// One member's identity evidence, as the sealed D6 declaration states it.
/// </summary>
/// <remarks>
/// <para>
/// A transport shape, not a domain concept. It carries what the declaration says and nothing it
/// does not say, so that the decision about what may be created is taken against the evidence
/// rather than against a convenient summary of it.
/// </para>
/// <para>
/// <strong>The two identifier blocks are deliberately separate.</strong> An issuer-stated symbol
/// and a vendor series key are different claims by different parties, and the declaration keeps
/// them apart for the reason D5 recorded: the vendor back-fills a full history under the issuer's
/// current key, so a vendor interval is evidence about the vendor's series and never about what
/// traded on a date.
/// </para>
/// </remarks>
/// <param name="Cik">The ten-digit CIK, as the declaration states it.</param>
/// <param name="IssuerName">The issuer name the identity evidence carries.</param>
/// <param name="IdentityClass">
/// One of the declaration's five classes. Only <c>issuer-stated-security-identity</c> names an
/// instrument; the others name an issuer, a vendor claim, an absence, or a refusal.
/// </param>
/// <param name="ResolutionStatus">The resolution status the precedence chain produced.</param>
/// <param name="AmbiguityStatus">
/// <c>none</c>, <c>refused-multiple-candidates</c> or <c>no-candidate</c>. Never resolved here.
/// </param>
/// <param name="Symbol">The symbol assertion, or null when no source named one.</param>
/// <param name="VendorSeries">The vendor series key and its extent, or null when none is held.</param>
public sealed record SecurityIdentityEvidence(
    string Cik,
    string? IssuerName,
    string IdentityClass,
    string ResolutionStatus,
    string AmbiguityStatus,
    SymbolAssertion? Symbol,
    VendorSeriesAssertion? VendorSeries)
{
    /// <summary>
    /// The candidate an authoritative source contradicted, kept rather than deleted.
    /// </summary>
    /// <remarks>
    /// It takes no part in the population decision - the accepted symbol does that. It is carried
    /// because it is part of the sealed canonical form the digest covers, and because a rejected
    /// symbol may have been correct at another time; discarding it would make that unanswerable.
    /// </remarks>
    public string? RejectedSymbol { get; init; }

    /// <summary>The one class that names an instrument rather than an issuer.</summary>
    public const string IssuerStatedSecurityIdentity = "issuer-stated-security-identity";

    /// <summary>No ambiguity was recorded for this member.</summary>
    public const string Unambiguous = "none";

    /// <summary>
    /// Several candidates were named and none was accepted.
    /// </summary>
    /// <remarks>
    /// Distinct from having no candidate at all, and the declaration keeps the two apart: 6 members
    /// are refused this way and 27 were never named by anybody. One says the evidence disagrees
    /// with itself; the other says the evidence is silent.
    /// </remarks>
    public const string RefusedMultipleCandidates = "refused-multiple-candidates";

    /// <summary>
    /// The security title the issuer's filing carried for a tradable equity line.
    /// </summary>
    /// <remarks>
    /// Named as a constant because it is a gate, not a label: a title of <c>debt</c> or
    /// <c>not-a-symbol</c> is evidence that the symbol does not name a tradable equity, and the
    /// declaration records three members of the first kind and one of the second.
    /// </remarks>
    public const string EquityTitle = "equity";
}

/// <summary>A symbol claim, with who made it and whether the chain accepted it.</summary>
/// <param name="Value">The canonical symbol, suffix stripped, class markers preserved.</param>
/// <param name="SourceAuthority">Which source stated it.</param>
/// <param name="IsIssuerStated">Whether the issuer stated it, directly or through EDGAR.</param>
/// <param name="IsAccepted">Whether the precedence chain accepted it.</param>
/// <param name="SecurityTitleObserved">
/// The security title a filing cover page carried, where one was read. Null for the 310 members
/// where no title was recovered - and null is not a statement that the line is an equity.
/// </param>
/// <param name="ValidityIntervalEvidenced">
/// Whether the evidence dates the symbol. False for every member of the sealed declaration, which
/// is why no ticker assertion may be persisted from it.
/// </param>
/// <param name="ValidFrom">
/// First date the claim covers, when the evidence dates it. Null for every member of both sealed
/// declarations, and read explicitly rather than assumed: a reader that ignored the field could not
/// tell an absent interval from one it forgot to look at.
/// </param>
/// <param name="ValidTo">Last date covered, or null when no end is evidenced.</param>
public sealed record SymbolAssertion(
    string Value,
    string SourceAuthority,
    bool IsIssuerStated,
    bool IsAccepted,
    string? SecurityTitleObserved,
    bool ValidityIntervalEvidenced,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null);

/// <summary>
/// A vendor series key and the extent of the series held under it.
/// </summary>
/// <remarks>
/// <see cref="FirstBarDate"/> and <see cref="LastBarDate"/> bound the vendor's own series. They are
/// a true statement about what the vendor published and are never a claim that the instrument
/// carried this symbol on those dates.
/// </remarks>
/// <param name="Value">The vendor key, suffix retained, because the suffix is part of the key.</param>
/// <param name="SourceAuthority">The vendor that published the series.</param>
/// <param name="FirstBarDate">First bar the vendor supplies under this key.</param>
/// <param name="LastBarDate">Last bar the vendor supplies under this key.</param>
public sealed record VendorSeriesAssertion(
    string Value,
    string SourceAuthority,
    DateOnly FirstBarDate,
    DateOnly LastBarDate);
