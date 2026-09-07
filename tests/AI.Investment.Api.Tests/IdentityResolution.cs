using System.Text.Json;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Which source wins when two of them name a ticker, and whether the winner can be priced.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from the stage that uses it, because these are the rules that decide whether
/// the platform buys the right security. A rule that only runs inside a database-backed,
/// environment-gated stage is a rule nobody exercises: it would be checked once, by hand, on the
/// day it was written. Here every branch is a fact that runs in the ordinary suite.
/// </para>
/// <para>
/// <strong>The failure this exists to prevent has a name.</strong> Bunge's normalised company name
/// matched exactly one delisted symbol, <c>BGEPF</c>, which is not the line anybody traded. Thirty-six
/// of a hundred and six such matches were later contradicted outright by EDGAR - preferred lines,
/// warrants, superseded symbols, successor companies. An exact name match names a symbol; it does
/// not establish that the symbol is the issuer's tradable security, and nothing here lets it
/// pretend otherwise.
/// </para>
/// </remarks>
internal static class IdentityResolution
{
    /// <summary>EDGAR and the delisted-list agree on the symbol.</summary>
    public const string Confirmed = "sec-confirmed";

    /// <summary>EDGAR names a different symbol. EDGAR wins; the other is kept as evidence.</summary>
    public const string Conflict = "sec-conflict";

    /// <summary>EDGAR names a symbol where nothing else did.</summary>
    public const string SecOnly = "sec-only";

    /// <summary>Only a delisted-list name match. Never authoritative.</summary>
    public const string Provisional = "eodhd-only-provisional";

    /// <summary>Several delisted symbols share the normalised name. Refused, not chosen.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>No source names a symbol. Still a member.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>
    /// The issuer's own filing named the symbol on its cover page.
    /// </summary>
    /// <remarks>
    /// Authoritative for the same reason <see cref="SecOnly"/> is, and arguably more so: it is the
    /// company's own statement in a document that cannot be withdrawn, and it survives the
    /// deregistration that strips EDGAR's ticker list. It is what recovered the members that died -
    /// Bed Bath &amp; Beyond under a successor shell's name, Meredith under an acquirer's - and it
    /// is admitted only after the security classification confirms the symbol names an equity.
    /// </remarks>
    public const string FilingRecovered = "sec-filing-recovered";

    /// <summary>
    /// The bare symbol, with the exchange suffix a vendor appends removed.
    /// </summary>
    /// <remarks>
    /// EODHD writes <c>DHIL.US</c> where EDGAR writes <c>DHIL</c>. Those are one ticker in two
    /// notations and reporting them as a disagreement would bury the real disagreements in noise.
    /// Only the suffix goes: <c>AIV-PA</c> keeps its class marker, because a preferred line and its
    /// common stock are genuinely different securities and collapsing them is the exact error this
    /// whole class exists to prevent.
    /// </remarks>
    public static string Symbol(string ticker)
    {
        ArgumentNullException.ThrowIfNull(ticker);

        var dot = ticker.LastIndexOf('.');

        return dot > 0 ? ticker[..dot] : ticker;
    }

    /// <summary>
    /// Decides the identity of one member from every source that named it.
    /// </summary>
    /// <param name="secTicker">What EDGAR's submissions document carries, or null.</param>
    /// <param name="provisionalTicker">A delisted-list name match, or null.</param>
    /// <param name="ambiguous">True when more than one delisted symbol shared the name.</param>
    /// <remarks>
    /// Total: every combination of inputs produces a status and a member. There is no path that
    /// returns nothing, because "we could not identify this company" is an outcome to record and
    /// never a reason to remove it from a sealed universe.
    /// </remarks>
    public static Resolution Resolve(
        string? secTicker,
        string? provisionalTicker,
        bool ambiguous = false)
    {
        var sec = Trim(secTicker);
        var provisional = Trim(provisionalTicker);

        if (sec is not null && provisional is not null)
        {
            return string.Equals(Symbol(sec), Symbol(provisional), StringComparison.OrdinalIgnoreCase)
                ? new Resolution(Confirmed, sec, provisional, null)
                : new Resolution(
                    Conflict,
                    sec,
                    provisional,
                    $"provisional `{provisional}` from a delisted-list name match; EDGAR says `{sec}`");
        }

        if (sec is not null)
        {
            return new Resolution(SecOnly, sec, null, null);
        }

        // No EDGAR ticker. Nothing below promotes a guess to an identity.
        if (ambiguous)
        {
            return new Resolution(
                Ambiguous,
                null,
                provisional,
                "more than one delisted symbol shares this normalised name; refused rather than chosen");
        }

        return provisional is not null
            ? new Resolution(Provisional, provisional, provisional, null)
            : new Resolution(Unmatched, null, null, null);
    }

