namespace AI.Investment.Application.Securities;

/// <summary>Why a member did not become a security. Exactly one reason, never a default.</summary>
public enum SecurityPopulationRefusal
{
    /// <summary>Not a refusal. Present so the default value is obviously wrong.</summary>
    None = 0,

    /// <summary>
    /// EDGAR named a ticker for the issuer, but nothing named the instrument. Issuer identity is
    /// not security identity: one company may issue several securities.
    /// </summary>
    IssuerIdentityOnly = 1,

    /// <summary>
    /// The only symbol claim is a vendor delisted-list name match, which the precedence chain never
    /// promotes. This is the error BGEPF stands for.
    /// </summary>
    VendorSymbolClaimOnly = 2,

    /// <summary>No source named a symbol at all.</summary>
    NoSecurityEvidence = 3,

    /// <summary>
    /// More than one candidate and nothing to settle it. Refused rather than chosen, and it stays
    /// refused: there is no fallback to a current ticker, a company name, or a database uniqueness
    /// accident.
    /// </summary>
    UnresolvedAmbiguity = 4,

    /// <summary>
    /// A filing was read and its security title says the symbol does not name a tradable equity -
    /// notes, or a placeholder that is not a symbol at all.
    /// </summary>
    SecurityTitleIsNotEquity = 5,

    /// <summary>
    /// The issuer named the instrument, but no identifier assertion can be persisted for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is not a survivorship exclusion, and the difference matters.</strong> The
    /// member is not refused for having no prices; it is refused because
    /// <c>SecurityIdentifier</c> requires a <c>ValidFrom</c>, the declaration evidences no interval
    /// for any ticker, and the vendor supplies no series whose extent could bound one. A security
    /// created here would carry no identifier at all: nothing could name it, look it up, or tell a
    /// second run that it already exists.
    /// </para>
    /// <para>
    /// A dated symbol-change source would make these members creatable with a ticker assertion and
    /// no vendor series whatever. The exclusion is about what can be asserted, not about what
    /// survived.
    /// </para>
    /// </remarks>
    NoPersistableIdentifier = 6,
}

/// <summary>What the policy decided about one member, and why.</summary>
/// <param name="Cik">The member.</param>
/// <param name="IsEligible">Whether a security may be created from this evidence.</param>
/// <param name="Refusal">The single reason, when it may not.</param>
/// <param name="VendorSymbol">The identifier that will be asserted, when it may.</param>
public sealed record SecurityPopulationDecision(
    string Cik,
    bool IsEligible,
    SecurityPopulationRefusal Refusal,
    VendorSeriesAssertion? VendorSymbol);

/// <summary>
/// Decides which members of the sealed identity declaration may become securities.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure, and separate from the writer that uses it.</strong> These are the rules that
/// decide whether the platform creates an instrument, so every branch is a fact the ordinary suite
/// exercises rather than something checked once by hand against a database.
/// </para>
/// <para>
/// <strong>Two conditions, and both are necessary.</strong> The first asks whether the evidence
/// names an instrument: only an issuer's own filing does that, and only when its security title
/// says the line is a tradable equity. The second asks whether the platform can name what it
/// creates: an identifier assertion needs an interval, and the only interval the declaration
/// evidences is the extent of a vendor series.
/// </para>
/// <para>
/// <strong>Nothing here guesses.</strong> There is no fallback to a current ticker, a company name,
/// a CIK, an observation symbol, or a uniqueness accident in the database. A member the evidence
/// cannot place stays unplaced and carries its reason.
/// </para>
/// </remarks>
public static class SecurityPopulationPolicy
{
    /// <summary>Decides one member. Total: every input produces a decision with a stated reason.</summary>
    public static SecurityPopulationDecision Decide(SecurityIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        // Ambiguity is checked first because it is the strongest statement in the evidence: the
        // sources named several candidates and none was accepted. It is deliberately not the same
        // as having no candidate at all - conflating them would report the 27 members nobody named
        // as though several sources had disagreed about them, and would hide how many identities
        // the evidence actually refuses to settle.
        if (string.Equals(
                evidence.AmbiguityStatus,
                SecurityIdentityEvidence.RefusedMultipleCandidates,
                StringComparison.Ordinal))
        {
            return Refuse(evidence, SecurityPopulationRefusal.UnresolvedAmbiguity);
        }

        if (evidence.Symbol is not { } symbol)
        {
            return Refuse(evidence, SecurityPopulationRefusal.NoSecurityEvidence);
        }

        if (!string.Equals(evidence.AmbiguityStatus, SecurityIdentityEvidence.Unambiguous, StringComparison.Ordinal))
        {
            // A symbol alongside any other ambiguity status is a shape the sealed declaration does
            // not produce. Refused rather than interpreted: an unrecognised ambiguity marker is
            // exactly the thing that must not be read as "no ambiguity".
            return Refuse(evidence, SecurityPopulationRefusal.UnresolvedAmbiguity);
        }

        if (!string.Equals(
                evidence.IdentityClass,
                SecurityIdentityEvidence.IssuerStatedSecurityIdentity,
                StringComparison.Ordinal))
        {
            // Each of the other classes is refused for its own reason, and the reasons are not
            // interchangeable: a vendor name match is a claim nobody authoritative made, while an
            // EDGAR ticker is a real claim about the wrong thing - an issuer, not an instrument.
            return Refuse(
                evidence,
                symbol.IsIssuerStated
                    ? SecurityPopulationRefusal.IssuerIdentityOnly
                    : SecurityPopulationRefusal.VendorSymbolClaimOnly);
        }

        if (!symbol.IsAccepted)
        {
            // Unreachable against the sealed declaration, where every class-one symbol is
            // accepted. Kept because the class and the acceptance are separate assertions in the
            // evidence, and a future declaration could carry one without the other.
            return Refuse(evidence, SecurityPopulationRefusal.NoSecurityEvidence);
        }

        if (!string.Equals(symbol.SecurityTitleObserved, SecurityIdentityEvidence.EquityTitle, StringComparison.Ordinal))
        {
            return Refuse(evidence, SecurityPopulationRefusal.SecurityTitleIsNotEquity);
        }

        if (evidence.VendorSeries is not { } series)
        {
            return Refuse(evidence, SecurityPopulationRefusal.NoPersistableIdentifier);
        }

        return new SecurityPopulationDecision(
            evidence.Cik,
            IsEligible: true,
            SecurityPopulationRefusal.None,
            series);
    }

    /// <summary>Decides every member, in the order the declaration presents them.</summary>
    public static IReadOnlyList<SecurityPopulationDecision> Decide(
        IEnumerable<SecurityIdentityEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return evidence.Select(Decide).ToList();
    }

    private static SecurityPopulationDecision Refuse(
        SecurityIdentityEvidence evidence,
        SecurityPopulationRefusal refusal) =>
        new(evidence.Cik, IsEligible: false, refusal, VendorSymbol: null);
}
