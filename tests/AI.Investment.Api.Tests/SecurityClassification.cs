using System.Text.RegularExpressions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Whether a recovered identity names an equity a price feed could be asked for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The error this exists to stop.</strong> A 10-K cover page lists every security
/// registered under Section 12(b), and <c>dei:TradingSymbol</c> takes the first. For a debt-only
/// registrant that is a bond: Ford Motor Credit's filing yields <c>F/26AB</c> for its 2.386% Notes,
/// HSBC USA's yields <c>HUSI/43</c>. Both are issuer-stated and correctly extracted, and both are
/// the wrong security to price. This is the same failure as <c>BGEPF</c> - authoritative evidence
/// pointing at an instrument nobody meant - reached from the opposite direction.
/// </para>
/// <para>
/// <strong>Two independent signals, and refusal if either objects.</strong> The security title
/// says what the instrument is; the symbol's shape says what kind of symbol it is. A regex over the
/// ticker alone would have caught the three bond tickers by their slash and missed a debt security
/// that happened to have a clean symbol. The title alone would have missed <c>N/A</c>, which is not
/// a security at all. Requiring both to be free of objection is what makes this evidence-based
/// rather than a pattern that happened to fit four rows.
/// </para>
/// <para>
/// <strong>What absence of evidence does.</strong> A missing title is not treated as debt - three
/// genuine recoveries carry a symbol and no title, because a filer tagged one and not the other -
/// but it is recorded as a lower confidence rather than waved through. What refuses a row is
/// positive evidence of debt, or a symbol that is not a symbol. Silence is neither.
/// </para>
/// </remarks>
internal static class SecurityClassification
{
    /// <summary>An equity a US price feed can be asked for.</summary>
    public const string Equity = "equity";

    /// <summary>A note, debenture or bond. Issuer-stated, and not what anyone meant to price.</summary>
    public const string Debt = "debt";

    /// <summary>Not a usable symbol at all: a placeholder, or a shape no US equity has.</summary>
    public const string NotASymbol = "not-a-symbol";

