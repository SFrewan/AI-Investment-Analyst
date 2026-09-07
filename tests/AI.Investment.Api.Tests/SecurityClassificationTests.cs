using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The equity classifier, tested against the four rows that made it necessary.
/// </summary>
/// <remarks>
/// The recovery produced 89 issuer-authoritative identities, of which four named something other
/// than a tradable equity. All four were correct readings of the filing and all four would have
/// been wrong to price. These facts pin both halves: the four are refused, and the awkward genuine
/// ones - a partnership's units, a REIT's beneficial interest, a foreign issuer's ordinary shares,
/// a title split across a line break - are not.
/// </remarks>
public sealed class SecurityClassificationTests
{
    /// <summary>The three debt tickers the cover pages really did state.</summary>
    [Theory]
    [InlineData("F/26AB", "2.386% Notes due February 17, 2026*")]
    [InlineData("HUSI/43", "$100,000,000 Zero Coupon Callable Accreting Notes due January 15, 2043")]
    [InlineData("GM/26", "5.250% Senior Notes due 2026")]
    public void A_debt_security_is_refused_however_authoritative_the_evidence(
        string ticker, string title)
    {
        var classification = SecurityClassification.Classify(ticker, title);

        Assert.Equal(SecurityClassification.Debt, classification.Kind);
        Assert.False(SecurityClassification.IsAcquirable(classification));

        // Refused on the title, not merely on the slash in the symbol: a bond with a tidy ticker
        // would be refused for the same stated reason.
        Assert.Contains("debt instrument", classification.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_where_a_symbol_should_be_is_not_a_symbol()
    {
        foreach (var placeholder in new[] { "N/A", "n/a", "NONE", "-", " ", "" })
        {
            var classification = SecurityClassification.Classify(placeholder, null);

            Assert.Equal(SecurityClassification.NotASymbol, classification.Kind);
            Assert.False(SecurityClassification.IsAcquirable(classification));
        }
    }

    /// <summary>
    /// The debt test fires before the shape test, so the reason given is the real one.
    /// </summary>
    [Fact]
    public void A_debt_security_with_a_tidy_symbol_is_still_refused_as_debt()
    {
        var classification = SecurityClassification.Classify("ABCD", "4.500% Senior Notes due 2031");

        Assert.Equal(SecurityClassification.Debt, classification.Kind);
        Assert.False(SecurityClassification.IsAcquirable(classification));

        // The evidence names whichever debt marker matched first - here the instrument noun,
        // not the coupon. Asserting the specific phrase would pin the order of the checks
        // rather than the thing that matters, which is that it was refused as debt.
        Assert.Contains("debt instrument", classification.Evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every equity shape the recovery actually produced, none of which may be refused.
    /// </summary>
    [Theory]
    [InlineData("AXDX", "Common Stock, $0.001 par value per share")]
    [InlineData("MANT", "Class A Common Stock")]
    [InlineData("FUN", "Depositary Units (RepresentingLimited Partner Interests)")]
    [InlineData("CCF", "Common stock, $.10 par value")]
    [InlineData("EVK", "Common Stock")]
    [InlineData("BBBY", "Common stock, $.01 par value")]
    [InlineData("MDP", "Common Stock, par value $1")]
    [InlineData("PDCO", "Common Stock, par value $.01")]
    public void Every_equity_the_recovery_found_is_acquirable(string ticker, string title)
    {
        var classification = SecurityClassification.Classify(ticker, title);

        Assert.Equal(SecurityClassification.Equity, classification.Kind);
        Assert.True(SecurityClassification.IsAcquirable(classification));
        Assert.Equal("high", classification.Confidence);
    }

    /// <summary>
    /// The awkward ones: units, ordinary shares, beneficial interest, and a broken line.
    /// </summary>
    /// <remarks>
    /// A rule that only knew "Common Stock" would have thrown away a partnership, a REIT and a
    /// foreign private issuer - and would have refused Clearday for a newline inside the phrase.
    /// Each of these is a real row from the recovery.
    /// </remarks>
    [Theory]
    [InlineData("Ordinary Shares, nominal value $0.01 per share")]
    [InlineData("Ordinary shares, par value ILS 3.00 per share")]
    [InlineData("Common units representing limited partnership interests")]
    [InlineData("Common Units, Representing Limited Partner Interests")]
    [InlineData("Common shares, no par value per share")]
    [InlineData("Common Shares of Beneficial Interest, $0.01 par value")]
    [InlineData("Common\n    Stock, $0.001 par value")]
    [InlineData("Common Stock, par value $0.01&#160;per share")]
    [InlineData("Class A Stock, $.01 par value")]
    public void An_equity_that_is_not_called_common_stock_is_still_an_equity(string title)
    {
        var classification = SecurityClassification.Classify("XYZ", title);

        Assert.Equal(SecurityClassification.Equity, classification.Kind);
        Assert.True(SecurityClassification.IsAcquirable(classification));
    }

    /// <summary>
    /// Silence is not evidence of debt, and it is not waved through either.
    /// </summary>
    /// <remarks>
    /// Three recoveries carry a symbol and no security title, because the filer tagged one and not
    /// the other. Refusing them would discard genuine identities; accepting them silently would
    /// hide that they rest on one signal instead of two. They are accepted and marked.
    /// </remarks>
    [Fact]
    public void A_symbol_with_no_title_is_accepted_at_a_lower_confidence()
    {
        foreach (var ticker in new[] { "MSOF", "LGIQ" })
        {
            var classification = SecurityClassification.Classify(ticker, null);

            Assert.Equal(SecurityClassification.Equity, classification.Kind);
            Assert.True(SecurityClassification.IsAcquirable(classification));

            // Marked, so a reader can see which identities rest on one signal.
            Assert.Equal("moderate", classification.Confidence);
            Assert.Contains("no security title", classification.Evidence, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Normalisation folds case and nothing else.
    /// </summary>
    [Fact]
    public void Normalising_a_ticker_changes_its_case_and_never_its_identity()
    {
        Assert.Equal("LGIQ", SecurityClassification.Normalise("lgiq"));
        Assert.Equal("LGIQ", SecurityClassification.Normalise("  lgiq  "));

        // Already-clean tickers are untouched, so normalisation cannot churn the table.
        Assert.Equal("AXDX", SecurityClassification.Normalise("AXDX"));

        // Idempotent: applying it twice is applying it once.
        Assert.Equal(
            SecurityClassification.Normalise("lgiq"),
            SecurityClassification.Normalise(SecurityClassification.Normalise("lgiq")));

        // The property that makes it safe on evidence: only case and edges moved.
        foreach (var ticker in new[] { "lgiq", "AXDX", " fun ", "F/26AB", "N/A" })
        {
            Assert.True(SecurityClassification.IsCaseFoldOnly(ticker));
            Assert.Equal(ticker.Trim().Length, SecurityClassification.Normalise(ticker).Length);
        }

        // And it does not turn a refused symbol into an accepted one.
        Assert.Equal(
            SecurityClassification.NotASymbol,
            SecurityClassification.Classify("n/a", null).Kind);
        Assert.Equal(
            SecurityClassification.Equity,
            SecurityClassification.Classify("lgiq", null).Kind);
    }

    /// <summary>Every classification is exactly one kind, and only one of them can be priced.</summary>
    [Fact]
    public void The_classification_is_total_and_only_equity_is_acquirable()
    {
        var cases = new[]
        {
            SecurityClassification.Classify("AXDX", "Common Stock"),
            SecurityClassification.Classify("F/26AB", "2.386% Notes due 2026"),
            SecurityClassification.Classify("N/A", null),
            SecurityClassification.Classify("MSOF", null),
        };

        Assert.All(cases, c => Assert.Contains(
            c.Kind,
            new[]
            {
                SecurityClassification.Equity,
                SecurityClassification.Debt,
                SecurityClassification.NotASymbol,
            },
            StringComparer.Ordinal));

        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Evidence)));
        Assert.Equal(2, cases.Count(SecurityClassification.IsAcquirable));
    }
}