    /// <summary>Whether a resolved identity is safe to fetch a price series against.</summary>
    /// <remarks>
    /// Deliberately conservative and deliberately ordered. A member with no symbol fails first
    /// because there is nothing to check; an ambiguous one fails before a provisional one because
    /// the ambiguity is the stronger statement; and a provisional one fails however plausible its
    /// name match looked, because plausibility is what produced <c>BGEPF</c>.
    /// </remarks>
    public static Readiness Assess(Resolution resolution, bool transportFailed)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (resolution.Status == Ambiguous)
        {
            return new Readiness(
                false,
                "the delisted-list name matched more than one symbol and no EDGAR ticker exists to " +
                "settle it; acquiring either would be a guess");
        }

        if (resolution.Ticker is null)
        {
            return new Readiness(
                false,
                transportFailed
                    ? "no symbol from any source, and EDGAR's submissions document could not be " +
                      "retrieved to supply one"
                    : "no symbol from any source");
        }

        if (resolution.Status == Provisional)
        {
            return new Readiness(
                false,
                "the symbol rests solely on an exact name match against the delisted symbol list, " +
                "which names a symbol without establishing it is the issuer's tradable line - the " +
                "error BGEPF stands for, and one that EDGAR contradicted in 36 of 106 such matches");
        }

        // sec-confirmed, sec-conflict and sec-only all rest on EDGAR's own submissions document,
        // which is the authority for who this issuer is and what it trades as.
        return new Readiness(true, null);
    }

    /// <summary>True when the status rests on EDGAR's own evidence.</summary>
    public static bool IsAuthoritative(string status) =>
        status is Confirmed or Conflict or SecOnly or FilingRecovered;

    /// <summary>
    /// What actually supplied the accepted symbol - never what was merely queried.
    /// </summary>
    /// <remarks>
    /// EDGAR is asked about every member, so "we called EDGAR" is true of all four hundred and
    /// says nothing. What a reader needs is which source produced the symbol that was accepted,
    /// because that is what decides whether the identity can be trusted. A member EDGAR answered
    /// without naming a ticker was queried, not sourced, and must not be labelled as though EDGAR
    /// supplied its identity.
    /// </remarks>
    public static string SymbolSource(string status, bool transportFailed)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (string.Equals(status, FilingRecovered, StringComparison.Ordinal))
        {
            return "SEC filing cover page, issuer-stated";
        }

        if (IsAuthoritative(status))
        {
            return "EDGAR submissions";
        }

        return status switch
        {
            Provisional => "EODHD delisted symbol list, exact name match",
            Ambiguous =>
                "EODHD delisted symbol list, ambiguous - candidates supplied, none accepted",
            _ => transportFailed
                ? "none - unresolved; EDGAR's submissions document could not be retrieved"
                : "none",
        };
    }

    /// <summary>
    /// True for a genuine two-source disagreement that EDGAR settled.
    /// </summary>
    /// <remarks>
    /// An ambiguity carries a conflict message too - more than one delisted symbol shared the
    /// name - but it is not a disagreement between sources: EDGAR named nothing, so there is no
    /// rejected symbol and no winner. Selecting the conflict table on "has a conflict message"
    /// therefore sweeps the ambiguous members in with blank symbol columns and inflates the count
    /// that matters. Selecting on the status does not.
    /// </remarks>
    public static bool IsConflict(string status) => status == Conflict;

    /// <summary>
    /// Reads the fingerprint a sealed manifest carries, and refuses one that is not the expected.
    /// </summary>
    /// <remarks>
    /// Comparing against a pinned literal rather than recomputing. Recomputing proves a file hashes
    /// to whatever it now contains, which is true of any file; comparing proves it is the file that
    /// was approved.
    /// </remarks>
    public static bool IsSealedAs(string manifestJson, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);

        using var document = JsonDocument.Parse(manifestJson);

        return document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("EvidenceBaseFingerprint", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expectedFingerprint, StringComparison.Ordinal);
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    /// <param name="Status">Which of the six outcomes this is.</param>
    /// <param name="Ticker">The symbol to use, or null when there is none to use.</param>
    /// <param name="PreviousTicker">What the other source said, kept whether or not it won.</param>
    /// <param name="Conflict">Stated when the two sources disagreed; null otherwise.</param>
    public sealed record Resolution(
        string Status,
        string? Ticker,
        string? PreviousTicker,
        string? Conflict);

    /// <param name="Ready">Whether a price series may be fetched against this member.</param>
    /// <param name="Reason">Why not, when not. Null exactly when ready.</param>
    public sealed record Readiness(bool Ready, string? Reason);
}