    /// <summary>Values filers write where a symbol would go when there is none.</summary>
    private static readonly string[] Placeholders =
        ["N/A", "NA", "N.A.", "NONE", "N-A", "-", "--", "NOTAPPLICABLE"];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The ticker as it should be stored: trimmed and upper-cased, and nothing else.
    /// </summary>
    /// <remarks>
    /// A case fold, deliberately. <c>lgiq</c> and <c>LGIQ</c> are the same security written two
    /// ways - a filer's tag is not always upper-case - so folding them together loses nothing.
    /// Anything more than case and whitespace would be editing the evidence rather than tidying it,
    /// which is the line that separates normalisation from invention.
    /// </remarks>
    public static string Normalise(string? ticker) =>
        (ticker ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Whether normalising changed anything but case and surrounding whitespace.</summary>
    /// <remarks>
    /// Used as a test, not a guard: it is the property that makes <see cref="Normalise"/> safe to
    /// apply to evidence, and it should be impossible for it to fail.
    /// </remarks>
    public static bool IsCaseFoldOnly(string? ticker) =>
        string.Equals(
            Normalise(ticker),
            (ticker ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies one recovered identity from its symbol and the security title beside it.
    /// </summary>
    public static Classification Classify(string? ticker, string? securityTitle)
    {
        var symbol = Normalise(ticker);
        var title = Tidy(securityTitle);

        // Signal one: is this a symbol at all?
        if (symbol.Length == 0 || Placeholders.Contains(symbol, StringComparer.Ordinal))
        {
            return new Classification(
                NotASymbol, symbol, "none",
                "the filing states no usable trading symbol; the value is a placeholder");
        }

        // Signal two: does the title say this is debt? Checked before the symbol's shape, so a
        // bond with a tidy symbol is still refused for the right reason.
        var debt = DebtEvidence(title);

        if (debt is not null)
        {
            return new Classification(
                Debt, symbol, "none",
                "the security title states a debt instrument: " + debt);
        }

        if (!IsEquitySymbol(symbol))
        {
            return new Classification(
                NotASymbol, symbol, "none",
                "the symbol is not the shape of a US equity ticker, which is one to five letters "
                + "with no exchange or maturity suffix");
        }

        var equity = EquityEvidence(title);

        return new Classification(
            Equity,
            symbol,
            equity is null ? "moderate" : "high",
            equity is null
                ? "the symbol is a well-formed US equity ticker and the filing states no security "
                  + "title to corroborate it"
                : "the security title states an equity instrument: " + equity);
    }

    /// <summary>Whether a classification may be sent to a price provider.</summary>
    public static bool IsAcquirable(Classification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        return string.Equals(classification.Kind, Equity, StringComparison.Ordinal);
    }

    /// <summary>The phrase in the title that says this is debt, or null.</summary>
    private static string? DebtEvidence(string title)
    {
        if (title.Length == 0)
        {
            return null;
        }

        foreach (var (pattern, name) in new[]
        {
            ("\\b(?:senior|subordinated|convertible)?\\s*notes?\\b", "notes"),
            ("\\bdebentures?\\b", "debentures"),
            ("\\bbonds?\\b", "bonds"),
            ("\\b\\d+(?:\\.\\d+)?\\s*%", "a stated coupon rate"),
            ("\\bdue\\s+(?:\\w+\\s+\\d{1,2},?\\s*)?(?:19|20)\\d{2}\\b", "a stated maturity"),
            ("\\$\\s?[\\d,]{7,}", "a stated principal amount"),
            ("\\bcapital securit(?:y|ies)\\b", "capital securities"),
            ("\\btrust preferred\\b", "trust preferred securities"),
        })
        {
            if (Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase, Timeout))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The phrase in the title that says this is equity, or null.</summary>
    /// <remarks>
    /// Wider than "common stock" because the recovered set is not: a partnership files depositary
    /// or common units, a REIT files shares of beneficial interest, a foreign private issuer files
    /// ordinary shares, and several file a share class. All are equities a price feed can quote.
    /// </remarks>
    private static string? EquityEvidence(string title)
    {
        if (title.Length == 0)
        {
            return null;
        }

        foreach (var (pattern, name) in new[]
        {
            ("\\bcommon\\s+stock\\b", "common stock"),
            ("\\bcommon\\s+shares?\\b", "common shares"),
            ("\\bordinary\\s+shares?\\b", "ordinary shares"),
            ("\\bclass\\s+[A-Z]\\s+(?:common\\s+)?stock\\b", "a common share class"),
            ("\\bdepositary\\s+units?\\b", "depositary units"),
            ("\\bcommon\\s+units?\\b", "common units"),
            ("\\blimited\\s+partner(?:ship)?\\s+interests?\\b", "limited partnership interests"),
            ("\\bbeneficial\\s+interest\\b", "shares of beneficial interest"),
        })
        {
            if (Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase, Timeout))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>One to five letters. No slash, no digits, no suffix.</summary>
    /// <remarks>
    /// The slash is NYSE's notation for a debt or preferred line - <c>F/26AB</c>, <c>GM/26</c> -
    /// and a digit in a US equity ticker is rare enough that refusing it and reporting the refusal
    /// is safer than admitting a maturity code by accident.
    /// </remarks>
    private static bool IsEquitySymbol(string symbol) =>
        Regex.IsMatch(symbol, "^[A-Z]{1,5}$", RegexOptions.None, Timeout);

    /// <summary>
    /// The title with its markup artefacts removed, so a line break cannot hide "Common Stock".
    /// </summary>
    /// <remarks>
    /// Real titles arrive carrying <c>&amp;#160;</c>, newlines inside the phrase and doubled
    /// spaces, because they were extracted from inline XBRL wrapped in formatting. One filer's
    /// "Common\n    Stock" would otherwise fail a match that its neighbour passes, for no reason a
    /// reader would accept.
    /// </remarks>
    private static string Tidy(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = title
            .Replace("&#160;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace(' ', ' ');

        return Regex.Replace(text, "\\s+", " ", RegexOptions.None, Timeout).Trim();
    }

    /// <param name="Kind">Equity, debt, or not a symbol.</param>
    /// <param name="Symbol">The normalised ticker.</param>
    /// <param name="Confidence">High when the title corroborates, moderate when it is silent.</param>
    /// <param name="Evidence">The phrase that decided it, in the words a reader can check.</param>
    internal sealed record Classification(
        string Kind,
        string Symbol,
        string Confidence,
        string Evidence);
}
